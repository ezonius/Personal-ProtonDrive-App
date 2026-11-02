using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdSync.Config;
using PdSync.Exec;
using PdSync.Proton;
using PdSync.State;

namespace PdSync.Cli;

/// <summary>
/// <c>pdsync trash</c> (SPEC §3.1, §6.2, §7, D11). <c>list</c> and <c>purge</c>
/// are local-only: they read or delete under <c>&lt;stateDir&gt;/trash</c> and
/// never open a state DB, take a run lock, or call Proton. <c>restore</c> is the
/// one mutating subcommand: it opens the state DB, takes the sync's run lock,
/// lists <c>/trash</c>, and calls <c>fs restore</c> for a uniquely named record.
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

    private sealed record RestorePathResult(
        string Path,
        string Status,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? CandidateUids);

    private sealed record RestoreJsonResult(
        string Command,
        string Sync,
        IReadOnlyList<RestorePathResult> Results);

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

    /// <summary>
    /// <c>pdsync trash restore &lt;sync&gt; &lt;path...&gt;</c> (SPEC §6.2, S15).
    /// Restore is name-addressed only: the CLI cannot address a trashed node by
    /// UID, and name collisions are silently arbitrary. The durable
    /// <c>remote_trash</c> record supplies the expected UID, so this command
    /// restores only when the recorded name identifies exactly one node with
    /// that UID; every other case refuses with exit 2.
    /// </summary>
    public static int ExecuteRestore(
        PdsyncConfig config,
        IReadOnlyList<string> syncNames,
        IReadOnlyList<string> rawPaths,
        bool forget,
        bool json,
        TextWriter output,
        TextWriter error)
    {
        var sync = ResolveSingleSync(config, syncNames);

        var databasePath = Path.Combine(
            config.StateDir, "state",
            StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot) + ".sqlite");
        if (!File.Exists(databasePath))
        {
            error.WriteLine(
                $"pdsync: no state database for sync '{sync.Name}'; run pdsync run first");
            return 1;
        }

        using var runLock = RunLock.Acquire(config, sync);
        using var state = StateStore.Open(config, sync);
        var cli = new ProtonCli(config.Proton.Cli);
        IReadOnlyList<ProtonNode>? trash = null;

        var results = new List<RestorePathResult>();
        var exit = 0;
        foreach (var raw in rawPaths)
        {
            var path = NormalizeRestorePath(raw);
            var pathKey = PathKey.Fold(path);
            var record = state.GetRemoteTrash(pathKey);
            if (record is null)
            {
                results.Add(new RestorePathResult(path, "missing",
                    $"no pdsync trash record for {path}", null));
                exit = Math.Max(exit, 1);
                continue;
            }

            if (forget)
            {
                // Local-only: drop the record without touching Proton.
                state.DeleteRemoteTrash(pathKey);
                results.Add(new RestorePathResult(path, "forgotten", null, null));
                continue;
            }

            if (state.GetEntry(pathKey) is not null)
            {
                results.Add(new RestorePathResult(path, "active",
                    $"path is active again in the sync; refusing to restore the old trashed node " +
                    $"(clear the record with: pdsync trash restore --forget {path})",
                    null));
                exit = Math.Max(exit, 2);
                continue;
            }

            trash ??= cli.List("/trash");
            var leaf = LeafName(record.Path);
            var candidates = trash.Where(node =>
                string.Equals(node.Name, leaf, StringComparison.Ordinal)).ToList();

            if (candidates.Count == 0)
            {
                results.Add(new RestorePathResult(path, "uid-mismatch",
                    $"recorded node {ShortUid(record.RemoteUid)} is no longer in trash; refusing to restore a different node",
                    null));
                exit = Math.Max(exit, 2);
                continue;
            }

            if (candidates.Count > 1)
            {
                var uids = candidates.Select(node => node.Uid).OrderBy(uid => uid, StringComparer.Ordinal).ToList();
                results.Add(new RestorePathResult(path, "ambiguous",
                    $"trash holds {candidates.Count} nodes named '{leaf}'; restore manually in the Proton web app (UID addressing is unsupported by the CLI as of 0.9.0)",
                    uids));
                exit = Math.Max(exit, 2);
                continue;
            }

            var match = candidates[0];
            if (!string.Equals(match.Uid, record.RemoteUid, StringComparison.Ordinal))
            {
                results.Add(new RestorePathResult(path, "uid-mismatch",
                    $"recorded node {ShortUid(record.RemoteUid)} is no longer in trash; refusing to restore a different node",
                    new[] { match.Uid }));
                exit = Math.Max(exit, 2);
                continue;
            }

            try
            {
                cli.Restore("/trash/" + leaf);
            }
            catch (ProtonAuthException)
            {
                throw;
            }
            catch (ProtonCliException ex)
            {
                // Keep the record: the node is still trashed and a later retry
                // may succeed once the conflict is resolved.
                results.Add(new RestorePathResult(path, "failed", ex.Message, null));
                exit = Math.Max(exit, 1);
                continue;
            }

            // A non-zero exit is not the only failure mode; prove the recorded
            // node actually left /trash before deleting its only undo record.
            IReadOnlyList<ProtonNode> after;
            try
            {
                after = cli.List("/trash");
            }
            catch (ProtonAuthException)
            {
                throw;
            }
            catch (ProtonCliException ex)
            {
                results.Add(new RestorePathResult(path, "unverified",
                    $"restore result could not be verified: {ex.Message}", null));
                exit = Math.Max(exit, 1);
                continue;
            }

            if (after.Any(node => string.Equals(node.Uid, record.RemoteUid, StringComparison.Ordinal)))
            {
                results.Add(new RestorePathResult(path, "unverified",
                    $"recorded node {ShortUid(record.RemoteUid)} is still in /trash after restore; " +
                    "keeping the record", null));
                exit = Math.Max(exit, 1);
                continue;
            }

            trash = after;
            state.DeleteRemoteTrash(pathKey);
            results.Add(new RestorePathResult(path, "restored", null, null));
        }

        if (json)
        {
            WriteJson(output, new RestoreJsonResult("trash-restore", sync.Name, results));
            return exit;
        }

        foreach (var result in results)
        {
            if (result.Status == "restored")
            {
                output.WriteLine($"restored {result.Path}");
                continue;
            }

            if (result.Status == "forgotten")
            {
                output.WriteLine($"forgot {result.Path}");
                continue;
            }

            error.WriteLine($"pdsync: {result.Path}: {result.Reason}");
            if (result.CandidateUids is { Count: > 0 })
                error.WriteLine($"  candidates: {string.Join(", ", result.CandidateUids)}");
        }

        return exit;
    }

    private static SyncConfig ResolveSingleSync(
        PdsyncConfig config,
        IReadOnlyList<string> syncNames)
    {
        var distinct = syncNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count != 1)
            throw new ConfigException(
                "trash restore requires exactly one sync (positional or --sync)");

        var match = config.Syncs.FirstOrDefault(sync =>
            string.Equals(sync.Name, distinct[0], StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new ConfigException($"unknown sync '{distinct[0]}'");
        return match;
    }

    private static string NormalizeRestorePath(string raw)
    {
        var value = PathKey.Nfc(raw.Replace('\\', '/').Trim('/'));
        if (value.Length == 0)
            throw new ConfigException("trash restore requires a relative path");
        return value;
    }

    private static string LeafName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string ShortUid(string uid) =>
        uid.Length <= 8 ? uid : uid[..8];

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
