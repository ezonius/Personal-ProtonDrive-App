namespace PdSync.Proton;

/// <summary>
/// Validation and normalization for remote paths. The CLI accepts POSIX-style
/// paths under fixed sections; v1 only syncs the file trees, not photos, albums,
/// or trash.
/// </summary>
public static class RemotePath
{
    public static readonly IReadOnlyList<string> Sections = new[]
    {
        "/my-files",
        "/devices",
        "/shared-by-me",
        "/shared-with-me",
        "/trash",
        "/albums",
        "/photos",
        "/photos-shared-by-me",
        "/photos-shared-with-me",
        "/photos-trash",
    };

    /// <summary>Sections a sync root may live in.</summary>
    public static readonly IReadOnlyList<string> SyncableSections = new[]
    {
        "/my-files",
        "/devices",
        "/shared-by-me",
        "/shared-with-me",
    };

    /// <summary>
    /// Normalizes a remote path: leading slash, no trailing slash, no empty,
    /// <c>.</c>, or <c>..</c> segments, no backslashes or control characters.
    /// Throws <see cref="FormatException"/> with a user-facing message.
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new FormatException("remote path is empty");

        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/'))
            throw new FormatException("remote path must start with '/'");
        if (trimmed.Contains('\\'))
            throw new FormatException("remote path must use '/' separators");
        if (trimmed.Any(char.IsControl))
            throw new FormatException("remote path contains control characters");

        while (trimmed.Length > 1 && trimmed.EndsWith('/'))
            trimmed = trimmed[..^1];

        if (trimmed == "/")
            return "/";

        var segments = trimmed.Split('/')
            .Skip(1)
            .Where(segment => segment.Length > 0)
            .ToArray();

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                throw new FormatException("remote path contains a '.' or '..' segment");
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>True when <paramref name="path"/> is a syncable section itself.</summary>
    public static bool IsSectionRoot(string normalizedPath) =>
        SyncableSections.Any(section =>
            string.Equals(section, normalizedPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when <paramref name="path"/> is inside a syncable section.</summary>
    public static bool IsSyncable(string normalizedPath)
    {
        if (normalizedPath == "/")
            return false;

        foreach (var section in SyncableSections)
        {
            if (normalizedPath.StartsWith(section + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Case-folded key used for state identity and overlap checks. Remote names
    /// are case-sensitive on Proton, but pdsync never syncs case, so two paths
    /// that differ only by case are the same entry.
    /// </summary>
    public static string IdentityKey(string normalizedPath) =>
        PdSync.Config.PathKey.Fold(normalizedPath);

    /// <summary>True when <paramref name="child"/> is <paramref name="parent"/> or below it.</summary>
    public static bool IsSameOrUnder(string child, string parent)
    {
        var c = IdentityKey(Normalize(child));
        var p = IdentityKey(Normalize(parent));
        if (c == p)
            return true;

        return c.StartsWith(p == "/" ? "/" : p + "/", StringComparison.Ordinal);
    }
}
