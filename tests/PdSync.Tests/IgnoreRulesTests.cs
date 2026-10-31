using System.Text;
using FluentAssertions;
using PdSync.Config;
using PdSync.Scan;

namespace PdSync.Tests;

public sealed class IgnoreRulesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private IgnoreRules Load(
        string? patterns,
        IReadOnlyList<string>? excludes = null,
        IgnoreCasePolicy casePolicy = IgnoreCasePolicy.Sensitive)
    {
        if (patterns is not null)
            _temp.Write(".pdignore", patterns);

        return IgnoreRules.Load(_temp.Combine(".pdignore"), excludes, casePolicy);
    }

    [Fact]
    public void MissingFile_IsEmptyAndIncludesEverything()
    {
        var rules = Load(null);

        rules.IsEmpty.Should().BeTrue();
        rules.Rules.Should().BeEmpty();
        rules.IsIgnored("a.txt", isDirectory: false).Should().BeFalse();
        rules.Decide("a.txt", isDirectory: false).Pattern.Should().BeNull();
    }

    [Fact]
    public void BlankAndCommentLines_AreSkipped_AndLineNumbersArePreserved()
    {
        var rules = Load("# comment\n\n*.tmp\n   \nlogs/\n");

        rules.Rules.Select(r => r.Pattern).Should().Equal("*.tmp", "logs/");
        rules.Rules.Select(r => r.Number).Should().Equal(1, 2);
        rules.Rules.Select(r => r.LineNumber).Should().Equal(3, 5);
    }

    [Fact]
    public void GlobAndBasenamePatterns_MatchAtAnyDepth()
    {
        var rules = Load("*.tmp\n");

        rules.IsIgnored("a.tmp", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("sub/a.tmp", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("a.txt", isDirectory: false).Should().BeFalse();
    }

    [Fact]
    public void Negation_ReincludesLater()
    {
        var rules = Load("*.log\n!keep.log\n");

        rules.IsIgnored("other.log", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("keep.log", isDirectory: false).Should().BeFalse();
        rules.IsIgnored("sub/keep.log", isDirectory: false).Should().BeFalse();
    }

    [Fact]
    public void DirectoryOnlyPattern_DoesNotMatchSameNamedFile()
    {
        var rules = Load("logs/\n");

        rules.IsIgnored("logs", isDirectory: true).Should().BeTrue();
        rules.IsIgnored("logs", isDirectory: false).Should().BeFalse();
        rules.IsIgnored("logs/a.txt", isDirectory: false).Should().BeTrue();
    }

    [Fact]
    public void LeadingSlash_AnchorsToTheRoot()
    {
        var rules = Load("/scratch/\n");

        rules.IsIgnored("scratch", isDirectory: true).Should().BeTrue();
        rules.IsIgnored("sub/scratch", isDirectory: true).Should().BeFalse();
    }

    [Fact]
    public void DoubleStar_MatchesZeroOrMoreDirectories()
    {
        var rules = Load("**/.DS_Store\n");

        rules.IsIgnored(".DS_Store", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("a/.DS_Store", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("a/b/.DS_Store", isDirectory: false).Should().BeTrue();
    }

    [Fact]
    public void MatchingIsCaseSensitiveByDefault()
    {
        var rules = Load("*.log\n");

        rules.IsIgnored("a.log", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("A.LOG", isDirectory: false).Should().BeFalse();
    }

    [Fact]
    public void InsensitivePolicyFoldsCase()
    {
        var rules = Load("*.log\n", casePolicy: IgnoreCasePolicy.Insensitive);

        rules.IsIgnored("A.LOG", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("sub/A.Log", isDirectory: false).Should().BeTrue();
    }

    [Fact]
    public void InlineExcludes_AreMergedAfterTheFile()
    {
        var rules = Load("*.tmp\n", excludes: new[] { "!keep.tmp" });

        rules.IsIgnored("other.tmp", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("keep.tmp", isDirectory: false).Should().BeFalse();
        rules.Rules.Select(r => r.Pattern).Should().Equal("*.tmp", "!keep.tmp");
        rules.Rules[1].Source.Should().Be("exclude[0]");
        rules.Rules[1].LineNumber.Should().BeNull();
    }

    [Fact]
    public void Explain_ReturnsTheDecidingRuleAndLine()
    {
        var rules = Load("*.log\n!keep.log\n");

        var kept = rules.Decide("keep.log", isDirectory: false);
        kept.IsIgnored.Should().BeFalse();
        kept.Pattern.Should().Be("!keep.log");
        kept.RuleNumber.Should().Be(2);
        kept.Source.Should().EndWith(":2");
        kept.MatchedPath.Should().Be("keep.log");

        var ignored = rules.Decide("other.log", isDirectory: false);
        ignored.IsIgnored.Should().BeTrue();
        ignored.Pattern.Should().Be("*.log");
        ignored.RuleNumber.Should().Be(1);
    }

    [Fact]
    public void Explain_ReportsTheIgnoredAncestor()
    {
        var rules = Load("logs/\n!logs/keep.log\n");

        var decision = rules.Decide("logs/keep.log", isDirectory: false);

        decision.IsIgnored.Should().BeTrue();
        decision.Pattern.Should().Be("logs/");
        decision.MatchedPath.Should().Be("logs");
        decision.Path.Should().Be("logs/keep.log");
    }

    [Fact]
    public void Explain_NoMatchMeansIncluded()
    {
        var rules = Load("*.tmp\n");

        var decision = rules.Decide("readme.txt", isDirectory: false);

        decision.IsIgnored.Should().BeFalse();
        decision.Pattern.Should().BeNull();
        decision.RuleNumber.Should().BeNull();
    }

    [Theory]
    [InlineData("./sub//a.tmp")]
    [InlineData("sub\\a.tmp")]
    [InlineData("sub/a.tmp")]
    public void Normalization_AcceptsSlashStylesAndRedundantSeparators(string path)
    {
        var rules = Load("*.tmp\n");

        rules.IsIgnored(path, isDirectory: false).Should().BeTrue();
    }

    [Fact]
    public void Normalization_RejectsParentTraversal()
    {
        var rules = Load("*.tmp\n");

        var act = () => rules.IsIgnored("../a.tmp", isDirectory: false);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("*.log\n!keep.log\n")]
    [InlineData("logs/\n!logs/keep.log\n")]
    [InlineData("logs/\n!logs/\n")]
    [InlineData("a/**/b\n")]
    [InlineData("/scratch/\n")]
    [InlineData("**/.DS_Store\n")]
    [InlineData("a\n!a/b\n")]
    [InlineData("foo/**\n")]
    [InlineData("dir/\n!dir/keep/\n")]
    [InlineData("*.log\n")]
    public void Decide_AgreesWithTheMabVerdict(string patterns)
    {
        var rules = Load(patterns);
        var paths = new[]
        {
            "a.log", "keep.log", "sub/a.log", "sub/keep.log",
            "logs", "logs/keep.log", "logs/other.log",
            "scratch", "sub/scratch",
            "a", "a/b", "a/x/b",
            "foo", "foo/a", "foo/a/b",
            ".DS_Store", "x/.DS_Store",
            "dir", "dir/keep", "dir/keep/x", "dir/other",
            "name.txt",
        };

        foreach (var path in paths)
        {
            foreach (var isDirectory in new[] { false, true })
            {
                rules.Decide(path, isDirectory).IsIgnored.Should().Be(
                    rules.IsIgnored(path, isDirectory),
                    $"path={path}, isDirectory={isDirectory}");
            }
        }
    }

    [Fact]
    public void Decide_AgreesWithTheMabVerdict_CaseInsensitive()
    {
        var rules = Load("*.log\n!keep.log\n", casePolicy: IgnoreCasePolicy.Insensitive);

        foreach (var path in new[] { "A.LOG", "KEEP.LOG", "sub/Other.Log" })
        {
            rules.Decide(path, isDirectory: false).IsIgnored.Should().Be(
                rules.IsIgnored(path, isDirectory: false),
                path);
        }
    }

    [Fact]
    public void InvalidPattern_ThrowsWithSourceLine()
    {
        var ex = Assert.Throws<IgnoreRulesException>(() => Load("*.tmp\n[\n"));

        ex.LineNumber.Should().Be(2);
        ex.SourceLabel.Should().EndWith(":2");
        ex.Pattern.Should().Be("[");
    }

    [Fact]
    public void IgnorePathThatIsADirectory_Throws()
    {
        var act = () => IgnoreRules.Load(_temp.Root);
        act.Should().Throw<IgnoreRulesException>();
    }

    [Fact]
    public void EmptyInlinePattern_Throws()
    {
        var act = () => IgnoreRules.Load(_temp.Combine(".pdignore"), new[] { "" });
        act.Should().Throw<IgnoreRulesException>().WithMessage("*empty*");
    }

    [Fact]
    public void Utf8Bom_IsIgnored()
    {
        var path = _temp.Combine(".pdignore");
        File.WriteAllText(path, "\uFEFF*.tmp\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var rules = IgnoreRules.Load(path);

        rules.Rules.Should().ContainSingle();
        rules.Rules[0].Pattern.Should().Be("*.tmp");
        rules.IsIgnored("a.tmp", isDirectory: false).Should().BeTrue();
    }

    [Fact]
    public void ConfigPolicyAndInlinePatterns_AreUsedByTheSyncOverload()
    {
        _temp.Write("local/.pdignore", "*.tmp\n");
        var config = ConfigTestHelper.Load(
            _temp,
            ConfigTestHelper.MinimalYaml(
                _temp,
                extraSync: """
                        exclude:
                          - "!keep.tmp"
                    """,
                extraDefaults: """
                    defaults:
                      ignoreCase: insensitive
                    """));

        var rules = IgnoreRules.Load(config.Syncs[0]);

        rules.IsIgnored("other.tmp", isDirectory: false).Should().BeTrue();
        rules.IsIgnored("KEEP.TMP", isDirectory: false).Should().BeFalse();
    }
}
