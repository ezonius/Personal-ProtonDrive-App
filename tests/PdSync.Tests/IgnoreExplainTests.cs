using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using PdSync.Cli;
using PdSync.Config;

namespace PdSync.Tests;

/// <summary>
/// `pdsync ignore` explains the deciding rule (SPEC §3.3 rule 7). The command is
/// read-only: these tests assert it never creates or modifies anything.
/// </summary>
public sealed class IgnoreExplainTests
{
    [Fact]
    public void IgnoredFile_ExplainsRuleNumberPatternSourceAndMatchedAncestor()
    {
        using var fixture = new Fixture(ignoreText: "bin/\n");

        var result = fixture.Cli("ignore", "bin/tool.exe");

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("ignore docs: bin/tool.exe (file)");
        result.Stdout.Should().Contain("verdict: ignored");
        result.Stdout.Should().Contain("#1 \"bin/\"");
        result.Stdout.Should().Contain(".pdignore:1");
        result.Stdout.Should().Contain("ignore file");
        result.Stdout.Should().Contain("matched ancestor: bin");
    }

    [Fact]
    public void NotIgnored_WithNoRules_ReportsIncludedAndNoRule()
    {
        using var fixture = new Fixture();

        var result = fixture.Cli("ignore", "notes.txt");

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("verdict: included");
        result.Stdout.Should().Contain("rule: none");
        result.Stdout.Should().NotContain("matched ancestor");
    }

    [Fact]
    public void NegatedRule_ReincludedPath_IsExplainedByTheNegation()
    {
        using var fixture = new Fixture(ignoreText: "*.log\n!keep.log\n");

        var included = fixture.Cli("ignore", "keep.log");
        included.ExitCode.Should().Be(0, included.Stderr);
        included.Stdout.Should().Contain("verdict: included");
        included.Stdout.Should().Contain("#2 \"!keep.log\"");
        included.Stdout.Should().Contain(".pdignore:2");

        var ignored = fixture.Cli("ignore", "other.log");
        ignored.ExitCode.Should().Be(0, ignored.Stderr);
        ignored.Stdout.Should().Contain("verdict: ignored");
        ignored.Stdout.Should().Contain("#1 \"*.log\"");
    }

    [Fact]
    public void InlineExclude_IsReportedAsInlineSource()
    {
        using var fixture = new Fixture(ignoreText: "*.log\n", exclude: new[] { "*.tmp" });

        var result = fixture.Cli("ignore", "scratch.tmp");

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("verdict: ignored");
        result.Stdout.Should().Contain("#2 \"*.tmp\"");
        result.Stdout.Should().Contain("exclude[0]");
        result.Stdout.Should().Contain("inline exclude");
    }

    [Fact]
    public void AnchoredPattern_OnlyMatchesAtTheRoot()
    {
        using var fixture = new Fixture(ignoreText: "/scratch/\n");

        var atRoot = fixture.Cli("ignore", "scratch/cache.dat");
        atRoot.ExitCode.Should().Be(0, atRoot.Stderr);
        atRoot.Stdout.Should().Contain("verdict: ignored");
        atRoot.Stdout.Should().Contain("matched ancestor: scratch");

        var nested = fixture.Cli("ignore", "sub/scratch/cache.dat");
        nested.ExitCode.Should().Be(0, nested.Stderr);
        nested.Stdout.Should().Contain("verdict: included");
        nested.Stdout.Should().Contain("rule: none");
    }

    [Fact]
    public void TrailingSeparator_RequestsDirectoryIntent()
    {
        using var fixture = new Fixture(ignoreText: "logs/\n");

        var asFile = fixture.Cli("ignore", "logs");
        asFile.ExitCode.Should().Be(0, asFile.Stderr);
        asFile.Stdout.Should().Contain("(file)");
        asFile.Stdout.Should().Contain("verdict: included");

        var asDirectory = fixture.Cli("ignore", "logs/");
        asDirectory.ExitCode.Should().Be(0, asDirectory.Stderr);
        asDirectory.Stdout.Should().Contain("(directory)");
        asDirectory.Stdout.Should().Contain("verdict: ignored");

        var nativeSeparator = fixture.Cli("ignore", "logs" + Path.DirectorySeparatorChar);
        nativeSeparator.ExitCode.Should().Be(0, nativeSeparator.Stderr);
        nativeSeparator.Stdout.Should().Contain("(directory)");
        nativeSeparator.Stdout.Should().Contain("verdict: ignored");
    }

    [Fact]
    public void RootedPathWithTrailingSeparator_IsResolvedAgainstTheSyncRoot()
    {
        using var fixture = new Fixture(ignoreText: "built/\n");

        var result = fixture.Cli("ignore", fixture.DocsRoot.Replace('\\', '/') + "/built/");

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("ignore docs: built (directory)");
        result.Stdout.Should().Contain("verdict: ignored");
    }

    [Fact]
    public void NonexistentPathInsideTheRoot_StillExplains()
    {
        using var fixture = new Fixture(ignoreText: "*.tmp\n");

        var result = fixture.Cli("ignore", "does/not/exist.tmp");

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("verdict: ignored");
        result.Stdout.Should().Contain("#1 \"*.tmp\"");
    }

    [Fact]
    public void MissingPathArgument_IsAConfigError()
    {
        using var fixture = new Fixture(ignoreText: "*.tmp\n");

        var result = fixture.Cli("ignore");

        result.ExitCode.Should().Be(4);
        result.Stderr.Should().Contain("<path>");
    }

    [Fact]
    public void RootedPathOutsideEverySyncRoot_IsAConfigError()
    {
        using var fixture = new Fixture(ignoreText: "*.tmp\n");
        var outside = fixture.Temp.Combine("elsewhere", "a.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);

        var result = fixture.Cli("ignore", outside);

        result.ExitCode.Should().Be(4);
        result.Stderr.Should().Contain("not inside any selected sync root");
    }

    [Fact]
    public void RelativePathAcrossTwoSyncs_IsAmbiguousUnlessSyncIsGiven()
    {
        using var fixture = new Fixture(ignoreText: "*.tmp\n", secondSync: true);

        var ambiguous = fixture.Cli("ignore", "a.tmp");
        ambiguous.ExitCode.Should().Be(4);
        ambiguous.Stderr.Should().Contain("ambiguous across syncs");
        ambiguous.Stderr.Should().Contain("docs");
        ambiguous.Stderr.Should().Contain("other");

        var selected = fixture.Cli("ignore", "a.tmp", "--sync", "docs");
        selected.ExitCode.Should().Be(0, selected.Stderr);
        selected.Stdout.Should().Contain("ignore docs: a.tmp (file)");

        var rooted = fixture.Cli("ignore", fixture.OtherRoot.Replace('\\', '/') + "/b.tmp");
        rooted.ExitCode.Should().Be(0, rooted.Stderr);
        rooted.Stdout.Should().Contain("ignore other: b.tmp (file)");
    }

    [Fact]
    public void Json_ReturnsAStableMachineReadableShape()
    {
        using var fixture = new Fixture(ignoreText: "bin/\n*.log\n");

        var result = fixture.Cli("ignore", "bin/tool.exe", "--json");

        result.ExitCode.Should().Be(0, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        root.GetProperty("command").GetString().Should().Be("ignore");
        root.GetProperty("sync").GetString().Should().Be("docs");
        root.GetProperty("root").GetString().Should().Be(fixture.DocsRoot);
        root.GetProperty("ignoreFile").GetString().Should().Be(Path.Combine(fixture.DocsRoot, ".pdignore"));
        root.GetProperty("path").GetString().Should().Be("bin/tool.exe");
        root.GetProperty("kind").GetString().Should().Be("file");
        root.GetProperty("ignored").GetBoolean().Should().BeTrue();
        root.GetProperty("verdict").GetString().Should().Be("ignored");
        root.GetProperty("pattern").GetString().Should().Be("bin/");
        root.GetProperty("rule").GetInt32().Should().Be(1);
        root.GetProperty("sourceKind").GetString().Should().Be("file");
        root.GetProperty("line").GetInt32().Should().Be(1);
        root.GetProperty("matchedPath").GetString().Should().Be("bin");
        root.GetProperty("source").GetString().Should().Contain(".pdignore:1");
    }

    [Fact]
    public void Json_NotIgnored_UsesNullsForRuleFields()
    {
        using var fixture = new Fixture();

        var result = fixture.Cli("ignore", "notes.txt", "--json");

        result.ExitCode.Should().Be(0, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        root.GetProperty("ignored").GetBoolean().Should().BeFalse();
        root.GetProperty("verdict").GetString().Should().Be("included");
        root.GetProperty("pattern").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("rule").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("source").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("sourceKind").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("matchedPath").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Command_LeavesTheStateDbAndFilesystemUnchanged()
    {
        using var fixture = new Fixture(ignoreText: "bin/\n");
        var stateFile = fixture.Temp.Combine("state", "state", "docs.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        File.WriteAllText(stateFile, "sentinel-not-a-real-db");

        var before = Snapshot(fixture.Temp.Root);

        var human = fixture.Cli("ignore", "bin/tool.exe");
        var json = fixture.Cli("ignore", "bin/tool.exe", "--json");
        var included = fixture.Cli("ignore", "notes.txt");

        human.ExitCode.Should().Be(0, human.Stderr);
        json.ExitCode.Should().Be(0, json.Stderr);
        included.ExitCode.Should().Be(0, included.Stderr);

        Snapshot(fixture.Temp.Root).Should().Be(before);
        File.ReadAllText(stateFile).Should().Be("sentinel-not-a-real-db");
        Directory.Exists(Path.Combine(fixture.Temp.Root, "logs")).Should().BeFalse();
    }

    private static string Snapshot(string root) =>
        string.Join('\n', Directory
            .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root, path);
                if (Directory.Exists(path))
                    return "D " + relative;
                var bytes = File.ReadAllBytes(path);
                return $"F {relative} {bytes.Length} {Convert.ToHexString(SHA1.HashData(bytes))}";
            }));

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public Fixture(string? ignoreText = null, string[]? exclude = null, bool secondSync = false)
        {
            DocsRoot = _temp.Combine("docs");
            OtherRoot = _temp.Combine("other");
            StateDir = _temp.Combine("state");
            Directory.CreateDirectory(DocsRoot);
            Directory.CreateDirectory(OtherRoot);
            Directory.CreateDirectory(StateDir);

            if (ignoreText is not null)
                _temp.Write("docs/.pdignore", ignoreText);

            var excludeBlock = exclude is null
                ? string.Empty
                : "    exclude:\n" + string.Join('\n', exclude.Select(pattern => $"      - \"{pattern}\"")) + "\n";
            var otherBlock = secondSync
                ? $"""

                      other:
                        local: "{OtherRoot.Replace('\\', '/')}"
                        remote: /my-files/other
                    """
                : string.Empty;

            ConfigPath = _temp.Write("pdsync.yaml", $"""
                version: 1
                stateDir: "{_temp.YamlPath("state")}"
                defaults:
                  settleSeconds: 0
                syncs:
                  docs:
                    local: "{DocsRoot.Replace('\\', '/')}"
                    remote: /my-files/docs
                {excludeBlock}{otherBlock}
                """);
        }

        public string DocsRoot { get; }
        public string OtherRoot { get; }
        public string StateDir { get; }
        public string ConfigPath { get; }
        public TempDirectory Temp => _temp;

        public CliResult Cli(params string[] args)
        {
            var all = args.Append("--config").Append(ConfigPath).ToArray();
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = CliApp.Run(all, stdout, stderr);
            return new CliResult(exit, stdout.ToString(), stderr.ToString());
        }

        public void Dispose() => _temp.Dispose();
    }
}
