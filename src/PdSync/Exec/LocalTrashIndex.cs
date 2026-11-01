using System.Globalization;
using System.Text.Json;
using PdSync.Config;

namespace PdSync.Exec;

/// <summary>
/// Read-only inventory of <c>&lt;stateDir&gt;/trash/&lt;syncId&gt;/&lt;runId&gt;</c>
/// plus the guarded delete used by <c>pdsync trash purge</c>. Nothing here opens
/// the state DB, takes the run lock, calls Proton, or creates directories
/// (SPEC §7, D11).
/// </summary>
public static class LocalTrashIndex
{
    public sealed record TrashRun(
        string SyncId,
        long RunId,
        DateTimeOffset CreatedAt,
        long FileCount,
        long Bytes,
        bool HasManifest,
        bool Active,
        string Path);

    public static string TrashRoot(PdsyncConfig config) =>
        PathUtil.TrimEnd(Path.GetFullPath(Path.Combine(config.StateDir, "trash")));

    /// <summary>
    /// Lists run directories under the configured trash root. When
    /// <paramref name="syncIds"/> is non-null, only those sync ids are returned.
    /// Missing trash/state directories produce an empty list; they are never
    /// created.
    /// </summary>
    public static IReadOnlyList<TrashRun> Scan(PdsyncConfig config, IReadOnlySet<string>? syncIds = null)
    {
        var trashRoot = TrashRoot(config);
        if (!Directory.Exists(trashRoot))
            return Array.Empty<TrashRun>();

        var runs = new List<TrashRun>();
        foreach (var syncDir in SafeEnumerateDirectories(trashRoot))
        {
            var syncId = Path.GetFileName(syncDir);
            if (!IsValidSegment(syncId))
                continue;
            if (syncIds is not null && !syncIds.Contains(syncId))
                continue;

            var active = IsSyncActive(config, syncId);
            foreach (var runDir in SafeEnumerateDirectories(syncDir))
            {
                var runName = Path.GetFileName(runDir);
                if (!long.TryParse(runName, NumberStyles.None, CultureInfo.InvariantCulture, out var runId) ||
                    !string.Equals(runName, runId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    continue;
                }

                string resolved;
                try
                {
                    resolved = ResolveRunDirectory(trashRoot, syncId, runName);
                }
                catch (SafetyException)
                {
                    continue;
                }

                if (!Directory.Exists(resolved))
                    continue;

                var (fileCount, bytes) = Measure(resolved);
                runs.Add(new TrashRun(
                    syncId,
                    runId,
                    CreatedAt(resolved),
                    fileCount,
                    bytes,
                    HasReadableManifest(resolved),
                    active,
                    resolved));
            }
        }

        runs.Sort((left, right) =>
        {
            var byCreated = left.CreatedAt.CompareTo(right.CreatedAt);
            if (byCreated != 0)
                return byCreated;
            var bySync = string.CompareOrdinal(left.SyncId, right.SyncId);
            return bySync != 0 ? bySync : left.RunId.CompareTo(right.RunId);
        });
        return runs;
    }

    /// <summary>
    /// Returns the canonical path for a run directory and refuses anything that
    /// is not exactly <c>&lt;trashRoot&gt;/&lt;syncId&gt;/&lt;runId&gt;</c>.
    /// </summary>
    public static string ResolveRunDirectory(string trashRoot, string syncId, string runId)
    {
        var root = PathUtil.TrimEnd(Path.GetFullPath(trashRoot));
        if (!IsValidSegment(syncId))
            throw new SafetyException($"trash: invalid sync id '{syncId}'");
        if (!IsValidSegment(runId))
            throw new SafetyException($"trash: invalid run id '{runId}'");

        var candidate = Path.GetFullPath(Path.Combine(root, syncId, runId));
        var relative = Path.GetRelativePath(root, candidate);
        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length != 2 ||
            segments[0] == ".." ||
            segments[1] == ".." ||
            !PathUtil.IsSameOrUnder(candidate, root) ||
            PathUtil.PathComparer.Equals(candidate, root))
        {
            throw new SafetyException(
                $"trash: refusing path outside the trash root: {candidate}");
        }

        return PathUtil.TrimEnd(candidate);
    }

    /// <summary>
    /// Deletes one whole run directory. The path is re-validated against the
    /// trash root immediately before deletion; individual files are never
    /// deleted, and reparse points inside the tree abort the delete.
    /// </summary>
    public static void DeleteRun(PdsyncConfig config, TrashRun run)
    {
        var resolved = ResolveRunDirectory(TrashRoot(config), run.SyncId, run.RunId.ToString(CultureInfo.InvariantCulture));
        if (!PathUtil.PathComparer.Equals(resolved, PathUtil.TrimEnd(run.Path)))
            throw new SafetyException($"trash: run path changed while purging: {run.Path}");

        if (!Directory.Exists(resolved))
            return;

        var attributes = File.GetAttributes(resolved);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new SafetyException($"trash: refusing to purge reparse point: {resolved}");

        DeleteTree(resolved);
    }

    /// <summary>
    /// A lock file means a run may be in progress. Stale locks are never broken
    /// automatically (SPEC §8 rule 6), so the purge skips them and reports it.
    /// </summary>
    public static bool IsSyncActive(PdsyncConfig config, string syncId)
    {
        var lockPath = Path.Combine(config.StateDir, "locks", syncId + ".lock");
        return File.Exists(lockPath);
    }

    private static void DeleteTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new SafetyException($"trash: refusing to purge reparse point: {entry}");

            if ((attributes & FileAttributes.Directory) != 0)
                DeleteTree(entry);
            else
                File.Delete(entry);
        }

        Directory.Delete(directory, recursive: false);
    }

    private static DateTimeOffset CreatedAt(string runDirectory)
    {
        var created = Directory.GetCreationTimeUtc(runDirectory);
        if (created.Year < 1980)
            created = Directory.GetLastWriteTimeUtc(runDirectory);
        return new DateTimeOffset(created, TimeSpan.Zero);
    }

    private static bool HasReadableManifest(string runDirectory)
    {
        var manifestPath = Path.Combine(runDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Counts trashed files and bytes. The per-run <c>manifest.json</c> is
    /// metadata and is not counted; nested reparse points are not traversed.
    /// </summary>
    private static (long FileCount, long Bytes) Measure(string runDirectory)
    {
        long fileCount = 0;
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(runDirectory);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (PathUtil.PathComparer.Equals(directory, runDirectory) &&
                        string.Equals(Path.GetFileName(file), "manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        fileCount++;
                        bytes += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Best effort: an unreadable file still exists but cannot
                        // contribute a reliable size.
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable subtrees; the run directory is still listed.
            }
        }

        return (fileCount, bytes);
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsValidSegment(string value) =>
        value.Length > 0 &&
        value is not "." and not ".." &&
        !Path.IsPathRooted(value) &&
        value.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':' }) < 0 &&
        !value.Any(char.IsControl);
}
