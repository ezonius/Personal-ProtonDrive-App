using System.Text.Json;
using System.Text.Json.Serialization;
using PdSync.Config;
using PdSync.Exec;
using PdSync.Plan;
using PdSync.State;

namespace PdSync.Cli;

/// <summary>
/// <c>pdsync rebuild &lt;sync&gt; --policy=...</c> (SPEC §3.1, §5.3). Replaces
/// the baseline with a fresh one derived from the current local and remote
/// snapshots. It reuses the normal planner adoption and executor paths; the
/// only behavioral input is the <c>initialSync</c> override for this operation.
/// </summary>
public static class RebuildCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly (string Name, InitialSyncPolicy Value)[] PolicyValues =
    {
        ("adopt", InitialSyncPolicy.Adopt),
        ("keep-both", InitialSyncPolicy.KeepBoth),
        ("local-wins", InitialSyncPolicy.LocalWins),
        ("remote-wins", InitialSyncPolicy.RemoteWins),
    };

    public static int Execute(
        PdsyncConfig config,
        IReadOnlyList<string> syncNames,
        string? policy,
        bool fast,
        bool full,
        bool force,
        bool cleanupStaged,
        bool interactive,
        int? olderThanDays,
        bool yes,
        bool dryRun,
        bool verbose,
        bool json,
        TextReader input,
        bool? stdinIsTty,
        TextWriter output,
        TextWriter error)
    {
        if (fast || full || force || cleanupStaged || interactive || olderThanDays is not null)
        {
            error.WriteLine(
                "pdsync: rebuild accepts only --config, --sync, --policy, --yes, --dry-run, --json, --verbose, and --no-color");
            return 1;
        }

        var sync = ResolveSync(config, syncNames, error);
        if (sync is null)
            return 1;

        if (policy is null)
        {
            WritePolicyUsage(error);
            return 1;
        }

        if (!TryParsePolicy(policy, out var initialSync, out var policyName))
        {
            error.WriteLine($"pdsync: unknown rebuild policy '{policy}'");
            WritePolicyUsage(error);
            return 1;
        }

        var effectiveSync = sync with
        {
            Options = sync.Options with { InitialSync = initialSync },
        };

        // Dry-run is a pure preview: no lock, no backup, no state DB, and a full
        // read-only listing from an empty in-memory baseline.
        if (dryRun)
        {
            using var prepared = SyncService.PrepareForRebuildPreview(config, effectiveSync);
            var previewPlan = prepared.Plan;
            var previewWriter = json ? error : output;
            WritePreview(previewWriter, sync, policyName, previewPlan, dryRun: true);
            if (previewPlan.HasBlockingConflicts)
            {
                WriteBlockingConflicts(previewWriter, previewPlan);
                if (json)
                    WriteJson(output, ResultJson(sync, policyName, previewPlan, Counts.From(previewPlan),
                        dryRun: true, backup: null, succeeded: 0, failed: 0, skipped: 0, exitCode: 2));
                return 2;
            }

            if (json)
                WriteJson(output, ResultJson(sync, policyName, previewPlan, Counts.From(previewPlan),
                    dryRun: true, backup: null, succeeded: 0, failed: 0, skipped: 0, exitCode: 0));
            return 0;
        }

        // Preview before taking the lock so a refusal (or a blocking conflict)
        // writes nothing at all, including state/locks, on a virgin state dir.
        PlanResult preview;
        using (var prepared = SyncService.PrepareForRebuildPreview(config, effectiveSync))
            preview = prepared.Plan;

        var promptWriter = json ? error : output;
        WritePreview(promptWriter, sync, policyName, preview, dryRun: false);
        if (preview.HasBlockingConflicts)
        {
            WriteBlockingConflicts(promptWriter, preview);
            if (json)
                WriteJson(output, ResultJson(sync, policyName, preview, Counts.From(preview),
                    dryRun: false, backup: null, succeeded: 0, failed: 0, skipped: 0, exitCode: 2));
            return 2;
        }

        if (!yes)
        {
            if (!IsInteractive(input, stdinIsTty))
            {
                error.WriteLine("pdsync: rebuild requires --yes when not interactive");
                return 1;
            }

            promptWriter.Write($"Rebuild state for {sync.Name} with policy {policyName}? [y/N]: ");
            var answer = input.ReadLine();
            if (answer is null)
            {
                promptWriter.WriteLine();
                error.WriteLine("pdsync: rebuild cancelled");
                return 1;
            }

            answer = answer.Trim();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                error.WriteLine("pdsync: rebuild cancelled");
                return 1;
            }
        }

        // The user approved the preview. Only now take the lock; a held lock
        // exits 5 before any backup or state replacement.
        using var runLock = RunLock.Acquire(config, sync);

        // Re-plan under the lock before destroying state: a conflict that
        // appeared since the preview still exits 2 with no backup.
        using var verified = SyncService.PrepareForRebuildPreview(config, effectiveSync);
        if (verified.Plan.HasBlockingConflicts)
        {
            WriteBlockingConflicts(promptWriter, verified.Plan);
            if (json)
                WriteJson(output, ResultJson(sync, policyName, verified.Plan, Counts.From(verified.Plan),
                    dryRun: false, backup: null, succeeded: 0, failed: 0, skipped: 0, exitCode: 2));
            return 2;
        }

        var backup = BackupAndClearState(config, sync);
        if (backup is not null && !json)
            output.WriteLine($"  state backup: {backup}");

        // The old DB is gone, so this writable store starts empty. The plan and
        // snapshots were captured under the lock, so no third listing occurs.
        using var execution = SyncService.PrepareForRebuildExecution(config, effectiveSync, verified);
        var attempt = SyncService.RunPrepared(
            execution,
            fastRequested: false,
            force: false,
            verbose,
            json ? TextWriter.Null : output,
            cleanupStaged: false,
            error);

        var resultCounts = Counts.From(attempt.Plan);
        var succeeded = attempt.Report?.Succeeded ?? 0;
        var failed = attempt.Report?.Failed ?? 0;
        var skipped = attempt.Report?.Skipped ?? 0;
        var exitCode = attempt.Plan.HasBlockingConflicts ? 2 : failed > 0 ? 1 : 0;

        if (!json)
        {
            output.WriteLine(
                $"rebuild {sync.Name}: policy {policyName}; " +
                $"{resultCounts.Recorded} recorded, {resultCounts.Uploaded} uploaded, " +
                $"{resultCounts.Downloaded} downloaded, {resultCounts.ConflictRenamed} conflict-renamed; " +
                $"succeeded {succeeded}, failed {failed}, skipped {skipped}");
            if (attempt.Report is null)
            {
                foreach (var conflict in attempt.Plan.Conflicts.Where(c => c.Resolution == PlanResolution.Abort))
                    output.WriteLine($"  aborted [{conflict.Kind}] {conflict.Path}: {conflict.Message}");
            }
        }
        else
        {
            WriteJson(output, ResultJson(sync, policyName, attempt.Plan, resultCounts, dryRun: false,
                backup, succeeded, failed, skipped, exitCode));
        }

        return exitCode;
    }

    private static SyncConfig? ResolveSync(
        PdsyncConfig config,
        IReadOnlyList<string> syncNames,
        TextWriter error)
    {
        var distinct = syncNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count != 1)
        {
            error.WriteLine("pdsync: rebuild requires exactly one sync (positional or --sync)");
            return null;
        }

        var match = config.Syncs.FirstOrDefault(sync =>
            string.Equals(sync.Name, distinct[0], StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new ConfigException($"unknown sync '{distinct[0]}'");
        return match;
    }

    private static bool TryParsePolicy(
        string value,
        out InitialSyncPolicy policy,
        out string name)
    {
        foreach (var (candidate, mapped) in PolicyValues)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
            {
                policy = mapped;
                name = candidate;
                return true;
            }
        }

        policy = default;
        name = value;
        return false;
    }

    private static void WritePolicyUsage(TextWriter error) =>
        error.WriteLine(
            "pdsync: rebuild requires --policy=adopt|keep-both|local-wins|remote-wins");

    /// <summary>
    /// Copies the existing raw DB file (even if it cannot be opened) into
    /// <c>&lt;stateDir&gt;/state/backups</c>, then removes it so the run starts
    /// from an empty baseline. Missing state produces no backup and no backups
    /// directory.
    /// </summary>
    private static string? BackupAndClearState(PdsyncConfig config, SyncConfig sync)
    {
        var stateDirectory = Path.Combine(config.StateDir, "state");
        var syncId = StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot);
        var databasePath = Path.Combine(stateDirectory, syncId + ".sqlite");
        if (!File.Exists(databasePath))
            return null;

        var backupsDirectory = Path.Combine(stateDirectory, "backups");
        Directory.CreateDirectory(backupsDirectory);
        var backupPath = Path.Combine(
            backupsDirectory,
            $"{syncId}.pre-rebuild-{DateTime.UtcNow:yyyyMMdd-HHmmss}.sqlite");
        File.Copy(databasePath, backupPath, overwrite: false);

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = databasePath + suffix;
            if (File.Exists(sidecar))
                File.Delete(sidecar);
        }

        File.Delete(databasePath);
        return backupPath;
    }

    private static bool IsInteractive(TextReader input, bool? stdinIsTty) =>
        stdinIsTty ?? (ReferenceEquals(input, Console.In) && !Console.IsInputRedirected);

    private sealed record Counts(int Recorded, int Uploaded, int Downloaded, int ConflictRenamed)
    {
        public static Counts From(PlanResult plan) => new(
            plan.Adoptions.Count,
            plan.Actions.Count(action => action.IsUpload),
            plan.Actions.Count(action => action.IsDownload),
            plan.Actions.Count(action => action.Kind == PlanActionKind.LocalConflictRename));
    }

    private static void WritePreview(
        TextWriter output,
        SyncConfig sync,
        string policy,
        PlanResult plan,
        bool dryRun)
    {
        var counts = Counts.From(plan);
        output.WriteLine(
            $"rebuild {sync.Name}{(dryRun ? " (dry-run)" : string.Empty)}: policy {policy}; " +
            $"{counts.Recorded} recorded, {counts.Uploaded} uploaded, " +
            $"{counts.Downloaded} downloaded, {counts.ConflictRenamed} conflict-renamed; " +
            $"{plan.Stats.Actions} action(s), {plan.Stats.Conflicts} conflict(s), {plan.Stats.Skipped} skipped");
    }

    private static void WriteBlockingConflicts(TextWriter output, PlanResult plan)
    {
        foreach (var conflict in plan.Conflicts.Where(c => c.Resolution == PlanResolution.Abort))
            output.WriteLine($"  aborted [{conflict.Kind}] {conflict.Path}: {conflict.Message}");
    }

    private static object ResultJson(
        SyncConfig sync,
        string policy,
        PlanResult plan,
        Counts counts,
        bool dryRun,
        string? backup,
        int succeeded,
        int failed,
        int skipped,
        int exitCode) => new
        {
            command = "rebuild",
            sync = sync.Name,
            policy,
            dryRun,
            recorded = counts.Recorded,
            uploaded = counts.Uploaded,
            downloaded = counts.Downloaded,
            conflictRenamed = counts.ConflictRenamed,
            actions = plan.Stats.Actions,
            conflicts = plan.Stats.Conflicts,
            conflictList = plan.Conflicts.Select(conflict => new
            {
                path = conflict.Path,
                kind = conflict.Kind.ToString(),
                resolution = conflict.Resolution.ToString(),
                message = conflict.Message,
            }),
            skippedActions = plan.Stats.Skipped,
            backup,
            succeeded,
            failed,
            skipped,
            exitCode,
        };

    private static void WriteJson(TextWriter output, object value) =>
        output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
