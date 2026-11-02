using FluentAssertions;
using PdSync.Proton;
using Xunit;

namespace PdSync.Tests;

public sealed class CliVersionTests
{
    [Theory]
    [InlineData("Proton Drive CLI cli-drive@0.9.0+75612299", "0.9.0")]
    [InlineData("Fake Proton Drive CLI 0.9.0 (pdsync fake)", "0.9.0")]
    [InlineData("0.9.0", "0.9.0")]
    [InlineData("0.9.0+meta", "0.9.0")]
    [InlineData("0.9.0-beta.1", "0.9.0-beta.1")]
    [InlineData("cli-drive@1.2.3-rc.2+build.7", "1.2.3-rc.2")]
    public void TryParse_ExtractsVersionFromArbitraryLines(string line, string expected)
    {
        CliVersion.TryParse(line, out var version).Should().BeTrue();

        version.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("0.9")]
    [InlineData("version 1.2")]
    public void TryParse_RejectsJunk(string? line)
    {
        CliVersion.TryParse(line, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("0.9.0", true)]
    [InlineData("  v1.2.3  ", true)]
    [InlineData("0.9.0-beta.1", true)]
    [InlineData("0.9.0+meta", true)]
    [InlineData("0.9", false)]
    [InlineData("0.9.0.1.2", false)]
    [InlineData("abc", false)]
    public void TryParseStrict_RequiresTheWholeValue(string text, bool expected)
    {
        CliVersion.TryParseStrict(text, out _).Should().Be(expected);
    }

    [Theory]
    [InlineData("0.8.5", "0.9.0")]
    [InlineData("0.9.0-beta.1", "0.9.0")]
    [InlineData("0.9.0", "0.9.0-beta.1")]
    [InlineData("0.9.0", "0.9.1")]
    [InlineData("1.0.0", "0.9.0")]
    public void CompareTo_OrdersBySemVer(string left, string right)
    {
        CliVersion.TryParse(left, out var a).Should().BeTrue();
        CliVersion.TryParse(right, out var b).Should().BeTrue();

        a.CompareTo(b).Should().Be(-b.CompareTo(a));
        if (left != right)
            a.CompareTo(b).Should().NotBe(0);
    }

    [Fact]
    public void CompareTo_UsesNumericComponentsNotLexicalOrder()
    {
        CliVersion.TryParse("0.10.0", out var newerMinor).Should().BeTrue();
        CliVersion.TryParse("0.9.0", out var lowerMinor).Should().BeTrue();
        CliVersion.TryParse("0.9.10", out var newerPatch).Should().BeTrue();
        CliVersion.TryParse("0.9.9", out var lowerPatch).Should().BeTrue();

        newerMinor.CompareTo(lowerMinor).Should().BeGreaterThan(0);
        lowerMinor.CompareTo(newerMinor).Should().BeLessThan(0);
        newerMinor.IsAtLeast(lowerMinor).Should().BeTrue();
        lowerMinor.IsAtLeast(newerMinor).Should().BeFalse();

        newerPatch.CompareTo(lowerPatch).Should().BeGreaterThan(0);
        lowerPatch.CompareTo(newerPatch).Should().BeLessThan(0);

        // Exercise the same ordering through the gate used by plan/run/doctor.
        ProtonVersionGate.Evaluate("proton-drive", "Fake Proton Drive CLI 0.10.0 (pdsync fake)", "0.9.0")
            .Supported.Should().BeTrue();
        ProtonVersionGate.Evaluate("proton-drive", "Fake Proton Drive CLI 0.9.10 (pdsync fake)", "0.9.9")
            .Supported.Should().BeTrue();
    }

    [Fact]
    public void Prerelease_RanksBelowSameRelease()
    {
        CliVersion.TryParse("0.9.0-beta.1", out var prerelease).Should().BeTrue();
        CliVersion.TryParse("0.9.0", out var release).Should().BeTrue();

        prerelease.IsAtLeast(release).Should().BeFalse();
        release.IsAtLeast(prerelease).Should().BeTrue();
    }

    [Fact]
    public void Gate_OlderVersionFailsWithClearMessage()
    {
        var result = ProtonVersionGate.Evaluate(
            "C:/Tools/proton-drive.exe",
            "Proton Drive CLI cli-drive@0.8.5+123",
            "0.9.0");

        result.Supported.Should().BeFalse();
        result.ParsedVersion!.ToString().Should().Be("0.8.5");
        result.Message.Should().Be(
            "proton-drive 0.8.5 (C:/Tools/proton-drive.exe) is older than proton.minVersion 0.9.0; " +
            "upgrade the CLI or lower the setting");
    }

    [Theory]
    [InlineData("Proton Drive CLI cli-drive@0.9.0+75612299")]
    [InlineData("Fake Proton Drive CLI 0.9.0 (pdsync fake)")]
    [InlineData("Proton Drive CLI cli-drive@1.0.0")]
    public void Gate_EqualOrNewerVersionPasses(string line)
    {
        var result = ProtonVersionGate.Evaluate("proton-drive", line, "0.9.0");

        result.Supported.Should().BeTrue();
        result.Message.Should().BeNull();
    }

    [Fact]
    public void Gate_UnparsableDetectedVersionFailsClosed()
    {
        var result = ProtonVersionGate.Evaluate("proton-drive", "unknown", "0.9.0");

        result.Supported.Should().BeFalse();
        result.Message.Should().Contain("cannot verify").And.Contain("proton.minVersion is 0.9.0");
    }
}
