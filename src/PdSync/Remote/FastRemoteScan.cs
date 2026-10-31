using System.Text.Json;
using PdSync.Config;
using PdSync.Scan;
using PdSync.State;

namespace PdSync.Remote;

/// <summary>
/// Fast-mode helpers (SPEC §6.3). Fast mode may only skip listing a remote
/// folder when the local subtree is unchanged against the state DB. A skipped
/// folder is rendered from the baseline, so it produces no actions at all.
/// </summary>
public static class FastRemoteScan
{
    public static Func<string, bool>? CreateSkipPredicate(
        IReadOnlyList<EntryRecord> baseline,
        LocalScan local,
        bool enabled)
    {
        if (!enabled || !local.IsComplete || local.Collisions.Count > 0)
            return null;

        var byKey = new Dictionary<string, EntryRecord>(StringComparer.Ordinal);
        foreach (var entry in baseline)
            byKey[entry.PathKey] = entry;

        return folder =>
        {
            var folderKey = PathKey.Fold(folder);
            if (!byKey.TryGetValue(folderKey, out var baseFolder) ||
                baseFolder.Kind != EntryKind.Folder ||
                baseFolder.Frozen ||
                !baseFolder.RemotePresent)
                return false;

            if (!local.ByKey.TryGetValue(folderKey, out var localFolder) || !localFolder.IsDirectory)
                return false;

            var prefix = folderKey + "/";
            foreach (var entry in byKey.Values)
            {
                if (entry.PathKey != folderKey && !entry.PathKey.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                if (entry.Frozen || !entry.RemotePresent)
                    return false;
                if (!local.ByKey.TryGetValue(entry.PathKey, out var localEntry))
                    return false;
                if (entry.Kind == EntryKind.Folder)
                {
                    if (!localEntry.IsDirectory)
                        return false;
                }
                else
                {
                    if (localEntry.IsDirectory || localEntry.Size != entry.Size)
                        return false;
                    if (string.IsNullOrEmpty(entry.Sha1) || string.IsNullOrEmpty(localEntry.Sha1) ||
                        !string.Equals(entry.Sha1, localEntry.Sha1, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }

            foreach (var entry in local.ByKey.Values)
            {
                if (entry.PathKey != folderKey &&
                    !entry.PathKey.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                if (!byKey.ContainsKey(entry.PathKey))
                    return false;
            }

            if (local.IgnoredByKey.Keys.Any(key =>
                    key == folderKey || key.StartsWith(prefix, StringComparison.Ordinal)))
                return false;

            if (local.SkippedByKey.Keys.Any(key =>
                    key == folderKey || key.StartsWith(prefix, StringComparison.Ordinal)))
                return false;

            return true;
        };
    }
}

/// <summary>Persists how many fast runs have happened since the last full listing.</summary>
public static class FastScanTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int ReadRunsSinceFull(PdsyncConfig config, SyncConfig sync)
    {
        var path = PathFor(config, sync);
        try
        {
            if (!File.Exists(path))
                return 0;
            var state = JsonSerializer.Deserialize<FastCounter>(File.ReadAllText(path), JsonOptions);
            return Math.Max(0, state?.RunsSinceFull ?? 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return 0;
        }
    }

    public static bool ShouldUseFastListing(
        PdsyncConfig config,
        SyncConfig sync,
        bool fastRequested,
        bool fullRequested,
        out int runsSinceFull)
    {
        runsSinceFull = ReadRunsSinceFull(config, sync);
        if (!fastRequested || fullRequested)
            return false;
        return runsSinceFull < sync.Options.FastFullEveryRuns;
    }

    public static void RecordRun(PdsyncConfig config, SyncConfig sync, bool fullScan)
    {
        var path = PathFor(config, sync);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var next = fullScan ? 0 : ReadRunsSinceFull(config, sync) + 1;
            var json = JsonSerializer.Serialize(
                new FastCounter(next, DateTimeOffset.UtcNow), JsonOptions);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fast bookkeeping must never turn a successful sync into a failure.
        }
    }

    public static string PathFor(PdsyncConfig config, SyncConfig sync) =>
        Path.Combine(
            config.StateDir,
            "state",
            StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot) + ".fast.json");

    private sealed record FastCounter(int RunsSinceFull, DateTimeOffset UpdatedAt);
}
