using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdSync.Config;
using PdSync.Exec;
using PdSync.State;

namespace PdSync.Cli;

/// <summary>
/// <c>pdsync trash list|purge</c> (SPEC §3.1, §7, D11). Local-only: it reads or
/// deletes under <c>&lt;stateDir&gt;/trash</c> and never opens a state DB, takes
/// a run lock, or calls Proton.
/// </summary>
public static class TrashCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record ListJsonRow(
        string SyncId,
        long RunId,
        DateTimeOffset CreatedAt,
        long FileCount,
        long Bytes,
        bool HasManifest);

    private sealed record PurgeJsonRow(
        string SyncId,
        long RunId,
        string Path,
        DateTimeOffset CreatedAt,
        long FileCount,
        long Bytes,
        bool HasManifest);

    private sealed record SkippedJsonRow(string SyncId, long RunId, string Path, string Reason);

    public static int ExecuteList(
        PdsyncConfig config,
        IReadOnlyList<string> syncNames,
        bool json,
        TextWriter output,
        TextWriter error)
    {
        var scan = LocalTrashIndex.Scan(config, ResolveSyncIds(config, syncNames));
        var runs = scan.Runs;
        if (json)
        {
            WriteJson(output, runs.Select(run => new ListJsonRow(
                run.SyncId, run.RunId, run.CreatedAt, run.FileCount, run.Bytes, run.HasManifest)));
            // Keep stdout a stable JSON array; unsafe entries are diagnostics on
            // stderr rather than silently copied into the inventory.
            WriteSkips(scan.Skipped, error);
            return 0;
        }

        if (runs.Count == 0)
            output.WriteLine("trash: no local trash runs");
        else
        {
            var names = SyncNamesById(config);
            output.WriteLine($"trash: {runs.Count} run(s) under {LocalTrashIndex.TrashRoot(config)}");
            foreach (var run in runs)
            {
                var name = names.TryGetValue(run.SyncId, out var configured) ? $" ({configured})" : string.Empty;
                output.WriteLine(
                    $"  sync {run.SyncId}{name} run {run.RunId}  " +
                    $"{run.CreatedAt:yyyy-MM-ddTHH:mm:ssZ}  " +
                    $"{run.FileCount} file(s), {run.Bytes} bytes, " +
                    $"manifest: {(run.HasManifest ? "yes" : "no")}" +
                    (run.Active ? "  (active run, purge will skip)" : string.Empty));
                output.WriteLine($"    {run.Path}");
            }
        }

        WriteSkips(scan.Skipped, output);
        return 0;
    }

    private static void WriteSkips(IReadOnlyList<LocalTrashIndex.TrashSkip> skips, TextWriter output)
    {
        foreach (var skip in skips)
        {
            var run = skip.RunId is null ? string.Empty : $" run {skip.RunId}";
            output.WriteLine($"  skip unsafe sync {skip.SyncId}{run}  {skip.Path}  ({skip.Reason})");
        }
    }

    public static int ExecutePurge(
        PdsyncConfig config,
        IReadOnlyList<string> syncNames,
        int olderThanDays,
        bool dryRun,
        bool yes,
        bool json,
        TextReader input,
        TextWriter output,
        TextWriter error)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-olderThanDays);
        var scan = LocalTrashIndex.Scan(config, ResolveSyncIds(config, syncNames));
        if (scan.Skipped.Count > 0)
        {
            var details = string.Join("; ", scan.Skipped.Select(skip => $"{skip.Path} ({skip.Reason})"));
            throw new SafetyException($"trash: refusing to purge unsafe trash entries: {details}");
        }

        var runs = scan.Runs;

        var eligible = runs
            .Where(run => !run.Active && run.CreatedAt < cutoff)
            .ToList();
        var skippedActive = runs
            .Where(run => run.Active && run.CreatedAt < cutoff)
            .ToList();

        if (!dryRun && eligible.Count > 0 && !yes)
        {
            var promptOutput = json ? error : output;
            promptOutput.Write(
                $"purge {eligible.Count} trash run(s) older than {olderThanDays} day(s) " +
                $"from {LocalTrashIndex.TrashRoot(config)}? [y/N]: ");
            var answer = input.ReadLine();
            if (answer is null)
            {
                promptOutput.WriteLine();
                return WriteCancelled(config, olderThanDays, cutoff, dryRun, skippedActive, json, output);
            }

            answer = answer.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return WriteCancelled(config, olderThanDays, cutoff, dryRun, skippedActive, json, output);
            }
        }

        var removed = new List<LocalTrashIndex.TrashRun>();
        if (!dryRun)
        {
            foreach (var run in eligible)
            {
                // A run may have started after the scan; re-check the lock at
                // the last possible moment and report it as skipped.
                if (LocalTrashIndex.IsSyncActive(config, run.SyncId))
                {
                    skippedActive.Add(run);
                    continue;
                }

                LocalTrashIndex.DeleteRun(config, run);
                removed.Add(run);
            }
        }

        if (json)
        {
            WriteJson(output, new
            {
                command = "trash-purge",
                olderThanDays,
                cutoff,
                dryRun,
                removed = (dryRun ? eligible : removed).Select(PurgeRow),
                skippedActive = skippedActive.Select(run =>
                    new SkippedJsonRow(run.SyncId, run.RunId, run.Path, "active-run-lock")),
            });
            return 0;
        }

        if (dryRun)
        {
            output.WriteLine($"trash purge --dry-run: {eligible.Count} run(s) would be removed (older than {olderThanDays} day(s), cutoff {cutoff:O})");
        }
        else
        {
            output.WriteLine($"trash purge: removed {removed.Count} run(s) older than {olderThanDays} day(s) (cutoff {cutoff:O})");
        }

        foreach (var run in dryRun ? eligible : removed)
            output.WriteLine($"  {(dryRun ? "would remove" : "removed")} {run.SyncId}/{run.RunId}  {run.Path}");
        foreach (var run in skippedActive)
            output.WriteLine($"  skip active {run.SyncId}/{run.RunId}  {run.Path}");
        if (eligible.Count == 0 && skippedActive.Count == 0)
            output.WriteLine("  nothing to remove");
        return 0;
    }

    private static int WriteCancelled(
        PdsyncConfig config,
        int olderThanDays,
        DateTimeOffset cutoff,
        bool dryRun,
        IReadOnlyList<LocalTrashIndex.TrashRun> skippedActive,
        bool json,
        TextWriter output)
    {
        if (json)
        {
            WriteJson(output, new
            {
                command = "trash-purge",
                olderThanDays,
                cutoff,
                dryRun,
                removed = Array.Empty<PurgeJsonRow>(),
                skippedActive = skippedActive.Select(run =>
                    new SkippedJsonRow(run.SyncId, run.RunId, run.Path, "active-run-lock")),
                aborted = true,
            });
            return 0;
        }

        output.WriteLine("trash purge: cancelled; nothing removed");
        foreach (var run in skippedActive)
            output.WriteLine($"  skip active {run.SyncId}/{run.RunId}  {run.Path}");
        return 0;
    }

    private static PurgeJsonRow PurgeRow(LocalTrashIndex.TrashRun run) =>
        new(run.SyncId, run.RunId, run.Path, run.CreatedAt, run.FileCount, run.Bytes, run.HasManifest);

    /// <summary>
    /// Resolves <c>--sync</c> names to sync ids. Unlike plan/run this accepts a
    /// disabled sync: retention applies to any configured sync. With no names,
    /// every trash directory is considered, including orphaned sync ids.
    /// </summary>
    private static IReadOnlySet<string>? ResolveSyncIds(PdsyncConfig config, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return null;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var match = config.Syncs.FirstOrDefault(sync =>
                string.Equals(sync.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new ConfigException($"unknown sync '{name}'");
            ids.Add(StateStore.ComputeSyncId(match.LocalRoot, match.RemoteRoot));
        }
        return ids;
    }

    private static Dictionary<string, string> SyncNamesById(PdsyncConfig config)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sync in config.Syncs)
            names.TryAdd(StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot), sync.Name);
        return names;
    }

    private static void WriteJson(TextWriter output, object value) =>
        output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
