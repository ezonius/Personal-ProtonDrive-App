using System.Diagnostics;
using System.Text.Json;
using PdSync.Config;
using PdSync.Plan;
using PdSync.Proton;
using PdSync.Remote;
using PdSync.Scan;
using PdSync.State;

namespace PdSync.Exec;

/// <summary>Snapshots and plan for one sync, held together for execution.</summary>
public sealed class PreparedSync : IDisposable
{
    internal PreparedSync(
        PdsyncConfig config,
        SyncConfig sync,
        ProtonCli cli,
        StateStore state,
        IReadOnlyList<EntryRecord> baseline,
        LocalScan local,
        RemoteSnapshot remote,
        PlanResult plan,
        bool usedFastListing,
        int runsSinceFull)
    {
        Config = config;
        Sync = sync;
        Cli = cli;
        State = state;
        Baseline = baseline;
        Local = local;
        Remote = remote;
        Plan = plan;
        UsedFastListing = usedFastListing;
        RunsSinceFull = runsSinceFull;
    }

    public PdsyncConfig Config { get; }
    public SyncConfig Sync { get; }
    public ProtonCli Cli { get; }
    public StateStore State { get; }
    public IReadOnlyList<EntryRecord> Baseline { get; }
    public LocalScan Local { get; }
    public RemoteSnapshot Remote { get; }
    public PlanResult Plan { get; }
    public bool UsedFastListing { get; }
    public int RunsSinceFull { get; }

    public void Dispose() => State.Dispose();

    /// <summary>
    /// Replaces the (read-only, in-memory) state with a writable store while
    /// keeping the snapshots and plan captured under the run lock. Rebuild uses
    /// this after the old DB has been backed up and removed, so the writable
    /// store starts from the empty baseline the plan was computed against.
    /// </summary>
    internal PreparedSync WithState(StateStore state) =>
        new(Config, Sync, Cli, state, Baseline, Local, Remote, Plan,
            UsedFastListing, RunsSinceFull);
}

public sealed record SyncAttempt(
    PlanResult Plan,
    ExecutionReport? Report,
    bool UsedFastListing,
    int RunsSinceFull,
    string? Warning,
    long DurationMs = 0);

/// <summary>
/// Orchestrates snapshots, planner, safety guards, executor, and state commit
/// for one sync. <c>plan</c> uses <see cref="PrepareForPlan"/>; <c>run</c> uses
/// <see cref="Run"/> so both share exactly the same preparation path.
/// </summary>
public static class SyncService
{
    public static PreparedSync PrepareForPlan(
        PdsyncConfig config,
        SyncConfig sync,
        bool fastRequested,
        bool fullRequested,
        bool interactive = false)
    {
        return Prepare(config, sync, readOnly: true, fastRequested, fullRequested, interactive,
            cleanupStaged: false);
    }

    /// <summary>
    /// Plans a rebuild against an empty baseline without opening the existing
    /// state DB. The caller supplies a sync whose <c>initialSync</c> policy has
    /// already been overridden for this operation.
    /// </summary>
    public static PreparedSync PrepareForRebuildPreview(
        PdsyncConfig config,
        SyncConfig sync)
    {
        return Prepare(
            config, sync, readOnly: true, fastRequested: false, fullRequested: true,
            interactive: false, cleanupStaged: false,
            baselineOverride: Array.Empty<EntryRecord>(), emptyState: true);
    }

    /// <summary>
    /// Opens a writable state store for an under-lock rebuild whose snapshots
    /// and plan were already captured by <see cref="PrepareForRebuildPreview"/>.
    /// The caller must have backed up and removed the old DB first, so the new
    /// store starts from an empty baseline. No second remote listing occurs.
    /// </summary>
    public static PreparedSync PrepareForRebuildExecution(
        PdsyncConfig config,
        SyncConfig sync,
        PreparedSync prepared)
    {
        return prepared.WithState(StateStore.Open(config, sync));
    }

    public static SyncAttempt Run(
        PdsyncConfig config,
        SyncConfig sync,
        bool fastRequested,
        bool fullRequested,
        bool force,
        bool interactive,
        bool verbose,
        TextWriter output,
        bool cleanupStaged,
        TextWriter? diagnostics = null)
    {
        using var prepared = Prepare(config, sync, readOnly: false, fastRequested, fullRequested,
            interactive, cleanupStaged);
        return RunPrepared(prepared, fastRequested, force, verbose, output, cleanupStaged, diagnostics);
    }

    /// <summary>
    /// Applies a plan that was prepared elsewhere (rebuild prepares it under the
    /// run lock). The caller owns <paramref name="prepared"/> and must dispose it.
    /// </summary>
    public static SyncAttempt RunPrepared(
        PreparedSync prepared,
        bool fastRequested,
        bool force,
        bool verbose,
        TextWriter output,
        bool cleanupStaged,
        TextWriter? diagnostics = null)
    {
        var config = prepared.Config;
        var sync = prepared.Sync;
        var diagnosticsWriter = diagnostics ?? output;

        var pending = prepared.State.LoadPending();
        string? warning = null;
        if (pending.Count > 0)
        {
            // A previous process died between the journal write and the commit.
            // The snapshots are authoritative; recomputing from B/L/R is safe and
            // avoids blocking every future run on a stale row.
            prepared.State.ClearPending();
            warning = $"cleared {pending.Count} stale pending row(s) from an interrupted run";
            diagnosticsWriter.WriteLine("warning: " + warning);
        }

        SafetyGuard.CheckDeletionBudget(
            prepared.Plan, prepared.Baseline, sync.Options.Safety, force);

        if (!prepared.Plan.CanExecute)
            return new SyncAttempt(prepared.Plan, null, prepared.UsedFastListing, prepared.RunsSinceFull, warning);

        var runId = prepared.State.StartRun();
        var runStopwatch = Stopwatch.StartNew();
        using var log = RunLogger.Create(
            config, sync, runId, prepared.Plan.Stats, prepared.UsedFastListing,
            prepared.RunsSinceFull, diagnosticsWriter);
        Executor? executor = null;
        try
        {
            executor = new Executor(
                config,
                sync,
                prepared.Cli,
                prepared.State,
                prepared.Baseline,
                prepared.Local,
                prepared.Remote,
                prepared.Plan,
                runId,
                output,
                verbose,
                log);

            var report = executor.Execute();
            var status = report.Failed == 0
                ? (prepared.Plan.Conflicts.Count > 0 ? "conflict" : "ok")
                : "partial";
            prepared.State.FinishRun(runId, status, JsonSerializer.Serialize(new
            {
                planned = prepared.Plan.Stats,
                succeeded = report.Succeeded,
                failed = report.Failed,
                skipped = report.Skipped,
                prunedRemoteDirectories = report.PrunedRemoteDirectories,
                logFile = log.Path,
            }));
            runStopwatch.Stop();
            log.RunFinish(status, runStopwatch.ElapsedMilliseconds,
                report.Succeeded, report.Failed, report.Skipped,
                report.PrunedRemoteDirectories.Count);

            if (fastRequested)
                FastScanTracker.RecordRun(config, sync, fullScan: !prepared.UsedFastListing);
            return new SyncAttempt(prepared.Plan, report, prepared.UsedFastListing,
                prepared.RunsSinceFull, warning, runStopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            var partial = executor?.PartialOutcomes ?? Array.Empty<ActionOutcome>();
            var succeeded = partial.Count(o => o.Status == "ok");
            var failed = partial.Count(o => o.Status == "failed");
            var skipped = partial.Count(o => o.Status == "skipped");
            var pruned = executor?.PartialPrunedRemoteDirectories.Count ?? 0;
            try
            {
                prepared.State.FinishRun(runId, "failed", JsonSerializer.Serialize(new
                {
                    planned = prepared.Plan.Stats,
                    succeeded,
                    failed,
                    skipped,
                    prunedRemoteDirectories = executor?.PartialPrunedRemoteDirectories ?? Array.Empty<string>(),
                    logFile = log.Path,
                    error = ex.Message,
                }));
            }
            catch
            {
                // Preserve the original exception.
            }

            runStopwatch.Stop();
            log.RunFinish("failed", runStopwatch.ElapsedMilliseconds,
                succeeded, failed, skipped, pruned, ex.Message);
            throw;
        }
    }

    private static PreparedSync Prepare(
        PdsyncConfig config,
        SyncConfig sync,
        bool readOnly,
        bool fastRequested,
        bool fullRequested,
        bool interactive,
        bool cleanupStaged,
        IReadOnlyList<EntryRecord>? baselineOverride = null,
        bool emptyState = false)
    {
        var state = emptyState
            ? StateStore.OpenEmpty(config, sync)
            : readOnly
                ? StateStore.OpenReadOnly(config, sync)
                : StateStore.Open(config, sync);

        try
        {
            var baseline = baselineOverride ?? state.LoadEntries(includeFrozen: true);
            var cli = new ProtonCli(config.Proton.Cli);

            var ignore = IgnoreRules.Load(sync);
            var local = LocalScanner.Scan(sync, ignore, new ScanOptions());
            SafetyGuard.CheckLocalTree(local, baseline);

            var baseByKey = baseline.ToDictionary(e => e.PathKey, StringComparer.Ordinal);
            var useFast = FastScanTracker.ShouldUseFastListing(
                config, sync, fastRequested, fullRequested, out var runsSinceFull);

            var predicate = FastRemoteScan.CreateSkipPredicate(baseline, local, useFast);
            var remote = RemoteIndex.Build(cli, sync.RemoteRoot,
                useFast ? baseByKey : null, useFast ? predicate : null);

            var plan = Planner.Plan(
                baseline,
                local,
                remote,
                sync,
                new PlanContext(DateTimeOffset.UtcNow, interactive, cleanupStaged));

            return new PreparedSync(
                config, sync, cli, state, baseline, local, remote, plan,
                usedFastListing: useFast, runsSinceFull);
        }
        catch
        {
            state.Dispose();
            throw;
        }
    }
}
