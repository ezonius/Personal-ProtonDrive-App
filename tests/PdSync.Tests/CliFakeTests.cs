using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PdSync.Cli;
using PdSync.Diagnostics;

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
    public void Run_WithoutFlag_LeavesAdoptedStagingLeftoverUntouched()
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

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly string? _oldState;
        private readonly string _fakeExe;

        public Fixture(string? defaults = null, string? safety = null, string? exclude = null)
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
            var yaml = $"""
                version: 1
                stateDir: "{_temp.YamlPath("state")}"
                proton:
                  cli: "{_fakeExe.Replace('\\', '/')}"
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

        public CliResult CliWithMappings(IDesktopMappingDetector detector, params string[] args)
        {
            var all = new[] { args[0], "--config", ConfigPath }.Concat(args.Skip(1)).ToArray();
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = CliApp.Run(all, stdout, stderr, detector);
            return new CliResult(exit, stdout.ToString(), stderr.ToString());
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
