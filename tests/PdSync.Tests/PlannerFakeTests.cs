using System.Diagnostics;
using System.Text;
using FluentAssertions;
using PdSync.Config;
using PdSync.Plan;
using PdSync.Proton;
using PdSync.Remote;
using PdSync.Scan;

namespace PdSync.Tests;

[Collection("fake-proton-cli")]
public sealed class PlannerFakeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string? _oldState;
    private readonly string _stateDir;
    private readonly string? _fakeExe;

    public PlannerFakeTests()
    {
        _oldState = Environment.GetEnvironmentVariable("PDSYNC_FAKE_STATE");
        _stateDir = _temp.Combine("fake-state");
        Directory.CreateDirectory(_stateDir);
        Environment.SetEnvironmentVariable("PDSYNC_FAKE_STATE", _stateDir);
        FakeExe = FindFakeExe();
        _fakeExe = FakeExe;
    }

    public string? FakeExe { get; }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PDSYNC_FAKE_STATE", _oldState);
        _temp.Dispose();
    }

    [Fact]
    public void RemoteIndex_ReadsTheFakeCliTree()
    {
        RequireFake();
        Seed("""
            {
              "auth": true,
              "nodes": [
                { "path": "/my-files/root", "type": "folder" },
                { "path": "/my-files/root/docs", "type": "folder" },
                { "path": "/my-files/root/docs/a.txt", "type": "file", "content": "alpha" }
              ]
            }
            """);

        var cli = new ProtonCli(_fakeExe!);
        var direct = cli.List("/my-files/root");
        direct.Should().Contain(n => n.Name == "docs" && n.Type == "folder");

        var snapshot = RemoteIndex.Build(cli, "/my-files/root");
        snapshot.IsComplete.Should().BeTrue();
        snapshot.ByKey.Should().ContainKey("docs/a.txt");
        snapshot.ByKey["docs/a.txt"].Sha1.Should().Be(Sha1("alpha"));
    }

    [Fact]
    public void PlanAgainstFake_NewLocalAndNewRemoteBecomeUploadAndDownload()
    {
        RequireFake();
        Seed("""
            {
              "auth": true,
              "nodes": [
                { "path": "/my-files/root", "type": "folder" },
                { "path": "/my-files/root/remote.txt", "type": "file", "content": "remote" }
              ]
            }
            """);

        var localRoot = _temp.Combine("local");
        Directory.CreateDirectory(localRoot);
        File.WriteAllText(Path.Combine(localRoot, "local.txt"), "local");

        var result = RunPlanner(localRoot);

        result.Conflicts.Should().BeEmpty();
        result.Actions.Select(a => (a.Kind, a.Path)).Should().BeEquivalentTo(new[]
        {
            (PlanActionKind.UploadCreate, "local.txt"),
            (PlanActionKind.DownloadCreate, "remote.txt"),
        });
    }

    [Fact]
    public void AuthenticatedCliFailure_IsClassified()
    {
        RequireFake();
        Seed("""
            {
              "auth": true,
              "nodes": [
                { "path": "/my-files/root", "type": "folder" }
              ]
            }
            """);
        RunFake("__fake", "set-auth", "off");

        var cli = new ProtonCli(_fakeExe!);
        var exception = Assert.Throws<ProtonAuthException>(() => cli.List("/my-files/root"));
        exception.ExitCode.Should().Be(3);
    }

    [Fact]
    public void PlanAgainstFake_EqualFilesAreAdopted()
    {
        RequireFake();
        Seed("""
            {
              "auth": true,
              "nodes": [
                { "path": "/my-files/root", "type": "folder" },
                { "path": "/my-files/root/a.txt", "type": "file", "content": "same" }
              ]
            }
            """);

        var localRoot = _temp.Combine("local");
        Directory.CreateDirectory(localRoot);
        File.WriteAllText(Path.Combine(localRoot, "a.txt"), "same");

        var result = RunPlanner(localRoot);

        result.Actions.Should().BeEmpty();
        result.Adoptions.Should().ContainSingle(a => a.Path == "a.txt");
        result.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void PlanAgainstFake_NewLocalDirectoryIsCreatedBeforeItsFileUploads()
    {
        RequireFake();
        Seed("""
            {
              "auth": true,
              "nodes": [
                { "path": "/my-files/root", "type": "folder" }
              ]
            }
            """);

        var localRoot = _temp.Combine("local");
        Directory.CreateDirectory(Path.Combine(localRoot, "docs"));
        File.WriteAllText(Path.Combine(localRoot, "docs", "a.txt"), "alpha");

        var result = RunPlanner(localRoot);

        result.Actions.Select(a => a.Kind).Should().Equal(
            PlanActionKind.CreateRemoteDirectory,
            PlanActionKind.UploadCreate);
        result.Actions[0].Path.Should().Be("docs");
        result.Actions[1].Path.Should().Be("docs/a.txt");
    }

    private PlanResult RunPlanner(string localRoot)
    {
        var yaml = ConfigTestHelper.MinimalYaml(_temp,
            local: localRoot,
            remote: "/my-files/root");
        var config = ConfigTestHelper.Load(_temp, yaml);
        var sync = config.Syncs.Single();

        var now = DateTimeOffset.UtcNow;
        var local = LocalScanner.Scan(sync, new ScanOptions(SettleSeconds: 0, Now: now));
        var remote = RemoteIndex.Build(new ProtonCli(_fakeExe!), sync.RemoteRoot);
        return Planner.Plan([], local, remote, sync, new PlanContext(now));
    }

    private void Seed(string manifest)
    {
        var path = _temp.Write("seed.json", manifest);
        RunFake("__fake", "seed", path);
    }

    private void RunFake(params string[] args)
    {
        var psi = new ProcessStartInfo(_fakeExe!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PDSYNC_FAKE_STATE"] = _stateDir;
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"fake CLI failed ({process.ExitCode}): {stdout}\n{stderr}");
    }

    private void RequireFake() =>
        Assert.False(_fakeExe is null, "FakeProtonCli.exe is not built; run dotnet build pdsync.sln");

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

    private static string Sha1(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
}
