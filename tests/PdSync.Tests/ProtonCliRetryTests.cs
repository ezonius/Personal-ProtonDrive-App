using System.Diagnostics;
using System.Text;
using FluentAssertions;
using PdSync.Proton;

namespace PdSync.Tests;

/// <summary>
/// Scenario-driven coverage for SPEC §6.4 retry/backoff: transient failures are
/// bounded and retried only where replay is safe, auth/permanent failures never
/// are. Delays are kept tiny so the suite stays fast.
/// </summary>
[Collection("fake-proton-cli")]
public sealed class ProtonCliRetryTests : IDisposable
{
    private static readonly ProtonRetryOptions FastRetry = new()
    {
        MaxAttempts = 3,
        BaseDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(5),
        Jitter = 0,
    };

    private readonly TempDirectory _temp = new();
    private readonly string? _oldState;
    private readonly string _stateDir;
    private readonly string _fakeExe;

    public ProtonCliRetryTests()
    {
        _oldState = Environment.GetEnvironmentVariable("PDSYNC_FAKE_STATE");
        _stateDir = _temp.Combine("fake-state");
        Directory.CreateDirectory(_stateDir);
        Environment.SetEnvironmentVariable("PDSYNC_FAKE_STATE", _stateDir);
        _fakeExe = FindFakeExe() ?? throw new InvalidOperationException(
            "FakeProtonCli is not built; run dotnet build pdsync.sln");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PDSYNC_FAKE_STATE", _oldState);
        _temp.Dispose();
    }

    [Fact]
    public void List_RetriesTransient503ThenSucceeds()
    {
        SeedRoot();
        Scenario("""{ "rules": [ { "match": "fs list", "times": 1, "exitCode": 503, "stderr": "HTTP 503 Service Unavailable" } ] }""");

        var nodes = Cli().List("/my-files");

        nodes.Should().ContainSingle(n => n.Name == "root");
        Invocations("\"fs\",\"list\"").Should().Be(2);
    }

    [Fact]
    public void List_Persistent5xxExhaustsBoundedAttempts()
    {
        SeedRoot();
        Scenario("""{ "rules": [ { "match": "fs list", "times": 0, "exitCode": 500, "stderr": "500 Internal Server Error" } ] }""");

        var exception = Assert.Throws<ProtonCliException>(() => Cli().List("/my-files/root"));

        exception.Kind.Should().Be(ProtonFailureKind.Transient);
        exception.Attempts.Should().Be(3);
        Invocations("\"fs\",\"list\"").Should().Be(3);
    }

    [Fact]
    public void List_RetriesTimeoutThenSucceeds()
    {
        SeedRoot();
        Scenario("""{ "rules": [ { "match": "fs list", "times": 1, "delayMs": 3000 } ] }""");

        var nodes = new ProtonCli(_fakeExe, TimeSpan.FromMilliseconds(150), FastRetry)
            .List("/my-files");

        nodes.Should().ContainSingle(n => n.Name == "root");
        Invocations("\"fs\",\"list\"").Should().Be(2);
    }

    [Fact]
    public void List_DoesNotRetryPermanentFailure()
    {
        SeedRoot();
        Scenario("""{ "rules": [ { "match": "fs list", "times": 0, "exitCode": 1, "stderr": "invalid path" } ] }""");

        var exception = Assert.Throws<ProtonCliException>(() => Cli().List("/my-files/root"));

        exception.Kind.Should().Be(ProtonFailureKind.Permanent);
        exception.Attempts.Should().Be(1);
        Invocations("\"fs\",\"list\"").Should().Be(1);
    }

    [Fact]
    public void AuthFailure_IsNeverRetried()
    {
        SeedRoot();
        RunFake("__fake", "set-auth", "off");

        var exception = Assert.Throws<ProtonAuthException>(() => Cli().List("/my-files/root"));

        exception.Kind.Should().Be(ProtonFailureKind.Auth);
        exception.ExitCode.Should().Be(3);
        exception.Attempts.Should().Be(1);
        Invocations("\"fs\",\"list\"").Should().Be(1);
    }

    [Fact]
    public void CreateFolder_RetriesRateLimitBecauseNothingExecuted()
    {
        SeedRoot();
        Scenario("""{ "rules": [ { "match": "fs create-folder", "times": 1, "exitCode": 429, "stderr": "429 Too Many Requests" } ] }""");

        var created = Cli().CreateFolder("/my-files/root", "docs");

        created.Name.Should().Be("docs");
        Invocations("\"fs\",\"create-folder\"").Should().Be(2);
    }

    [Fact]
    public void CreateFolder_Transient5xxIsNotReplayed()
    {
        SeedRoot();
        Scenario("""{ "rules": [ { "match": "fs create-folder", "times": 0, "exitCode": 503, "stderr": "HTTP 503 Service Unavailable" } ] }""");

        var exception = Assert.Throws<ProtonCliException>(
            () => Cli().CreateFolder("/my-files/root", "docs"));

        exception.Kind.Should().Be(ProtonFailureKind.Transient);
        exception.Attempts.Should().Be(1);
        Invocations("\"fs\",\"create-folder\"").Should().Be(1);
    }

    [Fact]
    public void Upload_TransientFailureIsNotReplayed()
    {
        SeedRoot();
        Scenario("""{ "rules": [ { "match": "fs upload", "times": 0, "exitCode": 503, "stderr": "HTTP 503 Service Unavailable" } ] }""");
        var local = _temp.Write("local/a.txt", "alpha");

        var exception = Assert.Throws<ProtonCliException>(
            () => Cli().Upload(local, "/my-files/root"));

        exception.Kind.Should().Be(ProtonFailureKind.Transient);
        exception.Attempts.Should().Be(1);
        Invocations("\"fs\",\"upload\"").Should().Be(1);
    }

    [Fact]
    public void Download_IsIdempotentAndRetriesTransientFailure()
    {
        SeedRoot();
        RunFake("__fake", "seed", _temp.Write("seed.json", """
            {
              "auth": true,
              "nodes": [
                { "path": "/my-files/root", "type": "folder" },
                { "path": "/my-files/root/a.txt", "type": "file", "content": "alpha" }
              ]
            }
            """));
        Scenario("""{ "rules": [ { "match": "fs download", "times": 1, "exitCode": 503, "stderr": "HTTP 503 Service Unavailable" } ] }""");
        var staging = _temp.Combine("staging");
        Directory.CreateDirectory(staging);

        Cli().Download("/my-files/root/a.txt", staging);

        File.ReadAllText(Path.Combine(staging, "a.txt")).Should().Be("alpha");
        Invocations("\"fs\",\"download\"").Should().Be(2);
    }

    private ProtonCli Cli() => new(_fakeExe, retry: FastRetry);

    private void SeedRoot() =>
        RunFake("__fake", "seed", _temp.Write("seed.json", """
            { "auth": true, "nodes": [ { "path": "/my-files/root", "type": "folder" } ] }
            """));

    private void Scenario(string json) =>
        RunFake("__fake", "scenario", _temp.Write("scenario.json", json));

    private int Invocations(string needle)
    {
        var path = Path.Combine(_stateDir, "invocations.jsonl");
        return File.Exists(path)
            ? File.ReadLines(path).Count(line => line.Contains(needle, StringComparison.Ordinal))
            : 0;
    }

    private void RunFake(params string[] args)
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
