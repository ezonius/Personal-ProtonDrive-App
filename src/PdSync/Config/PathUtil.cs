using System.Text.RegularExpressions;

namespace PdSync.Config;

/// <summary>
/// Path expansion and comparison helpers shared by the config loader, state
/// store, and scanner. All paths handed to the engine are absolute and have no
/// trailing separator except for a filesystem root.
/// </summary>
public static partial class PathUtil
{
    private static readonly Regex BraceVariable = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private static readonly Regex PercentVariable = new(@"%([A-Za-z_][A-Za-z0-9_]*)%", RegexOptions.Compiled);

    public static bool IsCaseInsensitive =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static StringComparer PathComparer => IsCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Expands <c>${VAR}</c> and <c>%VAR%</c>. An unset variable is a config
    /// error rather than silently producing a path with a literal <c>%VAR%</c>.
    /// </summary>
    public static string ExpandVariables(string value, string field = "path")
    {
        var missing = new List<string>();

        var expanded = BraceVariable.Replace(value, match => Lookup(match.Groups[1].Value, missing));
        expanded = PercentVariable.Replace(expanded, match => Lookup(match.Groups[1].Value, missing));

        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Distinct(StringComparer.Ordinal));
            throw new ConfigException($"{field}: environment variable(s) not set: {names}");
        }

        return expanded;
    }

    /// <summary>Expands environment variables and a leading <c>~</c>.</summary>
    public static string Expand(string value, string field = "path")
    {
        var expanded = ExpandVariables(value, field);
        if (expanded == "~")
            return HomeDirectory;

        if (expanded.StartsWith("~/", StringComparison.Ordinal) ||
            expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(HomeDirectory, expanded[2..]);
        }

        return expanded;
    }

    /// <summary>
    /// Expands, makes absolute (relative paths are resolved against
    /// <paramref name="baseDirectory"/> or the current directory), and trims
    /// trailing separators while preserving a root such as <c>C:\</c>.
    /// </summary>
    public static string ToAbsolute(string value, string? baseDirectory = null, string field = "path")
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ConfigException($"{field}: path is empty");

        var expanded = Expand(value, field);
        if (!Path.IsPathFullyQualified(expanded))
        {
            if (baseDirectory is null)
                throw new ConfigException($"{field}: path must be absolute: {value}");
            expanded = Path.Combine(baseDirectory, expanded);
        }

        return TrimEnd(Path.GetFullPath(expanded));
    }

    public static string TrimEnd(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var root = Path.GetPathRoot(path);
        if (root is not null && PathComparer.Equals(path, root))
            return root;

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsDriveOrFilesystemRoot(string path)
    {
        var full = TrimEnd(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full);
        return root is not null && PathComparer.Equals(TrimEnd(root), full);
    }

    /// <summary>True when <paramref name="child"/> is <paramref name="parent"/> or inside it.</summary>
    public static bool IsSameOrUnder(string child, string parent)
    {
        var c = TrimEnd(Path.GetFullPath(child));
        var p = TrimEnd(Path.GetFullPath(parent));

        if (PathComparer.Equals(c, p))
            return true;

        var prefix = p.EndsWith(Path.DirectorySeparatorChar) || p.EndsWith(Path.AltDirectorySeparatorChar)
            ? p
            : p + Path.DirectorySeparatorChar;

        return c.StartsWith(prefix, IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Normalizes a local path for state identity (NFC + case-folded).</summary>
    public static string IdentityKey(string path) => PathKey.Fold(ToAbsolute(path));

    private static string Lookup(string name, List<string> missing)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null)
        {
            missing.Add(name);
            return string.Empty;
        }

        return value;
    }
}
