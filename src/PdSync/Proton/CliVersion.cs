using System.Globalization;
using System.Text.RegularExpressions;

namespace PdSync.Proton;

/// <summary>
/// A tolerant <c>major.minor.patch</c> version (with an optional SemVer
/// pre-release suffix) parsed out of an arbitrary CLI version line.
/// </summary>
/// <remarks>
/// The official CLI prints <c>Proton Drive CLI cli-drive@0.9.0+75612299</c> and
/// the fake prints <c>Fake Proton Drive CLI 0.9.0 (pdsync fake)</c>; neither is
/// a bare version. Parsing searches for the first dotted triple instead of
/// trusting the whole line, and ignores build metadata.
/// </remarks>
public sealed record CliVersion(int Major, int Minor, int Patch, string? Prerelease)
    : IComparable<CliVersion>
{
    private static readonly Regex VersionPattern = new(
        @"(?<!\d)(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<pre>[0-9A-Za-z][0-9A-Za-z.-]*))?(?:\+[0-9A-Za-z.-]+)?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StrictVersionPattern = new(
        @"^\s*v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<pre>[0-9A-Za-z][0-9A-Za-z.-]*))?(?:\+[0-9A-Za-z.-]+)?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Tolerant parse for arbitrary CLI output: takes the first dotted triple
    /// anywhere in the line.
    /// </summary>
    public static bool TryParse(string? text, out CliVersion version)
    {
        version = default!;
        return !string.IsNullOrWhiteSpace(text) && TryBuild(VersionPattern.Match(text), out version);
    }

    /// <summary>
    /// Strict parse for configuration values: the whole trimmed value must be a
    /// version (an optional leading <c>v</c> and build metadata are accepted).
    /// </summary>
    public static bool TryParseStrict(string? text, out CliVersion version)
    {
        version = default!;
        return !string.IsNullOrWhiteSpace(text) && TryBuild(StrictVersionPattern.Match(text), out version);
    }

    private static bool TryBuild(Match match, out CliVersion version)
    {
        version = default!;
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;
        version = new CliVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(CliVersion? other)
    {
        if (other is null)
            return 1;

        var result = Major.CompareTo(other.Major);
        if (result != 0)
            return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0)
            return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0)
            return result;

        // A release ranks above any pre-release of the same triple.
        if (Prerelease is null && other.Prerelease is null)
            return 0;
        if (Prerelease is null)
            return 1;
        if (other.Prerelease is null)
            return -1;

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    public bool IsAtLeast(CliVersion other) => CompareTo(other) >= 0;

    public override string ToString() =>
        Prerelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    private static int ComparePrerelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var count = Math.Min(leftParts.Length, rightParts.Length);

        for (var i = 0; i < count; i++)
        {
            var a = leftParts[i];
            var b = rightParts[i];
            var aNumeric = IsNumeric(a);
            var bNumeric = IsNumeric(b);

            int result;
            if (aNumeric && bNumeric)
                result = CompareNumericIdentifiers(a, b);
            else if (aNumeric)
                result = -1;
            else if (bNumeric)
                result = 1;
            else
                result = string.Compare(a, b, StringComparison.Ordinal);

            if (result != 0)
                return result;
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static bool IsNumeric(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit);

    private static int CompareNumericIdentifiers(string left, string right)
    {
        var normalizedLeft = left.TrimStart('0');
        var normalizedRight = right.TrimStart('0');
        if (normalizedLeft.Length == 0)
            normalizedLeft = "0";
        if (normalizedRight.Length == 0)
            normalizedRight = "0";

        var lengthResult = normalizedLeft.Length.CompareTo(normalizedRight.Length);
        return lengthResult != 0
            ? lengthResult
            : string.Compare(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }
}

/// <summary>Outcome of comparing a detected CLI version line with a requirement.</summary>
public sealed record ProtonVersionCheckResult(
    string RawVersion,
    CliVersion? ParsedVersion,
    bool Supported,
    string? Message);

/// <summary>
/// The single startup version gate shared by <c>plan</c>, <c>run</c>, and
/// <c>doctor</c>. It never spawns the CLI; callers pass the raw
/// <see cref="ProtonCli.GetVersion"/> line so auth/spawn failures keep their
/// existing exit-code mapping.
/// </summary>
public static class ProtonVersionGate
{
    public static ProtonVersionCheckResult Evaluate(string executable, string rawVersion, string minVersion)
    {
        if (!CliVersion.TryParse(minVersion, out var required))
        {
            return new ProtonVersionCheckResult(
                rawVersion,
                null,
                false,
                $"proton.minVersion '{minVersion}' is not a valid version (expected major.minor.patch)");
        }

        if (!CliVersion.TryParse(rawVersion, out var actual))
        {
            return new ProtonVersionCheckResult(
                rawVersion,
                null,
                false,
                $"proton-drive: cannot verify the CLI version from '{rawVersion}' ({executable}); " +
                $"proton.minVersion is {required}");
        }

        if (!actual.IsAtLeast(required))
        {
            return new ProtonVersionCheckResult(
                rawVersion,
                actual,
                false,
                $"proton-drive {actual} ({executable}) is older than proton.minVersion {required}; " +
                "upgrade the CLI or lower the setting");
        }

        return new ProtonVersionCheckResult(rawVersion, actual, true, null);
    }
}
