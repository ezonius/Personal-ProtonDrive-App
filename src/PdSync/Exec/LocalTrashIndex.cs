using System.Globalization;
using System.Text.Json;
using PdSync.Config;

namespace PdSync.Exec;

/// <summary>
/// Read-only inventory of <c>&lt;stateDir&gt;/trash/&lt;syncId&gt;/&lt;runId&gt;</c>
/// plus the guarded delete used by <c>pdsync trash purge</c>. Nothing here opens
/// the state DB, takes the run lock, calls Proton, or creates directories
/// (SPEC §7, D11).
///
/// Safety is real-path based: the trash root, every sync-id directory, every
/// run directory, and every entry inside a run tree are checked for reparse
/// points (junctions/symlinks) before anything is measured or deleted.
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

    /// <summary>A trash entry that was refused before it could be measured or deleted.</summary>
    public sealed record TrashSkip(string SyncId, long? RunId, string Path, string Reason);

    public sealed record TrashScanResult(
        IReadOnlyList<TrashRun> Runs,
        IReadOnlyList<TrashSkip> Skipped);

    public static string TrashRoot(PdsyncConfig config) =>
        PathUtil.TrimEnd(Path.GetFullPath(Path.Combine(config.StateDir, "trash")));

    /// <summary>
    /// Lists run directories under the configured trash root. When
    /// <paramref name="syncIds"/> is non-null, only those sync ids are returned.
    /// Missing trash/state directories produce an empty list; they are never
    /// created. Directories whose real path leaves the trash root, or whose tree
    /// contains a reparse point, are returned in <see cref="TrashScanResult.Skipped"/>
    /// instead of being measured.
    /// </summary>
    public static TrashScanResult Scan(PdsyncConfig config, IReadOnlySet<string>? syncIds = null)
    {
        var trashRoot = TrashRoot(config);
        var runs = new List<TrashRun>();
        var skipped = new List<TrashSkip>();

        if (!Directory.Exists(trashRoot))
            return new TrashScanResult(runs, skipped);

        // The root itself must not be a link to somewhere else. Refuse the whole
        // scan rather than reporting or deleting through it.
        var rootReason = CheckDirectorySafety(trashRoot, trashRoot);
        if (rootReason is not null)
            throw new SafetyException($"trash: refusing trash root '{trashRoot}': {rootReason}");

        foreach (var syncDir in SafeEnumerateDirectories(trashRoot))
        {
            var syncId = Path.GetFileName(syncDir);
            if (!IsValidSegment(syncId))
                continue;
            if (syncIds is not null && !syncIds.Contains(syncId))
                continue;

            // Never enumerate through a sync-id junction: doing so would list or
            // delete files outside the trash root.
            var syncReason = CheckDirectorySafety(syncDir, trashRoot);
            if (syncReason is not null)
            {
                skipped.Add(new TrashSkip(syncId, null, syncDir, syncReason));
                continue;
            }

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
                catch (SafetyException ex)
                {
                    skipped.Add(new TrashSkip(syncId, runId, runDir, Describe(ex)));
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
        return new TrashScanResult(runs, skipped);
    }

    /// <summary>
    /// Returns the canonical path for a run directory and refuses anything that
    /// is not exactly <c>&lt;trashRoot&gt;/&lt;syncId&gt;/&lt;runId&gt;</c>.
    /// Existing directories are checked for reparse points and real-path
    /// containment; an existing run tree is preflighted for nested reparse
    /// points before it can be measured or deleted.
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

        ValidateExistingDirectory(root, root, "trash root");
        var syncDirectory = Path.Combine(root, syncId);
        ValidateExistingDirectory(syncDirectory, root, $"trash sync directory '{syncId}'");
        ValidateExistingDirectory(candidate, root, $"trash run directory '{runId}'");

        if (Directory.Exists(candidate))
            PreflightTree(candidate);

        return PathUtil.TrimEnd(candidate);
    }

    /// <summary>
    /// Deletes one whole run directory. The path and its complete tree are
    /// re-validated immediately before deletion: every ancestor and every entry
    /// must be free of reparse points, and the tree is preflighted in full before
    /// the first byte is removed.
    /// </summary>
    public static void DeleteRun(PdsyncConfig config, TrashRun run)
    {
        var resolved = ResolveRunDirectory(TrashRoot(config), run.SyncId, run.RunId.ToString(CultureInfo.InvariantCulture));
        if (!PathUtil.PathComparer.Equals(resolved, PathUtil.TrimEnd(run.Path)))
            throw new SafetyException($"trash: run path changed while purging: {run.Path}");

        if (!Directory.Exists(resolved))
            return;

        // Two-phase delete: the whole tree was already preflighted by
        // ResolveRunDirectory. Delete only after that succeeded.
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

    private static void ValidateExistingDirectory(string path, string trashRoot, string label)
    {
        var reason = CheckDirectorySafety(path, trashRoot);
        if (reason is not null)
            throw new SafetyException($"trash: refusing {label} '{path}': {reason}");
    }

    /// <summary>
    /// Returns a refusal reason when an existing directory is a reparse point or
    /// resolves outside <paramref name="trashRoot"/>; null when it is safe or
    /// absent.
    /// </summary>
    private static string? CheckDirectorySafety(string path, string trashRoot)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return "reparse point";

        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null)
            return "reparse point";

        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        var resolved = target is null ? Path.GetFullPath(path) : Path.GetFullPath(target.FullName);
        if (!PathUtil.IsSameOrUnder(resolved, Path.GetFullPath(trashRoot)))
            return "resolves outside the trash root";

        return null;
    }

    /// <summary>
    /// Walks the complete run tree and throws before any deletion if any entry is
    /// a reparse point or cannot be read. A reparse point is never traversed.
    /// </summary>
    private static void PreflightTree(string runDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(runDirectory);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SafetyException($"trash: refusing unreadable trash directory '{directory}': {ex.Message}");
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new SafetyException($"trash: refusing unreadable trash entry '{entry}': {ex.Message}");
                }

                var info = (attributes & FileAttributes.Directory) != 0
                    ? (FileSystemInfo)new DirectoryInfo(entry)
                    : new FileInfo(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                    throw new SafetyException($"trash: refusing nested reparse point: {entry}");

                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
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
        var lastWrite = Directory.GetLastWriteTimeUtc(runDirectory);
        var now = DateTime.UtcNow;
        var effective = created.Year >= 1980 && created <= now.AddDays(1)
            ? (created < lastWrite ? created : lastWrite)
            : lastWrite;
        return new DateTimeOffset(effective, TimeSpan.Zero);
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
    /// metadata and is not counted. The tree was preflighted, so no reparse
    /// point is traversed here.
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

    private static string Describe(SafetyException exception)
    {
        const string prefix = "trash: ";
        return exception.Message.StartsWith(prefix, StringComparison.Ordinal)
            ? exception.Message[prefix.Length..]
            : exception.Message;
    }

    private static bool IsValidSegment(string value) =>
        value.Length > 0 &&
        value is not "." and not ".." &&
        !Path.IsPathRooted(value) &&
        value.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':' }) < 0 &&
        !value.Any(char.IsControl);
}
