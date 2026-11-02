using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using PdSync.Cli;
using PdSync.Diagnostics;
using PdSync.State;

namespace PdSync.Tests;

[Collection("fake-proton-cli")]
public sealed class CliFakeTests
{
    [Fact]
    public void Plan_WritesNothing()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");

        var result = fixture.Cli("plan");

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("upload new a.txt");
        Directory.Exists(Path.Combine(fixture.StateDir, "state")).Should().BeFalse();
        fixture.RemoteFiles().Should().BeEmpty();
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("run")]
    public void MalformedIgnoreFile_ReturnsConfigExit4(string command)
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal(".pdignore", "[");

        var result = fixture.Cli(command);

        result.ExitCode.Should().Be(4, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("config:");
        result.Stderr.Should().Contain("invalid ignore pattern");
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("run")]
    public void MalformedInlineExclude_ReturnsConfigExit4(string command)
    {
        using var fixture = new Fixture(exclude: "[");
        fixture.SeedRoot();

        var result = fixture.Cli(command);

        result.ExitCode.Should().Be(4, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("config:");
        result.Stderr.Should().Contain("invalid ignore pattern");
    }

    [Fact]
    public void Run_PushesCreatesAndSecondRunIsNoOp()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("docs/a.txt", "alpha");

        var first = fixture.Cli("run");
        first.ExitCode.Should().Be(0, first.Stderr);
        fixture.RemoteFile("/my-files/root/docs/a.txt").Should().Be("alpha");
        Directory.GetFiles(Path.Combine(fixture.StateDir, "state"), "*.sqlite")
            .Should().NotBeEmpty();

        var uploads = fixture.InvocationCount("upload");
        var second = fixture.Cli("run");
        second.ExitCode.Should().Be(0, second.Stderr);
        fixture.InvocationCount("upload").Should().Be(uploads);
    }

    [Fact]
    public void Run_PullsRemoteFile()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("remote.txt", "from remote");

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(0, result.Stderr);
        File.ReadAllText(fixture.LocalFull("remote.txt")).Should().Be("from remote");
    }

    [Fact]
    public void Run_RemoteDeletionMovesLocalFileToTrash()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.Cli("run").ExitCode.Should().Be(0);

        fixture.Fake("fs", "trash", "/my-files/root/a.txt");
        var result = fixture.Cli("run", "--force");

        result.ExitCode.Should().Be(0, result.Stderr);
        File.Exists(fixture.LocalFull("a.txt")).Should().BeFalse();
        Directory.GetFiles(Path.Combine(fixture.StateDir, "trash"), "a.txt", SearchOption.AllDirectories)
            .Should().ContainSingle();
    }

    [Fact]
    public void Run_LocalDeletionTrashesRemoteFile()
    {
        using var fixture = new Fixture(safety: "maxDeletes: 10\n      maxDeletePercent: 100");
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.WriteLocal("b.txt", "beta");
        fixture.Cli("run").ExitCode.Should().Be(0);

        File.Delete(fixture.LocalFull("a.txt"));
        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(0, result.Stderr);
        fixture.IsTrashed("/my-files/root/a.txt").Should().BeTrue();
        fixture.IsTrashed("/my-files/root/b.txt").Should().BeFalse();
    }

    [Fact]
    public void Run_DeletionBudgetTripsBeforeAnyDelete()
    {
        using var fixture = new Fixture(safety: "maxDeletes: 1\n      maxDeletePercent: 100");
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.WriteLocal("b.txt", "beta");
        fixture.WriteLocal("c.txt", "gamma");
        fixture.Cli("run").ExitCode.Should().Be(0);

        File.Delete(fixture.LocalFull("a.txt"));
        File.Delete(fixture.LocalFull("b.txt"));
        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(6);
        result.Stderr.Should().Contain("deletion budget");
        fixture.IsTrashed("/my-files/root/a.txt").Should().BeFalse();
        fixture.IsTrashed("/my-files/root/b.txt").Should().BeFalse();
    }

    [Fact]
    public void FastMode_SkipsRemoteChangesInUnchangedSubtree_UntilFullScan()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFolder("sub");
        fixture.WriteLocal("sub/a.txt", "alpha");
        fixture.Cli("run", "--full").ExitCode.Should().Be(0);

        var source = fixture.Temp.Combine("new.txt");
        File.WriteAllText(source, "from remote");
        fixture.Fake("fs", "upload", "--json", "--file-conflict-strategy", "create-new-revision",
            source, "/my-files/root/sub");

        var fast = fixture.Cli("run", "--fast");
        fast.ExitCode.Should().Be(0, fast.Stderr);
        File.Exists(fixture.LocalFull("sub/new.txt")).Should().BeFalse();

        var full = fixture.Cli("run", "--full");
        full.ExitCode.Should().Be(0, full.Stderr);
        File.Exists(fixture.LocalFull("sub/new.txt")).Should().BeTrue();
    }

    [Fact]
    public void InitialConflict_KeepBothKeepsLocalAndRemote()
    {
        using var fixture = new Fixture(defaults: "initialSync: keep-both");
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "remote");
        fixture.WriteLocal("a.txt", "local");

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(0, result.Stderr);
        File.ReadAllText(fixture.LocalFull("a.txt")).Should().Be("remote");
        Directory.GetFiles(fixture.Local, "a*.txt").Should().HaveCount(2);
        fixture.RemoteFileNames().Should().HaveCount(2);
    }

    [Fact]
    public void FailedUpload_IsNotCommittedToState()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");

        var scenario = fixture.Temp.Write("scenario.json", """
            {
              "rules": [
                { "match": "fs upload", "times": 1, "exitCode": 1, "stderr": "injected failure" }
              ]
            }
            """);
        fixture.Fake("__fake", "scenario", scenario);

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(1);
        fixture.RemoteFiles().Should().BeEmpty();
        // A pre-transfer upload throw must not trigger same-run cleanup.
        fixture.InvocationCount("trash").Should().Be(0);

        var dbPath = Directory.GetFiles(Path.Combine(fixture.StateDir, "state"), "*.sqlite").Single();
        using var state = PdSync.State.StateStore.OpenReadOnly(fixture.Config, fixture.Config.Syncs.Single());
        state.CountEntries(includeFrozen: true).Should().Be(0);
        File.Exists(dbPath).Should().BeTrue();
    }

    [Fact]
    public void Run_RemoteRenameMovesLocalFile()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "alpha");
        fixture.Cli("run").ExitCode.Should().Be(0);

        fixture.Fake("fs", "rename", "/my-files/root/a.txt", "b.txt");
        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(0, result.Stderr);
        File.Exists(fixture.LocalFull("a.txt")).Should().BeFalse();
        File.ReadAllText(fixture.LocalFull("b.txt")).Should().Be("alpha");
    }

    [Fact]
    public void Run_LocalRenameMovesRemoteFile()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.Cli("run").ExitCode.Should().Be(0);

        File.Move(fixture.LocalFull("a.txt"), fixture.LocalFull("b.txt"));
        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(0, result.Stderr);
        fixture.IsTrashed("/my-files/root/a.txt").Should().BeFalse();
        fixture.RemoteFile("/my-files/root/b.txt").Should().Be("alpha");
    }

    [Fact]
    public void Run_TruncatedDownloadLeavesLocalUntouched()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "alpha");
        var scenario = fixture.Temp.Write("scenario.json", """
            { "rules": [ { "match": "fs download", "times": 1, "truncateDownload": true } ] }
            """);
        fixture.Fake("__fake", "scenario", scenario);

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(1);
        File.Exists(fixture.LocalFull("a.txt")).Should().BeFalse();
    }

    [Fact]
    public void PruneEmptyRemoteDirs_RemovesEmptyIgnoredRemoteFolder()
    {
        using var fixture = new Fixture(safety: "maxDeletes: 10\n      maxDeletePercent: 100");
        fixture.SeedRoot();
        fixture.SeedFolder("empty");
        fixture.WriteLocal(".pdignore", "empty/\n");
        fixture.WriteLocal("a.txt", "alpha");
        fixture.WriteLocal("b.txt", "beta");
        fixture.Cli("run").ExitCode.Should().Be(0);

        File.Delete(fixture.LocalFull("a.txt"));
        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(0, result.Stderr);
        fixture.IsTrashed("/my-files/root/empty").Should().BeTrue();
    }

    [Fact]
    public void FullVerification_DownloadsBackUploads()
    {
        using var fixture = new Fixture(defaults: "verify:\n  mode: full");
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(0, result.Stderr);
        fixture.InvocationCount("download").Should().BeGreaterThan(0);
        fixture.RemoteFile("/my-files/root/a.txt").Should().Be("alpha");
    }

    [Fact]
    public void Run_WritesJsonlRunLog()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(0, result.Stderr);
        var logFiles = RunLogFiles(fixture.StateDir);
        logFiles.Should().ContainSingle();
        var events = ReadRunLog(logFiles.Single());
        events.Select(e => e.GetProperty("event").GetString()).Should().Contain(
            new[] { "run_start", "action", "verification", "run_finish" });

        var start = events.Single(e => e.GetProperty("event").GetString() == "run_start");
        start.GetProperty("sync").GetString().Should().Be("docs");
        start.GetProperty("runId").GetInt64().Should().BePositive();

        var action = events.Single(e =>
            e.GetProperty("event").GetString() == "action" &&
            e.GetProperty("path").GetString() == "a.txt");
        action.GetProperty("kind").GetString().Should().Be("UploadCreate");
        action.GetProperty("outcome").GetString().Should().Be("ok");
        action.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        action.GetProperty("durationMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);

        var verification = events.Single(e => e.GetProperty("event").GetString() == "verification");
        verification.GetProperty("result").GetString().Should().Be("ok");
        verification.GetProperty("method").GetString().Should().Be("size+sha1");

        var finish = events.Single(e => e.GetProperty("event").GetString() == "run_finish");
        finish.GetProperty("status").GetString().Should().Be("ok");
        finish.GetProperty("succeeded").GetInt32().Should().Be(1);
        finish.GetProperty("failed").GetInt32().Should().Be(0);
        finish.GetProperty("skipped").GetInt32().Should().Be(0);
        finish.GetProperty("durationMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);

        using var state = PdSync.State.StateStore.OpenReadOnly(
            fixture.Config, fixture.Config.Syncs.Single());
        state.LoadRuns(1).Single().StatsJson.Should().Contain("\"logFile\"");
    }

    [Fact]
    public void Plan_DoesNotCreateRunLogs()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");

        var firstPlan = fixture.Cli("plan");
        firstPlan.ExitCode.Should().Be(0, firstPlan.Stderr);
        Directory.Exists(Path.Combine(fixture.StateDir, "logs")).Should().BeFalse();

        fixture.Cli("run").ExitCode.Should().Be(0);
        var logsBefore = RunLogFiles(fixture.StateDir).Length;

        fixture.WriteLocal("b.txt", "beta");
        var result = fixture.Cli("plan");

        result.ExitCode.Should().Be(0, result.Stderr);
        RunLogFiles(fixture.StateDir).Should().HaveCount(logsBefore);
    }

    [Fact]
    public void Run_JsonStaysValidWhenLogDirIsUnwritable()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        File.WriteAllText(Path.Combine(fixture.StateDir, "logs"), "not a directory");

        var result = fixture.Cli("run", "--json");

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stderr.Should().Contain("could not write run log");
        using var document = JsonDocument.Parse(result.Stdout);
        document.RootElement.GetProperty("command").GetString().Should().Be("run");
    }

    [Fact]
    public void Run_LogsPartialFailure()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.WriteLocal("b.txt", "beta");
        var scenario = fixture.Temp.Write("scenario.json", """
            { "rules": [ { "match": "fs upload", "times": 1, "exitCode": 1, "stderr": "injected failure" } ] }
            """);
        fixture.Fake("__fake", "scenario", scenario);

        var result = fixture.Cli("run", "--json");

        result.ExitCode.Should().Be(1);
        using var document = JsonDocument.Parse(result.Stdout);
        document.RootElement.GetProperty("command").GetString().Should().Be("run");
        var logFiles = RunLogFiles(fixture.StateDir);
        logFiles.Should().ContainSingle();
        var events = ReadRunLog(logFiles.Single());
        var failed = events.Single(e =>
            e.GetProperty("event").GetString() == "action" &&
            e.GetProperty("outcome").GetString() == "failed");
        failed.GetProperty("error").GetString().Should().Contain("injected failure");

        var finish = events.Single(e => e.GetProperty("event").GetString() == "run_finish");
        finish.GetProperty("status").GetString().Should().Be("partial");
        finish.GetProperty("succeeded").GetInt32().Should().Be(1);
        finish.GetProperty("failed").GetInt32().Should().Be(1);
    }

    [Fact]
    public void Run_LogsFailedRun()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.WriteLocal("b.txt", "beta");
        var scenario = fixture.Temp.Write("scenario.json", """
            { "rules": [ { "match": "b.txt", "times": 1, "authExpired": true } ] }
            """);
        fixture.Fake("__fake", "scenario", scenario);

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(3);
        var logFiles = RunLogFiles(fixture.StateDir);
        logFiles.Should().ContainSingle();
        var events = ReadRunLog(logFiles.Single());
        var failedAction = events.Single(e =>
            e.GetProperty("event").GetString() == "action" &&
            e.GetProperty("path").GetString() == "b.txt");
        failedAction.GetProperty("outcome").GetString().Should().Be("failed");

        var finish = events.Single(e => e.GetProperty("event").GetString() == "run_finish");
        finish.GetProperty("status").GetString().Should().Be("failed");
        finish.GetProperty("succeeded").GetInt32().Should().Be(1);
        finish.GetProperty("failed").GetInt32().Should().Be(1);
        finish.GetProperty("error").GetString().Should().Contain("not authenticated");

        using var state = PdSync.State.StateStore.OpenReadOnly(
            fixture.Config, fixture.Config.Syncs.Single());
        var stats = state.LoadRuns(1).Single().StatsJson;
        stats.Should().Contain("\"logFile\"").And.Contain("\"failed\":1");
    }

    [Fact]
    public void History_ShowsBothRunsNewestFirst()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.Cli("run").ExitCode.Should().Be(0);
        fixture.WriteLocal("a.txt", "alpha2");
        fixture.Cli("run").ExitCode.Should().Be(0);

        using var state = StateStore.OpenReadOnly(fixture.Config, fixture.Config.Syncs.Single());
        var ids = state.LoadRuns(10).Select(run => run.Id).ToList();
        ids.Should().HaveCount(2);

        var result = fixture.Cli("history");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain($"run {ids[0]}");
        result.Stdout.Should().Contain($"run {ids[1]}");
        result.Stdout.IndexOf($"run {ids[0]}", StringComparison.Ordinal)
            .Should().BeLessThan(result.Stdout.IndexOf($"run {ids[1]}", StringComparison.Ordinal));
        result.Stdout.Should().Contain("ok");
        result.Stdout.Should().Contain("planned");
    }

    [Fact]
    public void History_LimitReturnsExactlyOneRun()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.Cli("run").ExitCode.Should().Be(0);
        fixture.WriteLocal("a.txt", "alpha2");
        fixture.Cli("run").ExitCode.Should().Be(0);

        foreach (var args in new[] { new[] { "history", "--limit", "1" }, new[] { "history", "--limit=1" } })
        {
            var result = fixture.Cli(args);
            result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
            result.Stdout.Split('\n').Count(line => line.Contains("run ", StringComparison.Ordinal))
                .Should().Be(1);
        }

        var json = fixture.Cli("history", "--limit", "1", "--json");
        using var document = JsonDocument.Parse(json.Stdout);
        document.RootElement.GetProperty("limit").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("syncs")[0]
            .GetProperty("runs").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void History_SyncFilterByPositionalAndOption()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.Cli("run").ExitCode.Should().Be(0);
        AddEmptySync(fixture, "extra");

        foreach (var args in new[] { new[] { "history", "docs" }, new[] { "history", "--sync", "docs" } })
        {
            var result = fixture.Cli(args);
            result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
            result.Stdout.Should().Contain("run ");
            result.Stdout.Should().NotContain("extra");
        }

        var all = fixture.Cli("history");
        all.ExitCode.Should().Be(0, all.Stdout + all.Stderr);
        all.Stdout.Should().Contain("docs:");
        all.Stdout.Should().Contain("extra: no runs recorded");

        var extra = fixture.Cli("history", "extra");
        extra.ExitCode.Should().Be(0, extra.Stdout + extra.Stderr);
        extra.Stdout.Should().Contain("no runs recorded");
        extra.Stdout.Should().NotContain("run ");
    }

    [Fact]
    public void History_WithoutStateDirectoryCreatesNothing()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        var before = Directory.GetFileSystemEntries(fixture.StateDir)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var result = fixture.Cli("history");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("no runs recorded");
        Directory.GetFileSystemEntries(fixture.StateDir)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Should().Equal(before);
        Directory.Exists(Path.Combine(fixture.StateDir, "state")).Should().BeFalse();
        Directory.Exists(Path.Combine(fixture.StateDir, "logs")).Should().BeFalse();
        Directory.Exists(Path.Combine(fixture.StateDir, "locks")).Should().BeFalse();
    }

    [Fact]
    public void History_MalformedStatsJsonKeepsRowWithNullSummary()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.Cli("run").ExitCode.Should().Be(0);

        var sync = fixture.Config.Syncs.Single();
        var dbPath = Path.Combine(fixture.StateDir, "state",
            StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot) + ".sqlite");
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET stats_json = '{ not json';";
            command.ExecuteNonQuery();
        }

        var human = fixture.Cli("history");
        human.ExitCode.Should().Be(0, human.Stdout + human.Stderr);
        human.Stdout.Should().Contain("run ");

        var json = fixture.Cli("history", "--json");
        json.ExitCode.Should().Be(0, json.Stdout + json.Stderr);
        using var document = JsonDocument.Parse(json.Stdout);
        var run = document.RootElement.GetProperty("syncs")[0].GetProperty("runs")[0];
        run.GetProperty("planned").ValueKind.Should().Be(JsonValueKind.Null);
        run.GetProperty("succeeded").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void History_ShowsRunningPartialAndConflictRows()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.Cli("run").ExitCode.Should().Be(0);

        var sync = fixture.Config.Syncs.Single();
        var syncId = StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot);
        var dbPath = Path.Combine(fixture.StateDir, "state", syncId + ".sqlite");
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO runs (sync_id, started_at, finished_at, status, stats_json)
                VALUES ($sync, '2026-11-02T10:00:00.0000000Z', NULL, 'running', NULL),
                       ($sync, '2026-11-02T10:01:00.0000000Z', '2026-11-02T10:01:10.0000000Z', 'partial', $partial),
                       ($sync, '2026-11-02T10:02:00.0000000Z', '2026-11-02T10:02:10.0000000Z', 'conflict', $conflict);
                """;
            command.Parameters.AddWithValue("$sync", syncId);
            command.Parameters.AddWithValue("$partial",
                "{\"planned\":{\"Considered\":5,\"Actions\":3,\"Uploads\":2,\"Downloads\":1," +
                "\"Deletions\":0,\"Adoptions\":1,\"Conflicts\":0,\"Skipped\":2}," +
                "\"succeeded\":2,\"failed\":1,\"skipped\":0}");
            command.Parameters.AddWithValue("$conflict",
                "{\"planned\":{\"Considered\":6,\"Actions\":4,\"Uploads\":1,\"Downloads\":0," +
                "\"Deletions\":1,\"Adoptions\":3,\"Conflicts\":2,\"Skipped\":1}," +
                "\"succeeded\":3,\"failed\":0,\"skipped\":1}");
            command.ExecuteNonQuery();
        }

        var human = fixture.Cli("history");
        human.ExitCode.Should().Be(0, human.Stdout + human.Stderr);
        var lines = human.Stdout.Split('\n');
        static bool HasStatus(string line, string status)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 2 && parts[0] == "run" && parts[2] == status;
        }

        var running = lines.Single(line => HasStatus(line, "running"));
        running.Should().Contain("-> -").And.NotContain("planned");
        lines.Single(line => HasStatus(line, "partial"))
            .Should().Contain("planned 3 (up 2, down 1, del 0, conflict 0, adopt 1)")
            .And.Contain("ok 2  fail 1  skip 0");
        lines.Single(line => HasStatus(line, "conflict"))
            .Should().Contain("planned 4 (up 1, down 0, del 1, conflict 2, adopt 3)")
            .And.Contain("ok 3  fail 0  skip 1");

        var json = fixture.Cli("history", "--json");
        json.ExitCode.Should().Be(0, json.Stdout + json.Stderr);
        using var document = JsonDocument.Parse(json.Stdout);
        var runs = document.RootElement.GetProperty("syncs")[0].GetProperty("runs")
            .EnumerateArray().ToDictionary(run => run.GetProperty("status").GetString()!);

        runs["running"].GetProperty("finishedAt").ValueKind.Should().Be(JsonValueKind.Null);
        runs["running"].GetProperty("planned").ValueKind.Should().Be(JsonValueKind.Null);

        var partial = runs["partial"];
        partial.GetProperty("finishedAt").ValueKind.Should().Be(JsonValueKind.String);
        partial.GetProperty("succeeded").GetInt32().Should().Be(2);
        partial.GetProperty("failed").GetInt32().Should().Be(1);
        partial.GetProperty("skipped").GetInt32().Should().Be(0);
        partial.GetProperty("planned").GetProperty("conflicts").GetInt32().Should().Be(0);
        partial.GetProperty("planned").GetProperty("adoptions").GetInt32().Should().Be(1);

        var conflict = runs["conflict"];
        conflict.GetProperty("succeeded").GetInt32().Should().Be(3);
        conflict.GetProperty("failed").GetInt32().Should().Be(0);
        conflict.GetProperty("skipped").GetInt32().Should().Be(1);
        conflict.GetProperty("planned").GetProperty("conflicts").GetInt32().Should().Be(2);
        conflict.GetProperty("planned").GetProperty("adoptions").GetInt32().Should().Be(3);
    }

    [Fact]
    public void History_JsonHasDocumentedShape()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.Cli("run").ExitCode.Should().Be(0);

        var result = fixture.Cli("history", "--json");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        root.GetProperty("command").GetString().Should().Be("history");
        root.GetProperty("limit").GetInt32().Should().Be(20);
        var sync = root.GetProperty("syncs")[0];
        sync.GetProperty("sync").GetString().Should().Be("docs");
        sync.GetProperty("state").ValueKind.Should().Be(JsonValueKind.String);
        sync.GetProperty("pending").GetInt32().Should().Be(0);
        var run = sync.GetProperty("runs")[0];
        run.GetProperty("id").GetInt64().Should().BePositive();
        run.GetProperty("status").GetString().Should().Be("ok");
        run.GetProperty("startedAt").ValueKind.Should().Be(JsonValueKind.String);
        run.GetProperty("finishedAt").ValueKind.Should().Be(JsonValueKind.String);
        var planned = run.GetProperty("planned");
        planned.GetProperty("considered").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        planned.GetProperty("actions").GetInt32().Should().Be(1);
        planned.GetProperty("uploads").GetInt32().Should().Be(1);
        planned.GetProperty("downloads").GetInt32().Should().Be(0);
        planned.GetProperty("deletions").GetInt32().Should().Be(0);
        planned.GetProperty("adoptions").GetInt32().Should().Be(0);
        planned.GetProperty("conflicts").GetInt32().Should().Be(0);
        planned.GetProperty("skipped").GetInt32().Should().Be(0);
        run.GetProperty("succeeded").GetInt32().Should().Be(1);
        run.GetProperty("failed").GetInt32().Should().Be(0);
        run.GetProperty("skipped").GetInt32().Should().Be(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("501")]
    public void History_InvalidLimitIsUsageError(string value)
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();

        var result = fixture.Cli("history", "--limit", value);

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("--limit requires a number between 1 and 500");
    }

    private static void AddEmptySync(Fixture fixture, string name)
    {
        var local = fixture.Local + "-" + name;
        Directory.CreateDirectory(local);
        File.AppendAllText(fixture.ConfigPath, $"\n  {name}:\n    local: \"{local.Replace('\\', '/')}\"\n    remote: /my-files/root-{name}\n");
    }

    private static string[] RunLogFiles(string stateDir)
    {
        var logDir = Path.Combine(stateDir, "logs");
        return Directory.Exists(logDir)
            ? Directory.GetFiles(logDir, "*.jsonl", SearchOption.AllDirectories)
            : Array.Empty<string>();
    }

    private static List<JsonElement> ReadRunLog(string path) =>
        File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToList();

    [Fact]
    public void Doctor_VersionAuthFailureReturnsExit3()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        var scenario = fixture.Temp.Write("scenario.json", """
            { "rules": [ { "match": "--version", "authExpired": true } ] }
            """);
        fixture.Fake("__fake", "scenario", scenario);

        var result = fixture.Cli("doctor");

        result.ExitCode.Should().Be(3, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("auth:").And.Contain("auth login");
    }

    [Fact]
    public void Doctor_AuthStatusAuthFailureReturnsExit3()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        var scenario = fixture.Temp.Write("scenario.json", """
            { "rules": [ { "match": "fs list --json /$", "authExpired": true } ] }
            """);
        fixture.Fake("__fake", "scenario", scenario);

        var result = fixture.Cli("doctor");

        result.ExitCode.Should().Be(3, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("auth:").And.Contain("auth login");
    }

    [Fact]
    public void Doctor_RemoteListAuthFailureReturnsExit3()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        var scenario = fixture.Temp.Write("scenario.json", """
            { "rules": [ { "match": "fs list --json /my-files/root$", "authExpired": true } ] }
            """);
        fixture.Fake("__fake", "scenario", scenario);

        var result = fixture.Cli("doctor");

        result.ExitCode.Should().Be(3, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("auth:").And.Contain("auth login");
    }

    [Fact]
    public void Doctor_StagedLeftoverProbeAuthFailureReturnsExit3()
    {
        using var fixture = new Fixture();
        fixture.SeedFolder("sub");
        var scenario = fixture.Temp.Write("scenario.json", """
            { "rules": [ { "match": "fs list --json /my-files/root/sub$", "times": 1, "authExpired": true } ] }
            """);
        fixture.Fake("__fake", "scenario", scenario);

        var result = fixture.Cli("doctor");

        result.ExitCode.Should().Be(3, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("auth:").And.Contain("auth login");
    }

    [Fact]
    public void Doctor_NonAuthProbeFailureReturnsExit1()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        var scenario = fixture.Temp.Write("scenario.json", """
            { "rules": [ { "match": "fs list --json /$", "exitCode": 1, "stderr": "injected listing failure" } ] }
            """);
        fixture.Fake("__fake", "scenario", scenario);

        var result = fixture.Cli("doctor");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("FAIL auth:").And.Contain("injected listing failure");
    }

    [Fact]
    public void Doctor_ReportsHealthyFake()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();

        var result = fixture.Cli("doctor");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("doctor: ok");
    }

    [Fact]
    public void Doctor_JsonSerializesChecks()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();

        var result = fixture.Cli("doctor", "--json");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("\"ok\": true");
        result.Stdout.Should().NotContain("\"warning\"");
    }

    [Fact]
    public void Doctor_ReportsDesktopAppMappingOverlap()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();

        var result = fixture.CliWithMappings(StubDesktopMappings.WithMappings(fixture.Local), "doctor");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        var overlapLine = result.Stdout
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Single(line => line.StartsWith("FAIL sync docs desktop-app overlap:", StringComparison.Ordinal));
        overlapLine.Should().Contain(fixture.Local).And.Contain("exact overlap");
        result.Stdout.Should().Contain("doctor: problems found");
    }

    [Fact]
    public void Doctor_JsonIncludesDesktopMappingPath()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();

        var result = fixture.CliWithMappings(StubDesktopMappings.WithMappings(fixture.Local), "doctor", "--json");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        root.GetProperty("desktopMappings")[0].GetString().Should().Be(fixture.Local);

        var detail = root.GetProperty("checks")
            .EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "sync docs desktop-app overlap")
            .GetProperty("detail")
            .GetString();
        detail.Should().Contain(fixture.Local).And.Contain("exact overlap");
    }

    [Fact]
    public void Doctor_MalformedMappingsFileIsNonFatalWarning()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();

        var result = fixture.CliWithMappings(
            StubDesktopMappings.WithDiagnostic("cannot read desktop-app mappings: bad json"), "doctor");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("warn desktop-app mappings");
        result.Stdout.Should().Contain("bad json");
    }

    [Fact]
    public void Doctor_OldSchemaWarnsWithoutWritingState()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        using (StateStore.Open(fixture.Config, fixture.Config.Syncs.Single()))
        {
        }

        var dbPath = Directory.GetFiles(Path.Combine(fixture.StateDir, "state"), "*.sqlite").Single();
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 0;";
            command.ExecuteNonQuery();
        }

        var hashBefore = SHA256.HashData(File.ReadAllBytes(dbPath));
        var mtimeBefore = File.GetLastWriteTimeUtc(dbPath);

        var result = fixture.Cli("doctor");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("warn sync docs state: State database schema v0 is not current (v1); run pdsync run to migrate it");
        result.Stdout.Should().Contain("sync docs staged leftovers: skipped: state schema not current");
        result.Stdout.Split('\n').Count(line => line.StartsWith("warn ", StringComparison.Ordinal))
            .Should().Be(1, "one warning per sync, not one per probe");
        result.Stdout.Should().NotContain("doctor: problems found");
        SHA256.HashData(File.ReadAllBytes(dbPath)).Should().Equal(hashBefore);
        File.GetLastWriteTimeUtc(dbPath).Should().Be(mtimeBefore);
    }

    [Fact]
    public void Doctor_NewerSchemaStaysAFailure()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        using (StateStore.Open(fixture.Config, fixture.Config.Syncs.Single()))
        {
        }

        var dbPath = Directory.GetFiles(Path.Combine(fixture.StateDir, "state"), "*.sqlite").Single();
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }

        var doctor = fixture.Cli("doctor");

        doctor.ExitCode.Should().Be(1, doctor.Stdout + doctor.Stderr);
        doctor.Stdout.Should().Contain(
            "FAIL sync docs state: State database schema v2 is newer than this build (v1); upgrade pdsync.");
        doctor.Stdout.Should().NotContain("run pdsync run to migrate it");
        doctor.Stdout.Should().Contain("doctor: problems found");

        var plan = fixture.Cli("plan");

        plan.ExitCode.Should().NotBe(0, plan.Stdout + plan.Stderr);
        plan.Stderr.Should().Contain("State database schema v2 is newer than this build (v1)");
        plan.Stderr.Should().Contain("upgrade pdsync");
    }

    [Fact]
    public void Doctor_MalformedIgnoreWinsOverOldSchema()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        using (StateStore.Open(fixture.Config, fixture.Config.Syncs.Single()))
        {
        }

        var dbPath = Directory.GetFiles(Path.Combine(fixture.StateDir, "state"), "*.sqlite").Single();
        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 0;";
            command.ExecuteNonQuery();
        }

        fixture.WriteLocal(".pdignore", "foo[\n");

        var result = fixture.Cli("doctor");

        result.ExitCode.Should().Be(4, result.Stdout + result.Stderr);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().StartWith("config:");
        result.Stderr.Should().Contain("invalid ignore pattern 'foo['");
    }

    [Fact]
    public void Doctor_WithoutStateDirectoryCreatesNothing()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        var before = Directory.GetFileSystemEntries(fixture.StateDir)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var result = fixture.Cli("doctor");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        Directory.GetFileSystemEntries(fixture.StateDir)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Should().Equal(before);
        Directory.Exists(Path.Combine(fixture.StateDir, "state")).Should().BeFalse();
        Directory.Exists(Path.Combine(fixture.StateDir, "logs")).Should().BeFalse();
        Directory.Exists(Path.Combine(fixture.StateDir, "locks")).Should().BeFalse();
    }

    [Fact]
    public void Doctor_CorruptStateDatabaseStillFails()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        var sync = fixture.Config.Syncs.Single();
        var dbPath = Path.Combine(fixture.StateDir, "state",
            StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot) + ".sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllBytes(dbPath, Encoding.UTF8.GetBytes("this is not a sqlite database"));

        var result = fixture.Cli("doctor");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("FAIL sync docs");
        result.Stdout.Should().Contain("doctor: problems found");
    }

    [Fact]
    public void Doctor_MalformedIgnoreFileIsAConfigError()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal(".pdignore", "foo[\n");

        var result = fixture.Cli("doctor");

        result.ExitCode.Should().Be(4, result.Stdout + result.Stderr);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().StartWith("config:");
        result.Stderr.Should().Contain("invalid ignore pattern 'foo['");
    }

    [Fact]
    public void Run_StagedUploadRenameFailure_TrashesStagedNodeAndReturnsOriginalFailure()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("{a,b}.txt", "alpha");
        var scenario = fixture.Temp.Write("scenario.json", """
            { "rules": [ { "match": "fs rename", "times": 1, "exitCode": 1, "stderr": "injected rename failure" } ] }
            """);
        fixture.Fake("__fake", "scenario", scenario);

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(1);
        result.Stdout.Should().Contain("injected rename failure");
        fixture.RemoteFiles().Should().BeEmpty();
        var staging = fixture.Nodes().Keys
            .Where(k => k.StartsWith("/my-files/root/pdsync-upload-", StringComparison.Ordinal))
            .ToList();
        staging.Should().ContainSingle();
        fixture.IsTrashed(staging[0]).Should().BeTrue();
    }

    [Fact]
    public void Run_StagedUploadRenameFailure_LeavesNodeWhenCleanupAlsoFails()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("{a,b}.txt", "alpha");
        var scenario = fixture.Temp.Write("scenario.json", """
            {
              "rules": [
                { "match": "fs rename", "times": 1, "exitCode": 1, "stderr": "injected rename failure" },
                { "match": "fs trash", "times": 0, "exitCode": 1, "stderr": "injected cleanup failure" }
              ]
            }
            """);
        fixture.Fake("__fake", "scenario", scenario);

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(1);
        result.Stdout.Should().Contain("injected rename failure");
        result.Stdout.Should().NotContain("injected cleanup failure");
        // Prove the cleanup branch was actually entered and its trash failed.
        fixture.InvocationCount("trash").Should().Be(1);
        var staging = fixture.Nodes().Keys
            .Where(k => k.StartsWith("/my-files/root/pdsync-upload-", StringComparison.Ordinal))
            .ToList();
        staging.Should().ContainSingle();
        fixture.IsTrashed(staging[0]).Should().BeFalse();
    }

    [Fact]
    public void PlanAndRun_CleanupRemoteStagingLeftoverWithoutDownloadingIt()
    {
        using var fixture = new Fixture();
        fixture.SeedFile("pdsync-upload-0123456789ab", "orphan");

        var plan = fixture.Cli("plan");

        plan.ExitCode.Should().Be(0, plan.Stderr);
        plan.Stdout.Should().Contain("cleanup staged upload pdsync-upload-0123456789ab");
        Directory.Exists(Path.Combine(fixture.StateDir, "state")).Should().BeFalse();

        var run = fixture.Cli("run");
        run.ExitCode.Should().Be(0, run.Stderr);
        fixture.IsTrashed("/my-files/root/pdsync-upload-0123456789ab").Should().BeTrue();
        File.Exists(fixture.LocalFull("pdsync-upload-0123456789ab")).Should().BeFalse();

        var second = fixture.Cli("run");
        second.ExitCode.Should().Be(0, second.Stderr);
        fixture.IsTrashed("/my-files/root/pdsync-upload-0123456789ab").Should().BeTrue();
    }

    [Fact]
    public void Run_DoesNotCleanupRemoteDirectoryNamedLikeStagingLeaf()
    {
        using var fixture = new Fixture();
        var seed = fixture.Temp.Write("seed.json", """
            {
              "auth": true,
              "nodes": [
                { "path": "/my-files/root", "type": "folder" },
                { "path": "/my-files/root/pdsync-upload-0123456789ab", "type": "folder" }
              ]
            }
            """);
        fixture.Fake("__fake", "seed", seed);

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(0, result.Stderr);
        fixture.IsTrashed("/my-files/root/pdsync-upload-0123456789ab").Should().BeFalse();
        Directory.Exists(fixture.LocalFull("pdsync-upload-0123456789ab")).Should().BeTrue();
    }

    [Fact]
    public void PlanAndRun_CleanupNestedStagingLeftover()
    {
        using var fixture = new Fixture();
        var seed = fixture.Temp.Write("seed.json", """
            {
              "auth": true,
              "nodes": [
                { "path": "/my-files/root", "type": "folder" },
                { "path": "/my-files/root/nested", "type": "folder" },
                { "path": "/my-files/root/nested/pdsync-upload-0123456789ab", "type": "file", "content": "orphan" }
              ]
            }
            """);
        fixture.Fake("__fake", "seed", seed);

        var plan = fixture.Cli("plan");
        plan.ExitCode.Should().Be(0, plan.Stderr);
        plan.Stdout.Should().Contain("cleanup staged upload nested/pdsync-upload-0123456789ab");

        var run = fixture.Cli("run");
        run.ExitCode.Should().Be(0, run.Stderr);
        fixture.IsTrashed("/my-files/root/nested/pdsync-upload-0123456789ab").Should().BeTrue();
        File.Exists(fixture.LocalFull("nested/pdsync-upload-0123456789ab")).Should().BeFalse();
    }

    [Fact]
    public void Run_DoesNotTrashBaselineTrackedOrNonMatchingStagingNames()
    {
        using (var fixture = new Fixture())
        {
            fixture.SeedRoot();
            fixture.WriteLocal("pdsync-upload-0123456789ab", "tracked");
            var first = fixture.Cli("run");
            first.ExitCode.Should().Be(0, first.Stderr);

            var second = fixture.Cli("run");
            second.ExitCode.Should().Be(0, second.Stderr);
            fixture.IsTrashed("/my-files/root/pdsync-upload-0123456789ab").Should().BeFalse();
            File.Exists(fixture.LocalFull("pdsync-upload-0123456789ab")).Should().BeTrue();
        }

        using (var fixture = new Fixture())
        {
            var seed = fixture.Temp.Write("seed.json", """
                {
                  "auth": true,
                  "nodes": [
                    { "path": "/my-files/root", "type": "folder" },
                    { "path": "/my-files/root/pdsync-upload-0123456789AB", "type": "file", "content": "uppercase" },
                    { "path": "/my-files/root/pdsync-upload-0123456789a", "type": "file", "content": "short" },
                    { "path": "/my-files/root/pdsync-upload-0123456789ab.txt", "type": "file", "content": "extension" },
                    { "path": "/my-files/root/pdsync-upload2-0123456789ab", "type": "file", "content": "prefix" },
                    { "path": "/my-files/root/pdsync-upload-0123456789ag", "type": "file", "content": "nonhex" }
                  ]
                }
                """);
            fixture.Fake("__fake", "seed", seed);

            var result = fixture.Cli("run");

            result.ExitCode.Should().Be(0, result.Stderr);
            foreach (var path in new[]
                     {
                         "/my-files/root/pdsync-upload-0123456789AB",
                         "/my-files/root/pdsync-upload-0123456789a",
                         "/my-files/root/pdsync-upload-0123456789ab.txt",
                         "/my-files/root/pdsync-upload2-0123456789ab",
                         "/my-files/root/pdsync-upload-0123456789ag",
                     })
                fixture.IsTrashed(path).Should().BeFalse(path);
        }
    }

    [Fact]
    public void Doctor_ReportsAdoptedStagingLeftoverButNotOrdinaryFiles()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("pdsync-upload-0123456789ab", "tracked");
        fixture.WriteLocal("notes.txt", "ordinary user content");
        var first = fixture.Cli("run");
        first.ExitCode.Should().Be(0, first.Stderr);

        File.Delete(fixture.LocalFull("pdsync-upload-0123456789ab"));
        var uid = fixture.Nodes()["/my-files/root/pdsync-upload-0123456789ab"]
            .GetProperty("uid").GetString()!;

        var doctor = fixture.Cli("doctor");

        doctor.ExitCode.Should().Be(0, doctor.Stderr);
        doctor.Stdout.Should().Contain("adopted/historical");
        doctor.Stdout.Should().Contain("pdsync-upload-0123456789ab");
        doctor.Stdout.Should().Contain(uid);
        doctor.Stdout.Should().NotContain("notes.txt");
    }

    [Fact]
    public void Doctor_DoesNotReportIgnoredStagingLeftover()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("pdsync-upload-0123456789ab", "tracked");
        fixture.WriteLocal("keep.txt", "keep the local root non-empty");
        var first = fixture.Cli("run");
        first.ExitCode.Should().Be(0, first.Stderr);

        fixture.WriteLocal(".pdignore", "/pdsync-upload-0123456789ab\n");

        var doctor = fixture.Cli("doctor");

        doctor.ExitCode.Should().Be(0, doctor.Stdout + doctor.Stderr);
        doctor.Stdout.Should().Contain("staged leftovers: none");
        doctor.Stdout.Should().NotContain("adopted/historical");
    }

    [Fact]
    public void Run_WithoutFlag_UsesOrdinaryRestoreForModifiedAdoptedStagingNode()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("pdsync-upload-0123456789ab", "tracked");
        fixture.WriteLocal("keep.txt", "keep the local root non-empty");
        var first = fixture.Cli("run");
        first.ExitCode.Should().Be(0, first.Stderr);

        File.Delete(fixture.LocalFull("pdsync-upload-0123456789ab"));
        // A new UID/revision makes this an adopted remote-only leftover that
        // the normal delete-vs-modify path restores instead of trashing.
        var seed = fixture.Temp.Write("remote.json", """
            {
              "auth": true,
              "nodes": [
                { "path": "/my-files/root", "type": "folder" },
                { "path": "/my-files/root/keep.txt", "type": "file", "content": "keep the local root non-empty" },
                { "path": "/my-files/root/pdsync-upload-0123456789ab", "type": "file", "content": "leftover" }
              ]
            }
            """);
        fixture.Fake("__fake", "seed", seed);

        var run = fixture.Cli("run");

        run.ExitCode.Should().Be(0, run.Stdout + run.Stderr);
        fixture.IsTrashed("/my-files/root/pdsync-upload-0123456789ab").Should().BeFalse();
        File.Exists(fixture.LocalFull("pdsync-upload-0123456789ab")).Should().BeTrue();
    }

    [Fact]
    public void Run_WithoutFlag_TrashesUnchangedAdoptedStagingNodeAsOrdinaryLocalDelete()
    {
        using var fixture = new Fixture(safety: "maxDeletes: 10\n      maxDeletePercent: 100");
        fixture.SeedRoot();
        fixture.WriteLocal("pdsync-upload-0123456789ab", "tracked");
        fixture.WriteLocal("keep.txt", "keep the local root non-empty");
        var first = fixture.Cli("run");
        first.ExitCode.Should().Be(0, first.Stderr);

        File.Delete(fixture.LocalFull("pdsync-upload-0123456789ab"));

        var run = fixture.Cli("run");

        run.ExitCode.Should().Be(0, run.Stdout + run.Stderr);
        fixture.IsTrashed("/my-files/root/pdsync-upload-0123456789ab").Should().BeTrue();
    }

    [Fact]
    public void Run_WithCleanupStaged_TrashesAdoptedLeftoverAndSecondRunIsNoOp()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("pdsync-upload-0123456789ab", "tracked");
        fixture.WriteLocal("keep.txt", "keep the local root non-empty");
        var first = fixture.Cli("run");
        first.ExitCode.Should().Be(0, first.Stderr);

        File.Delete(fixture.LocalFull("pdsync-upload-0123456789ab"));
        var seed = fixture.Temp.Write("remote.json", """
            {
              "auth": true,
              "nodes": [
                { "path": "/my-files/root", "type": "folder" },
                { "path": "/my-files/root/keep.txt", "type": "file", "content": "keep the local root non-empty" },
                { "path": "/my-files/root/pdsync-upload-0123456789ab", "type": "file", "content": "leftover" }
              ]
            }
            """);
        fixture.Fake("__fake", "seed", seed);

        var run = fixture.Cli("run", "--cleanup-staged");

        run.ExitCode.Should().Be(0, run.Stdout + run.Stderr);
        fixture.IsTrashed("/my-files/root/pdsync-upload-0123456789ab").Should().BeTrue();
        File.Exists(fixture.LocalFull("pdsync-upload-0123456789ab")).Should().BeFalse();

        var second = fixture.Cli("run", "--cleanup-staged");
        second.ExitCode.Should().Be(0, second.Stdout + second.Stderr);
        second.Stdout.Should().Contain("planned 0");
        fixture.IsTrashed("/my-files/root/pdsync-upload-0123456789ab").Should().BeTrue();
    }

    [Fact]
    public void Plan_RejectsCleanupStagedFlagWithoutWritingState()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();

        var plan = fixture.Cli("plan", "--cleanup-staged");

        plan.ExitCode.Should().Be(1);
        plan.Stderr.Should().Contain("--cleanup-staged is only valid with run");
        Directory.Exists(Path.Combine(fixture.StateDir, "state")).Should().BeFalse();
    }

    [Fact]
    public void Doctor_OldCliFailsWithFailingCheck()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.Fake("__fake", "set-version", "0.8.5");

        var result = fixture.Cli("doctor");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("proton-drive: Fake Proton Drive CLI 0.8.5");
        result.Stdout.Should().Contain("FAIL proton-drive version:");
        result.Stdout.Should().Contain("older than proton.minVersion 0.9.0");
        result.Stdout.Should().Contain("doctor: problems found");
    }

    [Fact]
    public void Plan_OldCliFailsWithoutCreatingState()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.Fake("__fake", "set-version", "0.8.5");

        var result = fixture.Cli("plan");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("proton-drive 0.8.5").And.Contain("older than proton.minVersion 0.9.0");
        Directory.Exists(Path.Combine(fixture.StateDir, "state")).Should().BeFalse();
        fixture.InvocationCount("list").Should().Be(0);
    }

    [Fact]
    public void Run_OldCliFailsBeforeLockStateOrLogs()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.Fake("__fake", "set-version", "0.8.5");

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("older than proton.minVersion 0.9.0");
        Directory.Exists(Path.Combine(fixture.StateDir, "locks")).Should().BeFalse();
        Directory.Exists(Path.Combine(fixture.StateDir, "state")).Should().BeFalse();
        Directory.Exists(Path.Combine(fixture.StateDir, "logs")).Should().BeFalse();
        fixture.InvocationCount("upload").Should().Be(0);
    }

    [Theory]
    [InlineData("0.9.0")]
    [InlineData("0.9.1")]
    [InlineData("1.0.0")]
    public void Plan_EqualOrNewerCliPasses(string version)
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.Fake("__fake", "set-version", version);

        var result = fixture.Cli("plan");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("upload new a.txt");
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("run")]
    [InlineData("doctor")]
    public void LowerMinVersionOverride_AllowsDetectedCli(string command)
    {
        // Deliberately below the built-in 0.9.0 default: 0.8.5 only passes if
        // the configured 0.8.0 override is honored. If the default leaked
        // through, the gate would reject 0.8.5 and this test would fail.
        using var fixture = new Fixture(minVersion: "0.8.0");
        fixture.SeedRoot();
        fixture.Fake("__fake", "set-version", "0.8.5");

        var result = fixture.Cli(command);

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
    }

    [Fact]
    public void Plan_RaisedMinVersionRejectsDefaultCli()
    {
        using var fixture = new Fixture(minVersion: "1.0.0");
        fixture.SeedRoot();

        var result = fixture.Cli("plan");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("older than proton.minVersion 1.0.0");
    }

    [Fact]
    public void Plan_UnparsableCliVersionFailsClosed()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.Fake("__fake", "set-version", "not-a-version");

        var result = fixture.Cli("plan");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("cannot verify the CLI version")
            .And.Contain("proton.minVersion is 0.9.0");
    }

    [Fact]
    public void MalformedMinVersion_ReturnsConfigExit4()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        var yaml = File.ReadAllText(fixture.ConfigPath)
            .Replace("proton:", "proton:\n  minVersion: \"not-a-version\"", StringComparison.Ordinal);
        File.WriteAllText(fixture.ConfigPath, yaml);

        var result = fixture.Cli("plan");

        result.ExitCode.Should().Be(4, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("proton.minVersion").And.Contain("not-a-version");
    }

    [Fact]
    public void Rebuild_AdoptIdenticalContent_RecordsBaselineWithoutTransfer()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "same");
        fixture.WriteLocal("a.txt", "same");

        var result = fixture.Cli("rebuild", "docs", "--policy", "adopt", "--yes");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("1 recorded");
        fixture.InvocationCount("upload").Should().Be(0);
        fixture.InvocationCount("download").Should().Be(0);
        fixture.Entries().Should().ContainSingle().Which.Path.Should().Be("a.txt");
        File.Exists(fixture.DbPath()).Should().BeTrue();
    }

    [Fact]
    public void Rebuild_KeepBoth_ConflictRenamesAndKeepsBothSides()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "remote");
        fixture.WriteLocal("a.txt", "local");

        var result = fixture.Cli("rebuild", "docs", "--policy", "keep-both", "--yes");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        File.ReadAllText(fixture.LocalFull("a.txt")).Should().Be("remote");
        Directory.GetFiles(fixture.Local, "a*.txt").Should().HaveCount(2);
        fixture.RemoteFileNames().Should().HaveCount(2);
        fixture.Entries().Should().HaveCount(2);
        fixture.InvocationCount("upload").Should().BeGreaterThan(0);
        fixture.InvocationCount("download").Should().BeGreaterThan(0);
    }

    [Fact]
    public void Rebuild_LocalWins_UploadsRevisionAndPreservesRemoteHistory()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "remote");
        fixture.WriteLocal("a.txt", "local");

        var result = fixture.Cli("rebuild", "docs", "--policy", "local-wins", "--yes");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        File.ReadAllText(fixture.LocalFull("a.txt")).Should().Be("local");
        fixture.RemoteFile("/my-files/root/a.txt").Should().Be("local");
        fixture.IsTrashed("/my-files/root/a.txt").Should().BeFalse();
        fixture.InvocationCount("upload").Should().Be(1);
        fixture.InvocationCount("trash").Should().Be(0);
        fixture.Entries().Should().ContainSingle().Which.Path.Should().Be("a.txt");
    }

    [Fact]
    public void Rebuild_RemoteWins_DownloadsAndMovesLocalLoserToTrash()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "remote");
        fixture.WriteLocal("a.txt", "local");

        var result = fixture.Cli("rebuild", "docs", "--policy", "remote-wins", "--yes");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        File.ReadAllText(fixture.LocalFull("a.txt")).Should().Be("remote");
        fixture.RemoteFile("/my-files/root/a.txt").Should().Be("remote");
        Directory.GetFiles(Path.Combine(fixture.StateDir, "trash"), "a.txt", SearchOption.AllDirectories)
            .Should().ContainSingle();
        fixture.InvocationCount("download").Should().Be(1);
        fixture.InvocationCount("upload").Should().Be(0);
        fixture.Entries().Should().ContainSingle().Which.Path.Should().Be("a.txt");
    }

    [Fact]
    public void Rebuild_MissingState_CreatesFreshBaseline()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "remote");

        File.Exists(fixture.DbPath()).Should().BeFalse();
        var result = fixture.Cli("rebuild", "docs", "--policy", "adopt", "--yes");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        File.Exists(fixture.DbPath()).Should().BeTrue();
        File.ReadAllText(fixture.LocalFull("a.txt")).Should().Be("remote");
        fixture.Entries().Should().ContainSingle().Which.Path.Should().Be("a.txt");
        Directory.Exists(Path.Combine(fixture.StateDir, "state", "backups")).Should().BeFalse();
    }

    [Fact]
    public void Rebuild_CorruptState_BacksUpRawBytesThenRebuilds()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "same");
        fixture.WriteLocal("a.txt", "same");
        var corrupt = Encoding.UTF8.GetBytes("this is not a sqlite database");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.DbPath())!);
        File.WriteAllBytes(fixture.DbPath(), corrupt);

        var result = fixture.Cli("rebuild", "docs", "--policy", "adopt", "--yes");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        var backup = Directory.GetFiles(
            Path.Combine(fixture.StateDir, "state", "backups"), "*.pre-rebuild-*.sqlite")
            .Should().ContainSingle().Subject;
        File.ReadAllBytes(backup).Should().Equal(corrupt);
        fixture.Entries().Should().ContainSingle().Which.Path.Should().Be("a.txt");
    }

    [Fact]
    public void Rebuild_LockHeld_ReturnsExit5WithoutWritingOrListing()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "same");
        fixture.WriteLocal("a.txt", "same");
        fixture.Cli("run").ExitCode.Should().Be(0);
        var listings = fixture.InvocationCount("list");
        var before = File.ReadAllBytes(fixture.DbPath());
        using var held = PdSync.Exec.RunLock.Acquire(fixture.Config, fixture.Config.Syncs.Single());

        var result = fixture.Cli("rebuild", "docs", "--policy", "adopt", "--yes");

        result.ExitCode.Should().Be(5, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("lock");
        fixture.InvocationCount("list").Should().Be(listings, "the lock must precede all remote work");
        File.ReadAllBytes(fixture.DbPath()).Should().Equal(before);
        fixture.LegacyStateBackups().Should().BeEmpty();
        fixture.RebuildBackupFiles().Should().BeEmpty();
    }

    [Fact]
    public void Rebuild_WithoutYesNonInteractive_RefusesWithoutWriting()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "same");
        fixture.WriteLocal("a.txt", "same");
        fixture.Cli("run").ExitCode.Should().Be(0);
        var before = File.ReadAllBytes(fixture.DbPath());

        var result = fixture.CliWithInput(string.Empty,
            "rebuild", "docs", "--policy", "keep-both");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("rebuild requires --yes when not interactive");
        File.ReadAllBytes(fixture.DbPath()).Should().Equal(before);
        fixture.LegacyStateBackups().Should().BeEmpty();
        fixture.RebuildBackupFiles().Should().BeEmpty();
        fixture.InvocationCount("upload").Should().Be(0);
        fixture.InvocationCount("download").Should().Be(0);
    }

    [Fact]
    public void Rebuild_TtyRefusal_CancelsWithoutWriting()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "same");
        fixture.WriteLocal("a.txt", "same");
        fixture.Cli("run").ExitCode.Should().Be(0);
        var before = File.ReadAllBytes(fixture.DbPath());

        var result = fixture.CliWithStdin("n\n", isTty: true,
            "rebuild", "docs", "--policy", "keep-both");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("rebuild cancelled");
        File.ReadAllBytes(fixture.DbPath()).Should().Equal(before);
        fixture.LegacyStateBackups().Should().BeEmpty();
        fixture.RebuildBackupFiles().Should().BeEmpty();
    }

    [Fact]
    public void Rebuild_DryRun_PrintsSummaryWithoutWritingStateOrBackup()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "same");
        fixture.WriteLocal("a.txt", "same");
        fixture.Cli("run").ExitCode.Should().Be(0);
        fixture.WriteLocal("a.txt", "local change");
        var before = File.ReadAllBytes(fixture.DbPath());

        var result = fixture.Cli("rebuild", "docs", "--policy", "adopt", "--dry-run");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("rebuild docs (dry-run)");
        File.ReadAllBytes(fixture.DbPath()).Should().Equal(before);
        fixture.LegacyStateBackups().Should().BeEmpty();
        fixture.RebuildBackupFiles().Should().BeEmpty();
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData(null)]
    public void Rebuild_UnknownOrMissingPolicy_ReturnsUsageErrorWithValueList(string? policy)
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        var args = policy is null
            ? new[] { "rebuild", "docs", "--yes" }
            : new[] { "rebuild", "docs", "--policy", policy, "--yes" };

        var result = fixture.Cli(args);

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("adopt|keep-both|local-wins|remote-wins");
        File.Exists(fixture.DbPath()).Should().BeFalse();
    }

    [Fact]
    public void Rebuild_EmptyBaseline_DoesNotDeleteFromOldBaseline()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "alpha");
        fixture.WriteLocal("a.txt", "alpha");
        fixture.Cli("run").ExitCode.Should().Be(0);
        File.Delete(fixture.LocalFull("a.txt"));

        var result = fixture.Cli("rebuild", "docs", "--policy", "adopt", "--yes");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        // With an empty baseline the remote-only file is downloaded. If the old
        // baseline leaked in, the local deletion would remote-trash it instead.
        File.ReadAllText(fixture.LocalFull("a.txt")).Should().Be("alpha");
        fixture.IsTrashed("/my-files/root/a.txt").Should().BeFalse();
        fixture.Entries().Should().ContainSingle().Which.Path.Should().Be("a.txt");
    }

    [Fact]
    public void Rebuild_MultipleSyncs_ReturnsUsageError()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();

        var result = fixture.Cli(
            "rebuild", "docs", "other", "--policy", "adopt", "--yes");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("exactly one sync");
        fixture.LegacyStateBackups().Should().BeEmpty();
        fixture.RebuildBackupFiles().Should().BeEmpty();
    }

    [Fact]
    public void Rebuild_IgnoreRules_ExcludeIgnoredPathsFromBaseline()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal(".pdignore", "ignored.txt\n");
        fixture.WriteLocal("ignored.txt", "secret");
        fixture.WriteLocal("keep.txt", "keep");

        var result = fixture.Cli("rebuild", "docs", "--policy", "adopt", "--yes");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        fixture.Entries().Select(entry => entry.Path).Should().NotContain("ignored.txt");
        fixture.RemoteFileNames().Should().NotContain("/my-files/root/ignored.txt");
        fixture.RemoteFile("/my-files/root/keep.txt").Should().Be("keep");
    }

    [Fact]
    public void Rebuild_KindMismatch_ReturnsExit2WithoutReplacingState()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "remote");
        fixture.WriteLocal("a.txt", "same");
        fixture.Cli("run").ExitCode.Should().Be(0);
        File.Delete(fixture.LocalFull("a.txt"));
        Directory.CreateDirectory(fixture.LocalFull("a.txt"));
        var before = File.ReadAllBytes(fixture.DbPath());

        var result = fixture.Cli("rebuild", "docs", "--policy", "adopt", "--yes");

        result.ExitCode.Should().Be(2, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("kind changed");
        File.ReadAllBytes(fixture.DbPath()).Should().Equal(before);
        fixture.LegacyStateBackups().Should().BeEmpty();
        fixture.RebuildBackupFiles().Should().BeEmpty();
    }

    [Fact]
    public void Rebuild_Json_HasStableCamelCaseShape()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFile("a.txt", "same");
        fixture.WriteLocal("a.txt", "same");

        var result = fixture.Cli("rebuild", "docs", "--policy", "adopt", "--dry-run", "--json");

        result.ExitCode.Should().Be(0, result.Stdout + result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        root.GetProperty("command").GetString().Should().Be("rebuild");
        root.GetProperty("sync").GetString().Should().Be("docs");
        root.GetProperty("policy").GetString().Should().Be("adopt");
        root.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        root.GetProperty("recorded").GetInt32().Should().Be(1);
        root.GetProperty("uploaded").GetInt32().Should().Be(0);
        root.GetProperty("downloaded").GetInt32().Should().Be(0);
        root.GetProperty("conflictRenamed").GetInt32().Should().Be(0);
        root.GetProperty("backup").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("exitCode").GetInt32().Should().Be(0);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("run")]
    public void NonRebuildCommand_RejectsPolicyFlag(string command)
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();

        var result = fixture.Cli(command, "--policy", "adopt");

        result.ExitCode.Should().Be(1, result.Stdout + result.Stderr);
        result.Stderr.Should().Contain("--policy is only valid with rebuild");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly string? _oldState;
        private readonly string _fakeExe;

        public Fixture(string? defaults = null, string? safety = null, string? exclude = null, string? minVersion = null)
        {
            _oldState = Environment.GetEnvironmentVariable("PDSYNC_FAKE_STATE");
            Local = _temp.Combine("local");
            StateDir = _temp.Combine("state");
            FakeState = _temp.Combine("fake");
            Directory.CreateDirectory(Local);
            Directory.CreateDirectory(StateDir);
            Directory.CreateDirectory(FakeState);
            Environment.SetEnvironmentVariable("PDSYNC_FAKE_STATE", FakeState);

            _fakeExe = FindFakeExe()
                ?? throw new InvalidOperationException("FakeProtonCli is not built");

            var defaultBlock = defaults is null ? string.Empty : defaults;
            var safetyBlock = safety is null ? string.Empty : $"  safety:\n      {safety}";
            var excludeBlock = exclude is null ? string.Empty : $"exclude:\n  - \"{exclude}\"";
            var minVersionBlock = minVersion is null ? string.Empty : $"  minVersion: \"{minVersion}\"";
            var yaml = $"""
                version: 1
                stateDir: "{_temp.YamlPath("state")}"
                proton:
                  cli: "{_fakeExe.Replace('\\', '/')}"
                {minVersionBlock}
                defaults:
                  settleSeconds: 0
                {Indent(defaultBlock, 2)}
                {safetyBlock}
                syncs:
                  docs:
                    local: "{Local.Replace('\\', '/')}"
                    remote: /my-files/root
                {Indent(excludeBlock, 4)}
                """;
            ConfigPath = _temp.Write("pdsync.yaml", yaml);
            Config = PdSync.Config.ConfigLoader.Load(ConfigPath);
        }

        public string Local { get; }
        public string StateDir { get; }
        public string FakeState { get; }
        public string ConfigPath { get; }
        public PdSync.Config.PdsyncConfig Config { get; }
        public TempDirectory Temp => _temp;

        public string LocalFull(string relativePath) =>
            Path.Combine(Local, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void WriteLocal(string relativePath, string content)
        {
            var path = LocalFull(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void SeedRoot() =>
            Fake("__fake", "seed", _temp.Write("seed.json", """
                { "auth": true, "nodes": [ { "path": "/my-files/root", "type": "folder" } ] }
                """));

        public void SeedFolder(string relativePath) =>
            Fake("__fake", "seed", _temp.Write("seed.json", $$"""
                {
                  "auth": true,
                  "nodes": [
                    { "path": "/my-files/root", "type": "folder" },
                    { "path": "/my-files/root/{{relativePath}}", "type": "folder" }
                  ]
                }
                """));

        public void SeedFile(string relativePath, string content) =>
            Fake("__fake", "seed", _temp.Write("seed.json", $$"""
                {
                  "auth": true,
                  "nodes": [
                    { "path": "/my-files/root", "type": "folder" },
                    { "path": "/my-files/root/{{relativePath}}", "type": "file", "content": "{{content}}" }
                  ]
                }
                """));

        public CliResult Cli(params string[] args) =>
            CliWithMappings(StubDesktopMappings.None, args);

        public CliResult CliWithInput(string input, params string[] args) =>
            CliWithStdin(input, isTty: false, args);

        public CliResult CliWithStdin(string input, bool isTty, params string[] args)
        {
            var all = new[] { args[0], "--config", ConfigPath }.Concat(args.Skip(1)).ToArray();
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = CliApp.Run(
                all, stdout, stderr, StubDesktopMappings.None,
                input: new StringReader(input), stdinIsTty: isTty);
            return new CliResult(exit, stdout.ToString(), stderr.ToString());
        }

        public CliResult CliWithMappings(IDesktopMappingDetector detector, params string[] args)
        {
            var all = new[] { args[0], "--config", ConfigPath }.Concat(args.Skip(1)).ToArray();
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = CliApp.Run(all, stdout, stderr, detector);
            return new CliResult(exit, stdout.ToString(), stderr.ToString());
        }

        public string DbPath() => Path.Combine(
            StateDir, "state",
            PdSync.State.StateStore.ComputeSyncId(
                Config.Syncs.Single().LocalRoot, Config.Syncs.Single().RemoteRoot) + ".sqlite");

        public IReadOnlyList<PdSync.State.EntryRecord> Entries()
        {
            using var state = PdSync.State.StateStore.OpenReadOnly(Config, Config.Syncs.Single());
            return state.LoadEntries(includeFrozen: true);
        }

        /// <summary>The migration-style backup emitted directly under state/.</summary>
        public IReadOnlyList<string> LegacyStateBackups()
        {
            var directory = Path.Combine(StateDir, "state");
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "backup-*.sqlite")
                : Array.Empty<string>();
        }

        /// <summary>The explicit rebuild backups under state/backups/.</summary>
        public IReadOnlyList<string> RebuildBackupFiles()
        {
            var directory = Path.Combine(StateDir, "state", "backups");
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
        }

        public string Fake(params string[] args)
        {
            var psi = new ProcessStartInfo(_fakeExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["PDSYNC_FAKE_STATE"] = FakeState;
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"fake CLI failed ({process.ExitCode}): {stdout}\n{stderr}");
            return stdout;
        }

        public IReadOnlyDictionary<string, JsonElement> Nodes()
        {
            using var document = JsonDocument.Parse(Fake("__fake", "dump"));
            var nodes = document.RootElement.GetProperty("nodes").EnumerateArray()
                .Select(n => n.Clone())
                .ToList();
            var byUid = nodes.ToDictionary(
                n => n.GetProperty("uid").GetString()!,
                n => n,
                StringComparer.Ordinal);
            var paths = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            string? PathOf(JsonElement node)
            {
                var uid = node.GetProperty("uid").GetString()!;
                if (uid == "root-my-files")
                    return "/my-files";
                var nameElement = node.GetProperty("name");
                var name = nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()!
                    : nameElement.GetProperty("value").GetString()!;
                var parent = node.GetProperty("parentUid").GetString();
                if (parent is null || !byUid.TryGetValue(parent, out var parentNode))
                    return null;
                var parentPath = PathOf(parentNode);
                return parentPath is null ? null : parentPath + "/" + name;
            }

            foreach (var node in nodes)
            {
                var path = PathOf(node);
                if (path is not null)
                    paths[path] = node;
            }
            return paths;
        }

        public IEnumerable<string> RemoteFiles() =>
            Nodes().Where(p => p.Key.StartsWith("/my-files/root/", StringComparison.Ordinal) &&
                               !p.Value.TryGetProperty("trashTime", out _))
                   .Select(p => p.Key);

        public string? RemoteFile(string path)
        {
            if (!Nodes().TryGetValue(path, out var node) || node.TryGetProperty("trashTime", out _))
                return null;
            var sha1 = node.GetProperty("sha1").GetString()!;
            var blob = Path.Combine(FakeState, "blobs", sha1);
            return File.Exists(blob) ? File.ReadAllText(blob) : null;
        }

        public IReadOnlyList<string> RemoteFileNames() =>
            Nodes().Where(p => p.Key.StartsWith("/my-files/root/", StringComparison.Ordinal) &&
                               !p.Value.TryGetProperty("trashTime", out _))
                   .Select(p => p.Key)
                   .ToList();

        public bool IsTrashed(string path) =>
            Nodes().TryGetValue(path, out var node) && node.TryGetProperty("trashTime", out _);

        public int InvocationCount(string command)
        {
            var path = Path.Combine(FakeState, "invocations.jsonl");
            if (!File.Exists(path))
                return 0;
            return File.ReadLines(path).Count(line =>
            {
                using var document = JsonDocument.Parse(line);
                var args = document.RootElement.GetProperty("args").EnumerateArray()
                    .Select(a => a.GetString()).ToList();
                return args.Count > 1 && args[0] == "fs" && args[1] == command;
            });
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PDSYNC_FAKE_STATE", _oldState);
            _temp.Dispose();
        }

        private static string Indent(string value, int spaces)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            var prefix = new string(' ', spaces);
            return string.Join('\n', value.Split('\n').Select(line => prefix + line));
        }

        private static string? FindFakeExe()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    foreach (var name in new[] { "FakeProtonCli.exe", "FakeProtonCli" })
                    {
                        var candidate = Path.Combine(
                            directory.FullName, "tools", "FakeProtonCli", "bin", configuration, "net8.0", name);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
                directory = directory.Parent;
            }
            return null;
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private sealed class StubDesktopMappings : IDesktopMappingDetector
    {
        private readonly DesktopMappingScan _scan;

        private StubDesktopMappings(DesktopMappingScan scan) => _scan = scan;

        public static StubDesktopMappings None { get; } =
            new(new DesktopMappingScan(Array.Empty<DesktopMapping>(), null));

        public static StubDesktopMappings WithMappings(params string[] mappingRoots) =>
            new(new DesktopMappingScan(
                mappingRoots.Select(path => new DesktopMapping(path, "stub/Mappings.json")).ToArray(),
                null));

        public static StubDesktopMappings WithDiagnostic(string diagnostic) =>
            new(new DesktopMappingScan(Array.Empty<DesktopMapping>(), diagnostic));

        public DesktopMappingScan Detect() => _scan;
    }
}
