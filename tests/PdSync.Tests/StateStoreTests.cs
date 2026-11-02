using FluentAssertions;
using Microsoft.Data.Sqlite;
using PdSync.Config;
using PdSync.State;
using Xunit;

namespace PdSync.Tests;

public sealed class StateStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private (PdsyncConfig Config, SyncConfig Sync) CreateConfig(string name = "docs")
    {
        var local = _temp.Combine("local");
        Directory.CreateDirectory(local);

        var yaml = $"""
            version: 1
            stateDir: "{_temp.YamlPath("state")}"
            syncs:
              {name}:
                local: "{_temp.YamlPath("local")}"
                remote: "/my-files/docs"
            """;

        var config = ConfigTestHelper.Load(_temp, yaml);
        return (config, config.Syncs.Single());
    }

    private static EntryRecord SampleEntry(string key = "a.txt") => new(
        PathKey: key,
        Path: key,
        Kind: EntryKind.File,
        Size: 12,
        MtimeMs: 1700000000123,
        Sha1: "abc123",
        RemoteUid: "uid-1",
        RemoteRevUid: "rev-1",
        RemoteSize: 90,
        RemoteMtimeMs: 1700000000456,
        RemoteSha1: "abc123",
        LocalPresent: true,
        RemotePresent: true,
        Frozen: false,
        LastSyncedAt: DateTimeOffset.Parse("2024-01-02T03:04:05.678Z"));

    [Fact]
    public void Open_CreatesSchemaAndSyncRow()
    {
        var (config, sync) = CreateConfig();

        using var store = StateStore.Open(config, sync);

        File.Exists(store.DatabasePath).Should().BeTrue();
        store.DatabasePath.Should().StartWith(_temp.Combine("state", "state"));

        store.LoadEntries().Should().BeEmpty();
        store.CountEntries().Should().Be(0);
        store.LoadPending().Should().BeEmpty();
        store.LoadRuns().Should().BeEmpty();

        using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(command.ExecuteScalar()).Should().Be(StateStore.SchemaVersion);

        command.CommandText = "SELECT name, local_root, remote_root FROM syncs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", store.SyncId);
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        reader.GetString(0).Should().Be("docs");
        reader.GetString(1).Should().Be(sync.LocalRoot);
        reader.GetString(2).Should().Be(sync.RemoteRoot);
    }

    [Fact]
    public void Upsert_RoundTripsAllFields()
    {
        var (config, sync) = CreateConfig();
        using var store = StateStore.Open(config, sync);
        var entry = SampleEntry();

        store.UpsertEntry(entry);

        store.GetEntry(entry.PathKey).Should().Be(entry);
    }

    [Fact]
    public void Upsert_ReplacesExistingRow()
    {
        var (config, sync) = CreateConfig();
        using var store = StateStore.Open(config, sync);
        var entry = SampleEntry();

        store.UpsertEntry(entry);
        var updated = entry with
        {
            Size = 99,
            Sha1 = "def456",
            RemoteRevUid = "rev-2",
            RemotePresent = false,
            LastSyncedAt = DateTimeOffset.Parse("2024-02-03T04:05:06.789Z"),
        };
        store.UpsertEntry(updated);

        store.CountEntries().Should().Be(1);
        store.GetEntry(entry.PathKey).Should().Be(updated);
    }

    [Fact]
    public void GetEntry_ReturnsNullForUnknownKey()
    {
        var (config, sync) = CreateConfig();
        using var store = StateStore.Open(config, sync);

        store.GetEntry("missing").Should().BeNull();
    }

    [Fact]
    public void MarkFrozen_HidesEntryFromActiveState()
    {
        var (config, sync) = CreateConfig();
        using var store = StateStore.Open(config, sync);
        var a = SampleEntry("a.txt");
        var b = SampleEntry("b.txt");
        store.UpsertEntry(a);
        store.UpsertEntry(b);

        store.MarkFrozen(b.PathKey);

        store.LoadEntries().Should().ContainSingle().Which.Should().Be(a);
        store.CountEntries().Should().Be(1);

        var frozen = store.GetEntry(b.PathKey);
        frozen.Should().NotBeNull();
        frozen!.Frozen.Should().BeTrue();
        frozen.LocalPresent.Should().BeFalse();
        frozen.RemotePresent.Should().BeFalse();

        store.LoadEntries(includeFrozen: true).Should().HaveCount(2);
        store.CountEntries(includeFrozen: true).Should().Be(2);
    }

    [Fact]
    public void DeleteEntry_RemovesRow()
    {
        var (config, sync) = CreateConfig();
        using var store = StateStore.Open(config, sync);
        store.UpsertEntry(SampleEntry());

        store.DeleteEntry("a.txt");

        store.GetEntry("a.txt").Should().BeNull();
        store.CountEntries(includeFrozen: true).Should().Be(0);
    }

    [Fact]
    public void Pending_AddLoadRemove()
    {
        var (config, sync) = CreateConfig();
        using var store = StateStore.Open(config, sync);

        var id = store.AddPending("upload", "a.txt", """{"strategy":"new"}""");
        var pending = store.LoadPending();

        pending.Should().ContainSingle();
        pending[0].Id.Should().Be(id);
        pending[0].Op.Should().Be("upload");
        pending[0].Path.Should().Be("a.txt");
        pending[0].PayloadJson.Should().Be("""{"strategy":"new"}""");

        store.RemovePending(id);
        store.LoadPending().Should().BeEmpty();

        store.AddPending("delete", "b.txt");
        store.ClearPending().Should().Be(1);
        store.LoadPending().Should().BeEmpty();
    }

    [Fact]
    public void Runs_StartAndFinish()
    {
        var (config, sync) = CreateConfig();
        using var store = StateStore.Open(config, sync);

        var id = store.StartRun();
        store.FinishRun(id, "success", """{"uploaded":1}""");

        var run = store.LoadRuns().Should().ContainSingle().Subject;
        run.Id.Should().Be(id);
        run.Status.Should().Be("success");
        run.FinishedAt.Should().NotBeNull();
        run.StatsJson.Should().Be("""{"uploaded":1}""");
    }

    [Fact]
    public void Transaction_RollsBackOnException()
    {
        var (config, sync) = CreateConfig();
        using var store = StateStore.Open(config, sync);
        var entry = SampleEntry();

        var act = () => store.InTransaction(() =>
        {
            store.UpsertEntry(entry);
            throw new InvalidOperationException("boom");
        });

        act.Should().Throw<InvalidOperationException>();
        store.GetEntry(entry.PathKey).Should().BeNull();
    }

    [Fact]
    public void Transaction_CommitsOnSuccess()
    {
        var (config, sync) = CreateConfig();
        using var store = StateStore.Open(config, sync);
        var entry = SampleEntry();

        store.InTransaction(() =>
        {
            store.UpsertEntry(entry);
            store.AddPending("upload", entry.Path);
        });

        store.GetEntry(entry.PathKey).Should().Be(entry);
        store.LoadPending().Should().ContainSingle();
    }

    [Fact]
    public void NestedTransaction_IsRejected()
    {
        var (config, sync) = CreateConfig();
        using var store = StateStore.Open(config, sync);

        var act = () => store.InTransaction(() => store.InTransaction(() => { }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Nested*");
    }

    [Fact]
    public void Reopen_PersistsEntriesAndPendingRows()
    {
        var (config, sync) = CreateConfig();
        var entry = SampleEntry();

        var store = StateStore.Open(config, sync);
        store.UpsertEntry(entry);
        var pendingId = store.AddPending("upload", entry.Path);
        store.Dispose();

        using var reopened = StateStore.Open(config, sync);
        reopened.GetEntry(entry.PathKey).Should().Be(entry);
        reopened.LoadPending().Should().ContainSingle().Which.Id.Should().Be(pendingId);
    }

    [Fact]
    public void RenamingSync_KeepsTheSameDatabase()
    {
        var (config, sync) = CreateConfig("docs");

        var first = StateStore.Open(config, sync);
        var path = first.DatabasePath;
        first.UpsertEntry(SampleEntry());
        first.Dispose();

        var (renamedConfig, renamedSync) = CreateConfig("renamed");
        using var renamed = StateStore.Open(renamedConfig, renamedSync);

        renamed.DatabasePath.Should().Be(path);
        renamed.SyncName.Should().Be("renamed");
        renamed.GetEntry("a.txt").Should().NotBeNull();
    }

    [Fact]
    public void OpenReadOnly_OnMissingDatabase_UsesEmptyInMemoryBaseline()
    {
        var (config, sync) = CreateConfig();
        var expectedPath = Path.Combine(
            config.StateDir,
            "state",
            StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot) + ".sqlite");

        using var store = StateStore.OpenReadOnly(config, sync);

        store.IsReadOnly.Should().BeTrue();
        store.IsInMemory.Should().BeTrue();
        store.DatabasePath.Should().Be(expectedPath);
        File.Exists(expectedPath).Should().BeFalse();
        store.LoadEntries().Should().BeEmpty();
    }

    [Fact]
    public void OpenReadOnly_OnExistingDatabase_ReadsEntries()
    {
        var (config, sync) = CreateConfig();
        var entry = SampleEntry();

        var writer = StateStore.Open(config, sync);
        writer.UpsertEntry(entry);
        writer.Dispose();

        using var reader = StateStore.OpenReadOnly(config, sync);

        reader.IsReadOnly.Should().BeTrue();
        reader.IsInMemory.Should().BeFalse();
        reader.GetEntry(entry.PathKey).Should().Be(entry);
    }

    [Fact]
    public void OpenReadOnly_DoesNotMigrateAnOldSchema()
    {
        var (config, sync) = CreateConfig();
        var writer = StateStore.Open(config, sync);
        var path = writer.DatabasePath;
        writer.Dispose();

        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 0;";
            command.ExecuteNonQuery();
        }

        var act = () => StateStore.OpenReadOnly(config, sync);
        act.Should().Throw<StateSchemaMismatchException>()
            .WithMessage("*not current*run pdsync run to migrate it*");
    }

    [Fact]
    public void ComputeSyncId_IsStableAndDistinct()
    {
        var local = _temp.Combine("local");
        var a = StateStore.ComputeSyncId(local, "/my-files/docs");
        var b = StateStore.ComputeSyncId(local, "/my-files/docs");
        var c = StateStore.ComputeSyncId(local, "/my-files/other");

        a.Should().Be(b);
        a.Should().NotBe(c);
        a.Should().HaveLength(16);
    }

    [Fact]
    public void StateStore_RejectsNewerSchema()
    {
        var (config, sync) = CreateConfig();
        var store = StateStore.Open(config, sync);
        var path = store.DatabasePath;
        store.Dispose();

        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var act = () => StateStore.Open(config, sync);
        act.Should().Throw<StateSchemaTooNewException>()
            .WithMessage("*newer than this build*upgrade pdsync*");

        var readOnlyAct = () => StateStore.OpenReadOnly(config, sync);
        readOnlyAct.Should().Throw<StateSchemaTooNewException>()
            .WithMessage("*newer than this build*upgrade pdsync*");
    }
}
