using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PdSync.Cli;
using PdSync.Diagnostics;
using PdSync.State;

namespace PdSync.Tests;

/// <summary>
/// S8 / SPEC §6.4: a session that expires in the middle of a run must abort with
/// exit code 3, must not retry the auth failure, must not run any later action,
/// and must not commit state for the failed action. If auth expires after a
/// transfer but before its verification/rename, a remote change may already
/// exist and is reconciled on the next run (not asserted as impossible here).
///
/// These tests use FakeProtonCli fault injection, so they are fully
/// deterministic and run in the normal suite. They never touch a real account or
/// an authenticated session, which is why no opt-in/live gate is required.
/// Recovery is outside pdsync: run <c>proton-drive auth login</c>, then rerun.
/// </summary>
[Collection("fake-proton-cli")]
[Trait("Category", "AuthExpiry")]
public sealed class AuthExpiryTests
{
    [Fact]
    public void Run_AuthExpiryDuringUpload_Exits3WithoutRetryDestructiveActionOrCommit()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.WriteLocal("b.txt", "beta");
        fixture.WriteLocal("c.txt", "gamma");
        fixture.Scenario("""{ "rules": [ { "match": "b\\.txt", "times": 0, "authExpired": true } ] }""");

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(3);
        // This is pdsync's own recovery UX (ProtonCli builds the message and
        // CliApp prints it); it does not prove fake-CLI stderr propagation.
        result.Stderr.Should().Contain("proton-drive auth login",
            "the auth failure must tell the user how to recover");

        // a.txt is attempted first and succeeds; b.txt fails once and is never
        // retried; c.txt is never attempted because the run aborts.
        fixture.InvocationCount("upload").Should().Be(2);
        fixture.InvocationCountContaining("b.txt").Should().Be(1);
        fixture.InvocationCountContaining("c.txt").Should().Be(0);
        fixture.InvocationCount("download").Should().Be(0);
        fixture.InvocationCount("trash").Should().Be(0);

        fixture.RemoteFile("/my-files/root/a.txt").Should().Be("alpha");
        fixture.RemoteFile("/my-files/root/b.txt").Should().BeNull();
        fixture.RemoteFile("/my-files/root/c.txt").Should().BeNull();

        fixture.CommittedPaths().Should().BeEquivalentTo("a.txt");
        fixture.PendingCount().Should().Be(0);
    }

    [Fact]
    public void Run_AuthExpiryDuringDownload_Exits3WithoutRetryDestructiveActionOrCommit()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFiles("a.txt", "b.txt", "c.txt");
        fixture.Scenario("""{ "rules": [ { "match": "b\\.txt", "times": 0, "authExpired": true } ] }""");

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(3);
        result.Stderr.Should().Contain("proton-drive auth login");

        // a.txt is downloaded and committed; b.txt fails once and is never
        // retried; c.txt is never attempted because the run aborts.
        fixture.InvocationCount("download").Should().Be(2);
        fixture.InvocationCountContaining("b.txt").Should().Be(1);
        fixture.InvocationCountContaining("c.txt").Should().Be(0);
        fixture.InvocationCount("upload").Should().Be(0);
        fixture.InvocationCount("trash").Should().Be(0);

        File.ReadAllText(fixture.LocalFull("a.txt")).Should().Be("remote-a.txt");
        File.Exists(fixture.LocalFull("b.txt")).Should().BeFalse();
        File.Exists(fixture.LocalFull("c.txt")).Should().BeFalse();
        Directory.Exists(Path.Combine(fixture.StateDir, "trash")).Should().BeFalse();

        fixture.CommittedPaths().Should().BeEquivalentTo("a.txt");
        fixture.PendingCount().Should().Be(0);
    }

    [Fact]
    public void Run_AuthExpiryDuringDownloadReplace_LeavesExistingLocalFileAndStateUntouched()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.SeedFiles("a.txt");
        fixture.WriteLocal("a.txt", "remote-a.txt");
        fixture.Cli("run").ExitCode.Should().Be(0);

        // Create a newer remote revision while the local file stays unchanged,
        // so the planner selects DownloadReplace over the existing local file.
        var source = fixture.Temp.Write("remote-v2/a.txt", "remote-a-v2.txt");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow);
        fixture.Fake("fs", "upload", "--json", "--file-conflict-strategy", "create-new-revision",
            source, "/my-files/root");
        fixture.RemoteFile("/my-files/root/a.txt").Should().Be("remote-a-v2.txt");

        var beforeMtime = File.GetLastWriteTimeUtc(fixture.LocalFull("a.txt"));
        var downloadsBefore = fixture.InvocationCount("download");
        fixture.Scenario("""{ "rules": [ { "match": "fs download", "times": 0, "authExpired": true } ] }""");

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(3);
        result.Stderr.Should().Contain("proton-drive auth login");

        // The download fails once and is never retried; the existing local file
        // is not replaced or moved to trash before the CLI call.
        fixture.InvocationCount("download").Should().Be(downloadsBefore + 1);
        File.ReadAllText(fixture.LocalFull("a.txt")).Should().Be("remote-a.txt");
        File.GetLastWriteTimeUtc(fixture.LocalFull("a.txt")).Should().Be(beforeMtime);
        Directory.Exists(Path.Combine(fixture.StateDir, "trash")).Should().BeFalse();

        // State still holds the previous revision, not the failed replacement.
        fixture.CommittedEntry("a.txt").Sha1.Should().Be(Sha1("remote-a.txt"));
        fixture.CommittedPaths().Should().BeEquivalentTo("a.txt");
        fixture.PendingCount().Should().Be(0);
    }

    [Fact]
    public void Run_AuthExpiryDuringRemoteTrash_Exits3WithoutRetryDestructiveActionOrCommit()
    {
        using var fixture = new Fixture(safety: "maxDeletes: 10\n      maxDeletePercent: 100");
        fixture.SeedRoot();
        fixture.WriteLocal("a.txt", "alpha");
        fixture.WriteLocal("b.txt", "beta");
        fixture.WriteLocal("c.txt", "gamma");
        fixture.Cli("run").ExitCode.Should().Be(0);

        File.Delete(fixture.LocalFull("b.txt"));
        File.Delete(fixture.LocalFull("c.txt"));
        fixture.Scenario("""{ "rules": [ { "match": "b\\.txt", "times": 0, "authExpired": true } ] }""");

        // The first (setup) run also invoked the CLI; only this run's calls count.
        var uploadsBefore = fixture.InvocationCount("upload");
        var downloadsBefore = fixture.InvocationCount("download");
        var trashesBefore = fixture.InvocationCount("trash");
        var bBefore = fixture.InvocationCountContaining("b.txt");
        var cBefore = fixture.InvocationCountContaining("c.txt");

        var result = fixture.Cli("run");

        result.ExitCode.Should().Be(3);
        result.Stderr.Should().Contain("proton-drive auth login");

        // b.txt is trashed once and fails; c.txt is never attempted because the
        // run aborts. Neither node may be moved to Proton Trash.
        fixture.InvocationCount("trash").Should().Be(trashesBefore + 1);
        fixture.InvocationCountContaining("b.txt").Should().Be(bBefore + 1);
        fixture.InvocationCountContaining("c.txt").Should().Be(cBefore);
        fixture.InvocationCount("upload").Should().Be(uploadsBefore);
        fixture.InvocationCount("download").Should().Be(downloadsBefore);

        fixture.IsTrashed("/my-files/root/b.txt").Should().BeFalse();
        fixture.IsTrashed("/my-files/root/c.txt").Should().BeFalse();

        // The failed trash action's state delete rolled back; the baseline still
        // contains all three synced entries.
        fixture.CommittedPaths().Should().BeEquivalentTo("a.txt", "b.txt", "c.txt");
        fixture.PendingCount().Should().Be(0);
    }

    [Fact]
    public void Run_AuthExpiryDuringStagedRename_SkipsSameRunCleanupAndNextRunTrashesLeftover()
    {
        using var fixture = new Fixture();
        fixture.SeedRoot();
        fixture.WriteLocal("{a,b}.txt", "alpha");
        fixture.Scenario("""{ "rules": [ { "match": "fs rename", "times": 1, "authExpired": true } ] }""");

        var first = fixture.Cli("run");

        first.ExitCode.Should().Be(3);
        first.Stderr.Should().Contain("proton-drive auth login");
        // The session is gone: same-run cleanup must deliberately skip the CLI.
        fixture.InvocationCount("trash").Should().Be(0);

        var staging = fixture.Nodes().Keys
            .Where(k => k.StartsWith("/my-files/root/pdsync-upload-", StringComparison.Ordinal))
            .ToList();
        staging.Should().ContainSingle();
        fixture.IsTrashed(staging[0]).Should().BeFalse();

        // A plain rerun converges: the upload is retried and the leftover is
        // trashed deterministically by the planner cleanup action.
        var second = fixture.Cli("run");

        second.ExitCode.Should().Be(0, second.Stderr);
        fixture.InvocationCount("trash").Should().Be(1);
        fixture.IsTrashed(staging[0]).Should().BeTrue();
        File.ReadAllText(fixture.LocalFull("{a,b}.txt")).Should().Be("alpha");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly string? _oldState;
        private readonly string _fakeExe;

        public Fixture(string? safety = null)
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

            var safetyBlock = safety is null ? string.Empty : $"  safety:\n      {safety}";
            var yaml = $"""
                version: 1
                stateDir: "{_temp.YamlPath("state")}"
                proton:
                  cli: "{_fakeExe.Replace('\\', '/')}"
                defaults:
                  settleSeconds: 0
                {safetyBlock}
                syncs:
                  docs:
                    local: "{Local.Replace('\\', '/')}"
                    remote: /my-files/root
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

        public void SeedFiles(params string[] names)
        {
            var nodes = string.Join(",\n", names.Select(name => $$"""
                { "path": "/my-files/root/{{name}}", "type": "file", "content": "remote-{{name}}" }
                """));
            Fake("__fake", "seed", _temp.Write("seed.json", $$"""
                {
                  "auth": true,
                  "nodes": [
                    { "path": "/my-files/root", "type": "folder" },
                    {{nodes}}
                  ]
                }
                """));
        }

        public void Scenario(string json) =>
            Fake("__fake", "scenario", _temp.Write("scenario.json", json));

        public CliResult Cli(params string[] args)
        {
            var all = new[] { args[0], "--config", ConfigPath }.Concat(args.Skip(1)).ToArray();
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = CliApp.Run(all, stdout, stderr, new StubDesktopMappings());
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

        public int InvocationCount(string command) =>
            Invocations().Count(args => args.Count > 1 && args[0] == "fs" && args[1] == command);

        public int InvocationCountContaining(string fragment) =>
            Invocations().Count(args => args.Any(arg =>
                arg is not null && arg.Contains(fragment, StringComparison.OrdinalIgnoreCase)));

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

        public string? RemoteFile(string path)
        {
            if (!Nodes().TryGetValue(path, out var node) || node.TryGetProperty("trashTime", out _))
                return null;
            var sha1 = node.GetProperty("sha1").GetString()!;
            var blob = Path.Combine(FakeState, "blobs", sha1);
            return File.Exists(blob) ? File.ReadAllText(blob) : null;
        }

        public bool IsTrashed(string path) =>
            Nodes().TryGetValue(path, out var node) && node.TryGetProperty("trashTime", out _);

        public IReadOnlyList<string> CommittedPaths()
        {
            using var state = StateStore.OpenReadOnly(Config, Config.Syncs.Single());
            return state.LoadEntries(includeFrozen: true).Select(e => e.Path).ToList();
        }

        public EntryRecord CommittedEntry(string path)
        {
            using var state = StateStore.OpenReadOnly(Config, Config.Syncs.Single());
            return state.LoadEntries(includeFrozen: true).Single(e => e.Path == path);
        }

        public int PendingCount()
        {
            using var state = StateStore.OpenReadOnly(Config, Config.Syncs.Single());
            return state.LoadPending().Count;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PDSYNC_FAKE_STATE", _oldState);
            _temp.Dispose();
        }

        private List<List<string?>> Invocations()
        {
            var path = Path.Combine(FakeState, "invocations.jsonl");
            if (!File.Exists(path))
                return new List<List<string?>>();
            return File.ReadLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line =>
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.GetProperty("args").EnumerateArray()
                        .Select(a => a.GetString())
                        .ToList();
                })
                .ToList();
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

    private static string Sha1(string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private sealed class StubDesktopMappings : IDesktopMappingDetector
    {
        public DesktopMappingScan Detect() =>
            new(Array.Empty<DesktopMapping>(), null);
    }
}
