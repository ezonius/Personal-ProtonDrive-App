using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using PdSync.Cli;
using PdSync.Config;
using PdSync.Exec;
using PdSync.State;

namespace PdSync.Tests;

/// <summary>
/// `pdsync trash list|purge` (SPEC §3.1, §7, D11). Local trash retention is
/// manual only; these tests pin the read-only list, the age cutoff, active-run
/// protection, and the path-safety boundary.
/// </summary>
public sealed class TrashCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private PdsyncConfig LoadConfig(string? extraSync = null) =>
        ConfigTestHelper.Load(_temp, ConfigTestHelper.MinimalYaml(_temp, extraSync: extraSync));

    private string SyncId(PdsyncConfig config, SyncConfig sync) =>
        StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot);

    private static string RunPath(PdsyncConfig config, string syncId, long runId) =>
        Path.Combine(config.StateDir, "trash", syncId, runId.ToString(CultureInfo.InvariantCulture));

    private static string CreateRun(
        PdsyncConfig config,
        string syncId,
        long runId,
        DateTimeOffset created,
        bool manifest = true,
        params (string RelativePath, string Content)[] files)
    {
        var directory = RunPath(config, syncId, runId);
        Directory.CreateDirectory(directory);
        foreach (var (relativePath, content) in files)
        {
            var path = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        if (manifest)
            File.WriteAllText(Path.Combine(directory, "manifest.json"), "[]");
        Directory.SetCreationTimeUtc(directory, created.UtcDateTime);
        return directory;
    }

    private CliResult Cli(string[] args, string input = "")
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliApp.Run(args, stdout, stderr, desktopMappings: null, input: new StringReader(input));
        return new CliResult(exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void ListJson_ReportsStableShape_ForRunsWithAndWithoutManifest()
    {
        var config = LoadConfig();
        var syncId = SyncId(config, config.Syncs[0]);
        CreateRun(config, syncId, 7, DateTimeOffset.UtcNow.AddDays(-2), manifest: true,
            ("a.txt", "abc"), ("sub/b.txt", "de"));
        CreateRun(config, syncId, 8, DateTimeOffset.UtcNow.AddDays(-1), manifest: false,
            ("c.txt", "f"));

        var result = Cli(new[] { "trash", "list", "--config", config.ConfigPath, "--json" });

        result.ExitCode.Should().Be(0, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        var rows = document.RootElement.EnumerateArray().ToList();
        rows.Should().HaveCount(2);

        var first = rows[0];
        first.GetProperty("syncId").GetString().Should().Be(syncId);
        first.GetProperty("runId").GetInt64().Should().Be(7);
        first.GetProperty("createdAt").GetDateTimeOffset().Should().BeCloseTo(
            DateTimeOffset.UtcNow.AddDays(-2), TimeSpan.FromMinutes(5));
        first.GetProperty("fileCount").GetInt64().Should().Be(2);
        first.GetProperty("bytes").GetInt64().Should().Be(5);
        first.GetProperty("hasManifest").GetBoolean().Should().BeTrue();
        first.TryGetProperty("path", out _).Should().BeFalse();

        var second = rows[1];
        second.GetProperty("runId").GetInt64().Should().Be(8);
        second.GetProperty("hasManifest").GetBoolean().Should().BeFalse();
        second.GetProperty("fileCount").GetInt64().Should().Be(1);
        second.GetProperty("bytes").GetInt64().Should().Be(1);
    }

    [Fact]
    public void List_IsReadOnly_WhenStateDirectoryDoesNotExist()
    {
        var config = LoadConfig();
        Directory.Exists(config.StateDir).Should().BeFalse();

        var result = Cli(new[] { "trash", "list", "--config", config.ConfigPath, "--json" });

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("[]");
        Directory.Exists(config.StateDir).Should().BeFalse();
        Directory.Exists(Path.Combine(config.StateDir, "trash")).Should().BeFalse();
    }

    [Fact]
    public void List_HonorsSyncFilterAndHumanOutputShowsManifestStatus()
    {
        Directory.CreateDirectory(_temp.Combine("local2"));
        var config = LoadConfig(extraSync: $"""
              photos:
                local: "{_temp.YamlPath("local2")}"
                remote: "/my-files/photos"
            """);
        var docsId = SyncId(config, config.Syncs[0]);
        var photosId = SyncId(config, config.Syncs[1]);
        CreateRun(config, docsId, 1, DateTimeOffset.UtcNow.AddHours(-2));
        CreateRun(config, photosId, 2, DateTimeOffset.UtcNow.AddHours(-1));

        var filtered = Cli(new[]
        {
            "trash", "list", "--config", config.ConfigPath, "--sync", "docs", "--json",
        });
        filtered.ExitCode.Should().Be(0, filtered.Stderr);
        using (var document = JsonDocument.Parse(filtered.Stdout))
        {
            var rows = document.RootElement.EnumerateArray().ToList();
            rows.Should().ContainSingle();
            rows[0].GetProperty("syncId").GetString().Should().Be(docsId);
        }

        var all = Cli(new[] { "trash", "list", "--config", config.ConfigPath });
        all.ExitCode.Should().Be(0, all.Stderr);
        all.Stdout.Should().Contain("2 run(s)");
        all.Stdout.Should().Contain("manifest: yes");
        all.Stdout.Should().Contain("(docs)");
        all.Stdout.Should().Contain("(photos)");
    }

    [Fact]
    public void Purge_RemovesOnlyRunsOlderThanCutoff()
    {
        var config = LoadConfig();
        var syncId = SyncId(config, config.Syncs[0]);
        var oldRun = CreateRun(config, syncId, 1, DateTimeOffset.UtcNow.AddDays(-10));
        var newRun = CreateRun(config, syncId, 2, DateTimeOffset.UtcNow.AddDays(-1));

        var result = Cli(new[]
        {
            "trash", "purge", "--config", config.ConfigPath, "--older-than", "5", "--yes",
        });

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("removed 1 run(s)");
        Directory.Exists(oldRun).Should().BeFalse();
        Directory.Exists(newRun).Should().BeTrue();
    }

    [Fact]
    public void Purge_DryRun_RemovesNothing()
    {
        var config = LoadConfig();
        var syncId = SyncId(config, config.Syncs[0]);
        var oldRun = CreateRun(config, syncId, 1, DateTimeOffset.UtcNow.AddDays(-10));

        var result = Cli(new[]
        {
            "trash", "purge", "--config", config.ConfigPath, "--older-than", "5", "--dry-run",
        });

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("would remove");
        result.Stdout.Should().Contain("would be removed");
        Directory.Exists(oldRun).Should().BeTrue();
    }

    [Fact]
    public void Purge_SkipsRunDirectoryWithActiveSyncLock()
    {
        var config = LoadConfig();
        var syncId = SyncId(config, config.Syncs[0]);
        var oldRun = CreateRun(config, syncId, 1, DateTimeOffset.UtcNow.AddDays(-10));
        var lockDirectory = Path.Combine(config.StateDir, "locks");
        Directory.CreateDirectory(lockDirectory);
        File.WriteAllText(Path.Combine(lockDirectory, syncId + ".lock"), "pid=1\n");

        var result = Cli(new[]
        {
            "trash", "purge", "--config", config.ConfigPath, "--older-than", "5", "--yes",
        });

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("skip active");
        Directory.Exists(oldRun).Should().BeTrue();
    }

    [Fact]
    public void Purge_WithoutManifest_StillDeletesWholeRunDirectory()
    {
        var config = LoadConfig();
        var syncId = SyncId(config, config.Syncs[0]);
        var oldRun = CreateRun(config, syncId, 1, DateTimeOffset.UtcNow.AddDays(-10),
            manifest: false, ("lost.txt", "x"));

        var list = Cli(new[] { "trash", "list", "--config", config.ConfigPath, "--json" });
        using (var document = JsonDocument.Parse(list.Stdout))
            document.RootElement[0].GetProperty("hasManifest").GetBoolean().Should().BeFalse();

        var result = Cli(new[]
        {
            "trash", "purge", "--config", config.ConfigPath, "--older-than", "5", "--yes",
        });

        result.ExitCode.Should().Be(0, result.Stderr);
        Directory.Exists(oldRun).Should().BeFalse();
    }

    [Fact]
    public void Purge_RequiresConfirmation_AndCancelRemovesNothing()
    {
        var config = LoadConfig();
        var syncId = SyncId(config, config.Syncs[0]);
        var oldRun = CreateRun(config, syncId, 1, DateTimeOffset.UtcNow.AddDays(-10));

        var declined = Cli(new[] { "trash", "purge", "--config", config.ConfigPath, "--older-than", "5" }, "n\n");
        declined.ExitCode.Should().Be(0, declined.Stderr);
        declined.Stdout.Should().Contain("cancelled");
        Directory.Exists(oldRun).Should().BeTrue();

        var accepted = Cli(new[] { "trash", "purge", "--config", config.ConfigPath, "--older-than", "5" }, "yes\n");
        accepted.ExitCode.Should().Be(0, accepted.Stderr);
        Directory.Exists(oldRun).Should().BeFalse();
    }

    [Fact]
    public void Purge_ClosedInput_CancelsInsteadOfDeleting()
    {
        var config = LoadConfig();
        var syncId = SyncId(config, config.Syncs[0]);
        var oldRun = CreateRun(config, syncId, 1, DateTimeOffset.UtcNow.AddDays(-10));

        var result = Cli(new[] { "trash", "purge", "--config", config.ConfigPath, "--older-than", "5" }, string.Empty);

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("cancelled");
        Directory.Exists(oldRun).Should().BeTrue();
    }

    [Fact]
    public void Purge_RefusesPathEscapeAttempts()
    {
        var config = LoadConfig();
        var trashRoot = LocalTrashIndex.TrashRoot(config);

        var parentEscape = () => LocalTrashIndex.ResolveRunDirectory(trashRoot, "..", "5");
        parentEscape.Should().Throw<SafetyException>();

        var runEscape = () => LocalTrashIndex.ResolveRunDirectory(trashRoot, "abc", "..");
        runEscape.Should().Throw<SafetyException>();

        var rootedRun = () => LocalTrashIndex.ResolveRunDirectory(trashRoot, "abc", Path.Combine(_temp.Root, "outside"));
        rootedRun.Should().Throw<SafetyException>();

        var valid = LocalTrashIndex.ResolveRunDirectory(trashRoot, "abc", "5");
        valid.Should().Be(Path.Combine(trashRoot, "abc", "5"));
    }

    [Fact]
    public void Purge_EmptyTrash_DoesNothing()
    {
        var config = LoadConfig();

        var result = Cli(new[]
        {
            "trash", "purge", "--config", config.ConfigPath, "--older-than", "5", "--yes", "--json",
        });

        result.ExitCode.Should().Be(0, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        document.RootElement.GetProperty("removed").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Purge_RequiresOlderThanOption()
    {
        var config = LoadConfig();

        var result = Cli(new[] { "trash", "purge", "--config", config.ConfigPath, "--yes" });

        result.ExitCode.Should().Be(1);
        result.Stderr.Should().Contain("--older-than");
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
