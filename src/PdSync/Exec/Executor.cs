using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PdSync.Config;
using PdSync.Plan;
using PdSync.Proton;
using PdSync.Remote;
using PdSync.Scan;
using PdSync.State;

namespace PdSync.Exec;

public sealed record ActionOutcome(
    PlanAction Action,
    bool Success,
    string? Error = null,
    long DurationMs = 0,
    VerificationSummary? Verification = null,
    string? Outcome = null)
{
    public string Status => Outcome ?? (Success
        ? "ok"
        : Error?.StartsWith("skipped", StringComparison.Ordinal) == true ? "skipped" : "failed");
}

public sealed class ExecutionReport
{
    public ExecutionReport(
        IReadOnlyList<ActionOutcome> outcomes,
        IReadOnlyList<PlanAdoption> adoptions,
        IReadOnlyList<string> prunedRemoteDirectories)
    {
        Outcomes = outcomes;
        Adoptions = adoptions;
        PrunedRemoteDirectories = prunedRemoteDirectories;
    }

    public IReadOnlyList<ActionOutcome> Outcomes { get; }
    public IReadOnlyList<PlanAdoption> Adoptions { get; }
    public IReadOnlyList<string> PrunedRemoteDirectories { get; }
    public int Failed => Outcomes.Count(o => o.Status == "failed");
    public int Succeeded => Outcomes.Count(o => o.Status == "ok");
    public int Skipped => Outcomes.Count(o => o.Status == "skipped");
}

/// <summary>
/// Executes one <see cref="PlanResult"/> and commits successful state changes.
/// Every destructive or transfer action is journaled in <c>pending</c> before
/// the CLI call and removed in the same transaction as its state update.
/// </summary>
public sealed class Executor
{
    private readonly PdsyncConfig _config;
    private readonly SyncConfig _sync;
    private readonly ProtonCli _cli;
    private readonly StateStore _state;
    private IReadOnlyList<EntryRecord> _baseline;
    private IReadOnlyDictionary<string, EntryRecord> _baseByKey;
    private readonly LocalScan _local;
    private readonly RemoteSnapshot _remote;
    private readonly PlanResult _plan;
    private readonly long _runId;
    private readonly TextWriter _output;
    private readonly bool _verbose;
    private readonly RunLogger? _log;
    private readonly LocalTrashStore _trash;
    private readonly Verifier _verifier;
    private readonly string _stagingRoot;
    private readonly List<string> _failedPrefixes = new();
    private readonly HashSet<string> _failedKeys = new(StringComparer.Ordinal);
    private readonly List<string> _pruned = new();
    private readonly List<ActionOutcome> _outcomes = new();

    public Executor(
        PdsyncConfig config,
        SyncConfig sync,
        ProtonCli cli,
        StateStore state,
        IReadOnlyList<EntryRecord> baseline,
        LocalScan local,
        RemoteSnapshot remote,
        PlanResult plan,
        long runId,
        TextWriter output,
        bool verbose,
        RunLogger? log = null)
    {
        _config = config;
        _sync = sync;
        _cli = cli;
        _state = state;
        _baseline = baseline;
        _baseByKey = baseline.ToDictionary(e => e.PathKey, StringComparer.Ordinal);
        _local = local;
        _remote = remote;
        _plan = plan;
        _runId = runId;
        _output = output;
        _verbose = verbose;
        _log = log;
        _trash = new LocalTrashStore(config, sync, runId);
        _verifier = new Verifier(cli, sync.Options.Verify);
        _stagingRoot = Path.Combine(config.StateDir, "tmp", runId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Outcomes recorded so far, even when <see cref="Execute"/> throws.</summary>
    public IReadOnlyList<ActionOutcome> PartialOutcomes => _outcomes;

    /// <summary>Remote directories pruned so far, even when <see cref="Execute"/> throws.</summary>
    public IReadOnlyList<string> PartialPrunedRemoteDirectories => _pruned;

    public ExecutionReport Execute(CancellationToken cancellationToken = default)
    {
        var outcomes = _outcomes;

        try
        {
            foreach (var action in _plan.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (action.Kind == PlanActionKind.DropBase &&
                    (_failedPrefixes.Any(prefix => action.PathKey == prefix.TrimEnd('/') ||
                                                   action.PathKey.StartsWith(prefix, StringComparison.Ordinal)) ||
                     _failedKeys.Contains(action.PathKey)))
                {
                    Record(outcomes, new ActionOutcome(action, false,
                        "skipped: an earlier action failed", Outcome: "skipped"));
                    continue;
                }

                if (action.Kind != PlanActionKind.DropBase && _failedKeys.Contains(action.PathKey))
                {
                    Record(outcomes, new ActionOutcome(action, false,
                        "skipped: an earlier action for this path failed", Outcome: "skipped"));
                    continue;
                }

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    var verification = ExecuteOne(action);
                    stopwatch.Stop();
                    Record(outcomes, new ActionOutcome(action, true,
                        DurationMs: stopwatch.ElapsedMilliseconds, Verification: verification));
                    if (_verbose)
                        _output.WriteLine($"ok     {Describe(action)}");
                }
                catch (ProtonAuthException ex)
                {
                    stopwatch.Stop();
                    Record(outcomes, new ActionOutcome(action, false, ex.Message,
                        stopwatch.ElapsedMilliseconds));
                    _failedKeys.Add(action.PathKey);
                    if (action.IsDelete)
                        _failedPrefixes.Add(action.PathKey + "/");
                    _output.WriteLine($"FAILED {Describe(action)}: {ex.Message}");
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    stopwatch.Stop();
                    Record(outcomes, new ActionOutcome(action, false,
                        "cancelled: " + ex.Message, stopwatch.ElapsedMilliseconds));
                    throw;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    Record(outcomes, new ActionOutcome(action, false, ex.Message,
                        stopwatch.ElapsedMilliseconds,
                        (ex as VerificationException)?.Verification));
                    _failedKeys.Add(action.PathKey);
                    if (action.IsDelete)
                        _failedPrefixes.Add(action.PathKey + "/");
                    // A failed cross-parent step 1 must suppress step 2: its
                    // intermediate source path does not exist yet. A failed
                    // adoption must suppress the rename that consumes the
                    // baseline path it did not rewrite.
                    if ((action.Kind == PlanActionKind.RemoteDirectoryMove ||
                         action.Kind == PlanActionKind.AdoptRemoteDirectoryMove) &&
                        action.SecondaryPath is not null)
                        _failedKeys.Add(PathKey.Fold(action.SecondaryPath));
                    _output.WriteLine($"FAILED {Describe(action)}: {ex.Message}");
                }
            }

            foreach (var adoption in _plan.Adoptions)
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    Adopt(adoption);
                    stopwatch.Stop();
                    outcomes.Add(new ActionOutcome(
                        new PlanAction(PlanActionKind.DropBase, adoption.Path, adoption.PathKey,
                            Reason: "adoption"),
                        true, DurationMs: stopwatch.ElapsedMilliseconds));
                    _log?.Action("Adopt", adoption.Path, adoption.PathKey, null,
                        "ok", stopwatch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    outcomes.Add(new ActionOutcome(
                        new PlanAction(PlanActionKind.DropBase, adoption.Path, adoption.PathKey,
                            Reason: "adoption"),
                        false, ex.Message));
                    _log?.Action("Adopt", adoption.Path, adoption.PathKey, null,
                        "failed", stopwatch.ElapsedMilliseconds, ex.Message);
                    _output.WriteLine($"FAILED adopt {adoption.Path}: {ex.Message}");
                }
            }

            FreezeIgnoredBaselines();

            if (_sync.Options.PruneEmptyRemoteDirs && _plan.Actions.Any(a => a.IsDelete))
                PruneEmptyRemoteDirectories(outcomes, cancellationToken);

            _trash.SaveManifest();
        }
        finally
        {
            TryDeleteDirectory(_stagingRoot);
        }

        return new ExecutionReport(outcomes, _plan.Adoptions, _pruned);
    }

    private VerificationSummary? ExecuteOne(PlanAction action)
    {
        var pending = _state.AddPending(action.Kind.ToString(), action.Path,
            JsonSerializer.Serialize(action));
        try
        {
            // A remote trash is a remote side effect: journal the undo record in
            // its own committed statement before the CLI call, exactly like the
            // pending row. A crash after `fs trash` but before the entry delete
            // then still leaves a restorable record.
            if (action.Kind == PlanActionKind.RemoteTrash)
                RecordRemoteTrashBeforeCall(action);

            VerificationSummary? verification = null;
            _state.InTransaction(() =>
            {
                verification = ExecuteAction(action);
                ApplyState(action);
                _state.RemovePending(pending);
            });
            return verification;
        }
        catch (OperationCanceledException)
        {
            try { _state.RemovePending(pending); } catch { /* best effort */ }
            throw;
        }
        catch
        {
            try { _state.RemovePending(pending); } catch { /* best effort */ }
            throw;
        }
    }

    private VerificationSummary? ExecuteAction(PlanAction action)
    {
        switch (action.Kind)
        {
            case PlanActionKind.CreateRemoteDirectory:
                CreateRemoteDirectory(action);
                return null;
            case PlanActionKind.CreateLocalDirectory:
                CreateLocalDirectory(action);
                return null;
            case PlanActionKind.DownloadCreate:
            case PlanActionKind.DownloadReplace:
            case PlanActionKind.DownloadRestore:
                return Download(action);
            case PlanActionKind.LocalConflictRename:
                RenameLocal(action.Path, action.SecondaryPath
                    ?? throw new SyncException("conflict rename has no target"));
                return null;
            case PlanActionKind.UploadCreate:
            case PlanActionKind.UploadRevision:
            case PlanActionKind.KeepBothUpload:
                return Upload(action);
            case PlanActionKind.RemoteMove:
                MoveRemote(action);
                return null;
            case PlanActionKind.RemoteDirectoryMove:
                MoveRemoteDirectory(action);
                return null;
            case PlanActionKind.AdoptRemoteDirectoryMove:
                AdoptRemoteDirectoryMove(action);
                return null;
            case PlanActionKind.LocalMove:
                MoveLocal(action);
                return null;
            case PlanActionKind.LocalDirectoryMove:
                MoveLocalDirectory(action);
                return null;
            case PlanActionKind.LocalTrash:
                TrashLocal(action);
                return null;
            case PlanActionKind.RemoteTrash:
                TrashRemote(action);
                return null;
            case PlanActionKind.CleanupStagedUpload:
                CleanupStagedUpload(action);
                return null;
            case PlanActionKind.DropBase:
                return null;
            default:
                throw new SyncException($"unsupported action: {action.Kind}");
        }
    }

    private void CreateRemoteDirectory(PlanAction action)
    {
        var (parent, name) = SplitRelative(action.Path);
        var node = _cli.CreateFolder(RemoteFull(parent), name);
        var local = _local.ByKey.GetValueOrDefault(action.PathKey);
        StateSet(action.Path, EntryKind.Folder, local, node: node);
    }

    private void CreateLocalDirectory(PlanAction action)
    {
        var full = LocalFull(action.Path);
        Directory.CreateDirectory(full);
        var remote = _remote.Get(action.PathKey);
        StateSet(action.Path, EntryKind.Folder, StatLocal(action.Path), remote: remote);
    }

    private VerificationSummary Upload(PlanAction action)
    {
        var localFull = LocalFull(action.Path);
        if (!File.Exists(localFull))
            throw new SyncException($"local file to upload is missing: {localFull}");

        var (parent, localName) = SplitRelative(action.Path);
        var existing = _remote.Get(action.PathKey);
        var remoteName = existing is null ? localName : Leaf(existing.Path);
        var uploadPath = localFull;
        var staging = false;
        var needsSafeName = LocalPathGlob.RequiresStaging(localFull) ||
                            LocalPathGlob.RequiresStaging(remoteName);

        if (!string.Equals(remoteName, localName, StringComparison.Ordinal) || needsSafeName)
        {
            var directory = Path.Combine(_stagingRoot, "upload", action.PathKey);
            Directory.CreateDirectory(directory);
            var stagedName = needsSafeName
                ? StagedUpload.NewName()
                : remoteName;
            uploadPath = Path.Combine(directory, stagedName);
            File.Copy(localFull, uploadPath, overwrite: true);
            staging = true;
        }

        var uploadedLeaf = Path.GetFileName(uploadPath);
        var renamed = false;
        var uploadResultReceived = false;
        try
        {
            var result = _cli.Upload(uploadPath, RemoteFull(parent));
            if (result.TransferredItems == 0 && result.SkippedItems == 0)
                throw new SyncException($"upload reported no transfer for {action.Path}");
            uploadResultReceived = true;

            if (staging &&
                !string.Equals(uploadedLeaf, remoteName, StringComparison.Ordinal))
            {
                _cli.Rename(RemoteFull(parent) + "/" + uploadedLeaf, remoteName);
                renamed = true;
            }

            var (node, verification) = _verifier.VerifyUpload(RemoteFull(parent), remoteName, uploadPath);
            StateSet(action.Path, EntryKind.File, StatLocal(action.Path), node: node);
            return verification;
        }
        catch (ProtonAuthException)
        {
            // The CLI session is gone, so same-run cleanup is deliberately
            // skipped: a second CLI call cannot succeed and could stall or raise
            // a second auth error. The next run's planner trashes the leftover.
            throw;
        }
        catch
        {
            // Only a post-transfer failure is eligible for same-run cleanup.
            // An upload call that threw before returning a result did not prove
            // that any remote node exists, so it must not trigger a trash.
            if (uploadResultReceived)
            {
                // Best-effort trash the just-created remote node, but never
                // replace the original exception. A pre-existing remote file is
                // not trashed when the upload became a new revision on it.
                var currentLeaf = renamed ? remoteName : uploadedLeaf;
                if (StagedUpload.IsStagingLeaf(currentLeaf) || existing is null)
                    TryTrashUploadedNode(RemoteFull(parent), currentLeaf);
            }
            throw;
        }
        finally
        {
            if (staging)
                TryDeleteDirectory(Path.GetDirectoryName(uploadPath)!);
        }
    }

    private VerificationSummary Download(PlanAction action)
    {
        var remote = _remote.GetByUid(action.RemoteUid) ?? _remote.Get(action.PathKey)
            ?? throw new SyncException($"remote entry is missing for {action.Path}");

        var parentRel = Parent(action.Path);
        var name = Leaf(action.Path);
        var staging = Path.Combine(_stagingRoot, "download", action.PathKey);
        Directory.CreateDirectory(staging);

        _cli.Download(RemoteFull(remote.Path), staging);
        var downloaded = Path.Combine(staging, Leaf(remote.Path));
        if (!File.Exists(downloaded))
        {
            var files = Directory.GetFiles(staging);
            downloaded = files.Length == 1
                ? files[0]
                : throw new SyncException($"download did not produce exactly one file for {action.Path}");
        }

        var verification = _verifier.VerifyDownload(remote, downloaded);

        var destination = LocalFull(action.Path);
        var destinationDirectory = Path.GetDirectoryName(destination);
        if (destinationDirectory is not null)
            Directory.CreateDirectory(destinationDirectory);

        if (File.Exists(destination) || Directory.Exists(destination))
            _trash.Move(destination, action.Path, $"replaced during download ({action.Kind})");

        FileSystemUtil.MoveFile(downloaded, destination, overwrite: true);
        StateSet(action.Path, EntryKind.File, StatLocal(action.Path), remote: remote);
        return verification;
    }

    private void MoveRemote(PlanAction action)
    {
        var target = action.SecondaryPath
            ?? throw new SyncException("remote move has no target");
        var sourceFull = RemoteFull(action.Path);
        var (targetParent, targetName) = SplitRelative(target);
        var (sourceParent, sourceName) = SplitRelative(action.Path);

        if (string.Equals(sourceParent, targetParent, StringComparison.Ordinal))
        {
            if (!string.Equals(sourceName, targetName, StringComparison.Ordinal))
                _cli.Rename(sourceFull, targetName);
        }
        else
        {
            _cli.Move(sourceFull, RemoteFull(targetParent));
            if (!string.Equals(sourceName, targetName, StringComparison.Ordinal))
                _cli.Rename(RemoteFull(target), targetName);
        }

        MoveState(action, target);
    }

    private void MoveLocal(PlanAction action)
    {
        var target = action.SecondaryPath
            ?? throw new SyncException("local move has no target");
        var sourceFull = LocalFull(action.Path);
        var targetFull = LocalFull(target);
        Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);

        if (Directory.Exists(sourceFull))
            Directory.Move(sourceFull, targetFull);
        else if (File.Exists(sourceFull))
            File.Move(sourceFull, targetFull, overwrite: false);
        else
            throw new SyncException($"local source for move is missing: {sourceFull}");

        MoveState(action, target);
    }

    /// <summary>
    /// Applies exactly one CLI call for a directory move. A cross-parent rename
    /// is planned as two separate actions (move, then same-parent rename), each
    /// with its own pending journal row and state commit, so a failure between
    /// the calls is re-detected and completed by the next run.
    /// </summary>
    private void MoveRemoteDirectory(PlanAction action)
    {
        var target = action.SecondaryPath
            ?? throw new SyncException("remote directory move has no target");

        // Use remote-snapshot casing for the source and resolved parent. Local
        // and baseline casing can legitimately differ (folded adoption), and the
        // remote is case-sensitive.
        var sourceEntry = _remote.GetByUid(action.RemoteUid) ?? _remote.Get(action.PathKey);
        var sourcePath = sourceEntry?.Path ?? action.Path;
        var sourceFull = RemoteFull(sourcePath);
        var (sourceParent, sourceName) = SplitRelative(sourcePath);
        var (targetParent, targetName) = SplitRelative(target);
        var crossParent = !string.Equals(PathKey.Fold(sourceParent),
            PathKey.Fold(targetParent), StringComparison.Ordinal);

        if (!crossParent)
        {
            // Same-parent rename; nothing to move.
            if (!string.Equals(sourceName, targetName, StringComparison.Ordinal))
                _cli.Rename(sourceFull, targetName);
        }
        else
        {
            // The move-only step keeps the old leaf by contract.
            if (!string.Equals(PathKey.Fold(sourceName), PathKey.Fold(targetName),
                    StringComparison.Ordinal))
                throw new SyncException(
                    $"cross-parent directory move must keep the old leaf: {sourcePath} -> {target}");

            var targetParentFull = string.Empty;
            if (targetParent.Length > 0)
            {
                // The planner only emits a directory move when the destination
                // parent is present in the remote snapshot; re-check so a stale
                // plan cannot move a folder into a missing parent.
                var parent = _remote.Get(PathKey.Fold(targetParent));
                if (parent is null || !parent.IsDirectory)
                    throw new SyncException(
                        $"remote parent for directory move is missing: {targetParent}");
                targetParentFull = parent.Path;
            }

            _cli.Move(sourceFull, RemoteFull(targetParentFull));
        }

        RewriteMovedSubtree(action, target);
    }

    /// <summary>
    /// Rewrites the baseline subtree to the remote path the remote side already
    /// moved to, without any local or remote filesystem action. A later step in
    /// the same plan (or the next run) uses the adopted baseline to complete the
    /// rename instead of downloading the moved subtree.
    /// </summary>
    private void AdoptRemoteDirectoryMove(PlanAction action)
    {
        var target = action.SecondaryPath
            ?? throw new SyncException("directory adoption has no target");
        RewriteMovedSubtree(action, target);
    }

    private void MoveLocalDirectory(PlanAction action)
    {
        var target = action.SecondaryPath
            ?? throw new SyncException("local directory move has no target");
        var sourceFull = LocalFull(action.Path);
        var targetFull = LocalFull(target);
        if (!Directory.Exists(sourceFull))
            throw new SyncException($"local source for directory move is missing: {sourceFull}");

        Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
        FileSystemUtil.MoveDirectory(sourceFull, targetFull);
        RewriteMovedSubtree(action, target);
    }

    /// <summary>
    /// Rewrites every baseline row at or under the moved directory in one
    /// transaction (the caller already wraps this in one with the pending row).
    /// Remote uids, revisions, digests, sizes and frozen flags are preserved;
    /// <c>path</c>, <c>path_key</c>, and <c>last_synced_at</c> change. The
    /// in-memory baseline is updated too so a follow-up step (a cross-parent
    /// rename) rewrites the already-moved subtree rather than the original one.
    /// </summary>
    private void RewriteMovedSubtree(PlanAction action, string target)
    {
        var sourceKey = action.PathKey;
        var prefix = sourceKey + "/";
        var rows = _baseline
            .Where(e => e.PathKey == sourceKey ||
                        e.PathKey.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(e => e.PathKey, StringComparer.Ordinal)
            .ToList();

        if (rows.Count == 0)
            throw new SyncException($"baseline subtree is missing for directory move {action.Path}");

        // Use the baseline's own root path for suffix removal. The action path may
        // carry remote casing, and slicing the folded key would lowercase the
        // descendant suffix.
        var root = rows.FirstOrDefault(row => row.PathKey == sourceKey)
            ?? throw new SyncException($"baseline root row is missing for directory move {action.Path}");
        var rewrites = new List<EntryRecord>(rows.Count);
        foreach (var row in rows)
        {
            var suffix = row.PathKey == sourceKey
                ? string.Empty
                : row.Path.StartsWith(root.Path, StringComparison.Ordinal)
                    ? row.Path[root.Path.Length..]
                    : throw new SyncException(
                        $"baseline row '{row.Path}' is not under move root '{root.Path}'");
            var newPath = target + suffix;
            rewrites.Add(row with
            {
                Path = newPath,
                PathKey = PathKey.Fold(newPath),
                LastSyncedAt = DateTimeOffset.UtcNow,
            });
        }

        foreach (var row in rows)
            _state.DeleteEntry(row.PathKey);
        foreach (var row in rewrites)
            _state.UpsertEntry(row);

        var rewrittenKeys = new HashSet<string>(rows.Select(r => r.PathKey), StringComparer.Ordinal);
        var updated = _baseline
            .Where(e => !rewrittenKeys.Contains(e.PathKey))
            .Concat(rewrites)
            .ToList();
        _baseline = updated;
        _baseByKey = updated.ToDictionary(e => e.PathKey, StringComparer.Ordinal);
    }

    private void MoveState(PlanAction action, string target)
    {
        var old = _baseByKey.GetValueOrDefault(action.PathKey)
            ?? throw new SyncException($"baseline entry is missing for move {action.Path}");

        var local = StatLocal(target)
            ?? throw new SyncException($"moved local path is missing: {target}");
        var remote = _remote.GetByUid(action.RemoteUid) ?? _remote.Get(action.PathKey);

        EntryRecord moved;
        if (remote is not null)
        {
            moved = EntryFromLocalRemote(target, local, remote);
        }
        else
        {
            moved = old with
            {
                Path = target,
                PathKey = PathKey.Fold(target),
                Size = local.Size,
                MtimeMs = local.Mtime.ToUnixTimeMilliseconds(),
                Sha1 = local.Sha1,
                LocalPresent = true,
                RemotePresent = old.RemotePresent,
                LastSyncedAt = DateTimeOffset.UtcNow,
            };
        }

        _state.DeleteEntry(action.PathKey);
        _state.UpsertEntry(moved);
    }

    private void TrashLocal(PlanAction action)
    {
        var full = LocalFull(action.Path);
        if (!File.Exists(full) && !Directory.Exists(full))
            return;

        _trash.Move(full, action.Path, "remote deletion");
        _state.DeleteEntry(action.PathKey);
    }

    private void RecordRemoteTrashBeforeCall(PlanAction action)
    {
        // Resolve the UID before the CLI call so a node can never be trashed
        // without an undo record. The row is kept even if the trash call fails;
        // `trash restore` then refuses with exit 2 and never guesses.
        var uid = action.RemoteUid
            ?? _remote.Get(action.PathKey)?.Uid
            ?? _baseByKey.GetValueOrDefault(action.PathKey)?.RemoteUid;
        if (string.IsNullOrEmpty(uid))
            throw new SyncException($"cannot record remote trash for {action.Path}: remote uid is unknown");

        _state.RecordRemoteTrash(action.Path, action.PathKey, uid, _runId);
    }

    private void TrashRemote(PlanAction action)
    {
        // The undo record was already committed by ExecuteOne before this call.
        _cli.Trash(RemoteFull(action.Path));
        _state.DeleteEntry(action.PathKey);
    }

    private void CleanupStagedUpload(PlanAction action)
    {
        _cli.Trash(RemoteFull(action.Path));
        // The automatic path normally has no baseline row; the opt-in path
        // requires one. Deleting is harmless in both cases and keeps the
        // state correct after an interrupted earlier run.
        _state.DeleteEntry(action.PathKey);
    }

    private void TryTrashUploadedNode(string parentRemote, string leaf)
    {
        var remotePath = parentRemote.TrimEnd('/') + "/" + leaf;
        try
        {
            _cli.Trash(remotePath);
            if (_verbose)
                _output.WriteLine($"cleanup trashed staged upload {remotePath}");
        }
        catch
        {
            // Cleanup is best effort. The original upload/rename/verification
            // failure is rethrown by the caller; the next run's planner trashes
            // any leftover matching staging node.
        }
    }

    private void Adopt(PlanAdoption adoption)
    {
        var local = _local.ByKey.GetValueOrDefault(adoption.PathKey);
        var remote = _remote.Get(adoption.PathKey);
        if (local is null || remote is null)
            throw new SyncException("adoption source disappeared");

        _state.UpsertEntry(EntryFromLocalRemote(adoption.Path, local, remote));
        // The path is active again; a stale undo record must not outlive it.
        _state.DeleteRemoteTrash(adoption.PathKey);
    }

    private void ApplyState(PlanAction action)
    {
        switch (action.Kind)
        {
            case PlanActionKind.DropBase:
                _state.DeleteEntry(action.PathKey);
                break;
            // The path is active again; a stale undo record must not outlive it.
            case PlanActionKind.UploadCreate:
            case PlanActionKind.KeepBothUpload:
            case PlanActionKind.DownloadCreate:
                _state.DeleteRemoteTrash(action.PathKey);
                break;
            // Other actions already wrote their state in their execute method.
            default:
                break;
        }
    }

    private void FreezeIgnoredBaselines()
    {
        var ignored = _plan.Skipped
            .Where(s => s.Reason == PlanSkipReason.Ignored)
            .Select(s => s.PathKey);
        _state.InTransaction(() =>
        {
            foreach (var key in ignored)
                _state.MarkFrozen(key);
        });
    }

    private void PruneEmptyRemoteDirectories(List<ActionOutcome> outcomes, CancellationToken cancellationToken)
    {
        RemoteSnapshot current;
        try
        {
            current = RemoteIndex.Build(_cli, _sync.RemoteRoot);
        }
        catch (Exception ex) when (ex is not ProtonAuthException)
        {
            Record(outcomes, new ActionOutcome(
                new PlanAction(PlanActionKind.RemoteTrash, string.Empty, string.Empty,
                    Reason: "prune empty remote directories"),
                false, ex.Message));
            return;
        }

        var folders = current.Entries
            .Where(e => e.IsDirectory)
            .OrderByDescending(e => e.Path.Count(c => c == '/'))
            .ThenByDescending(e => e.Path.Length)
            .ToList();

        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = folder.PathKey;
            var prefix = key + "/";
            var hasChild = current.Entries.Any(e =>
                e.PathKey.StartsWith(prefix, StringComparison.Ordinal) &&
                !removed.Contains(e.PathKey));
            if (hasChild)
                continue;

            if (Directory.Exists(LocalFull(folder.Path)))
                continue;

            var pending = _state.AddPending("PruneRemoteDirectory", folder.Path);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                _state.InTransaction(() =>
                {
                    _cli.Trash(RemoteFull(folder.Path));
                    _state.DeleteEntry(key);
                    _state.RemovePending(pending);
                });
                removed.Add(key);
                _pruned.Add(folder.Path);
                stopwatch.Stop();
                Record(outcomes, new ActionOutcome(
                    new PlanAction(PlanActionKind.RemoteTrash, folder.Path, key,
                        Reason: "prune empty remote directory"), true,
                    DurationMs: stopwatch.ElapsedMilliseconds));
                if (_verbose)
                    _output.WriteLine($"ok     prune empty remote dir {folder.Path}");
            }
            catch (ProtonAuthException)
            {
                throw;
            }
            catch (Exception ex)
            {
                try { _state.RemovePending(pending); } catch { /* best effort */ }
                stopwatch.Stop();
                Record(outcomes, new ActionOutcome(
                    new PlanAction(PlanActionKind.RemoteTrash, folder.Path, key,
                        Reason: "prune empty remote directory"), false, ex.Message,
                    DurationMs: stopwatch.ElapsedMilliseconds));
                _output.WriteLine($"FAILED prune {folder.Path}: {ex.Message}");
            }
        }
    }

    private void RenameLocal(string from, string to)
    {
        var source = LocalFull(from);
        var destination = LocalFull(to);
        var directory = Path.GetDirectoryName(destination);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        if (Directory.Exists(source))
            Directory.Move(source, destination);
        else if (File.Exists(source))
            File.Move(source, destination, overwrite: false);
        else
            throw new SyncException($"local file for conflict rename is missing: {source}");
    }

    private void StateSet(
        string path,
        EntryKind kind,
        LocalEntry? local,
        RemoteEntry? remote = null,
        ProtonNode? node = null)
    {
        var entry = node is not null
            ? EntryFromNode(path, kind, local, node)
            : remote is not null && local is not null
                ? EntryFromLocalRemote(path, local, remote)
                : remote is not null
                    ? EntryFromRemote(path, kind, remote)
                    : throw new SyncException($"no remote metadata for state entry {path}");
        _state.UpsertEntry(entry);
    }

    private EntryRecord EntryFromLocalRemote(string path, LocalEntry local, RemoteEntry remote)
    {
        return new EntryRecord(
            PathKey.Fold(path),
            path,
            remote.IsDirectory ? EntryKind.Folder : EntryKind.File,
            local.IsDirectory ? 0 : local.Size,
            local.Mtime.ToUnixTimeMilliseconds(),
            local.IsDirectory ? null : local.Sha1,
            remote.Uid,
            remote.RevisionUid,
            remote.Size,
            remote.ClaimedMtime?.ToUnixTimeMilliseconds() ?? remote.ModifiedAt.ToUnixTimeMilliseconds(),
            remote.Sha1,
            LocalPresent: true,
            RemotePresent: true,
            Frozen: false,
            LastSyncedAt: DateTimeOffset.UtcNow);
    }

    private EntryRecord EntryFromNode(string path, EntryKind kind, LocalEntry? local, ProtonNode node)
    {
        return new EntryRecord(
            PathKey.Fold(path),
            path,
            kind,
            kind == EntryKind.Folder ? 0 : local?.Size,
            local?.Mtime.ToUnixTimeMilliseconds(),
            kind == EntryKind.Folder ? null : local?.Sha1,
            node.Uid,
            node.RevisionUid,
            node.Size,
            (node.ClaimedMtime ?? node.ModifiedAt).ToUnixTimeMilliseconds(),
            node.Sha1,
            LocalPresent: local is not null,
            RemotePresent: true,
            Frozen: false,
            LastSyncedAt: DateTimeOffset.UtcNow);
    }

    private EntryRecord EntryFromRemote(string path, EntryKind kind, RemoteEntry remote)
    {
        return new EntryRecord(
            PathKey.Fold(path),
            path,
            kind,
            null,
            null,
            null,
            remote.Uid,
            remote.RevisionUid,
            remote.Size,
            remote.ClaimedMtime?.ToUnixTimeMilliseconds() ?? remote.ModifiedAt.ToUnixTimeMilliseconds(),
            remote.Sha1,
            LocalPresent: false,
            RemotePresent: true,
            Frozen: false,
            LastSyncedAt: DateTimeOffset.UtcNow);
    }

    private LocalEntry? StatLocal(string relativePath)
    {
        var full = LocalFull(relativePath);
        if (Directory.Exists(full))
        {
            return new LocalEntry(relativePath, full, PathKey.Fold(relativePath),
                LocalEntryKind.Directory, 0, new DirectoryInfo(full).LastWriteTimeUtc, null);
        }

        if (!File.Exists(full))
            return null;

        var info = new FileInfo(full);
        return new LocalEntry(
            relativePath,
            full,
            PathKey.Fold(relativePath),
            LocalEntryKind.File,
            info.Length,
            info.LastWriteTimeUtc,
            ContentHash.ComputeSha1(full));
    }

    private string LocalFull(string relativePath) =>
        Path.Combine(_sync.LocalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private string RemoteFull(string relativePath)
    {
        var root = RemotePath.Normalize(_sync.RemoteRoot);
        return relativePath.Length == 0 ? root : root.TrimEnd('/') + "/" + relativePath;
    }

    private static (string Parent, string Name) SplitRelative(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0
            ? (string.Empty, relativePath)
            : (relativePath[..slash], relativePath[(slash + 1)..]);
    }

    private static string Parent(string relativePath) => SplitRelative(relativePath).Parent;

    private static string Leaf(string relativePath) => SplitRelative(relativePath).Name;

    private static string Describe(PlanAction action) =>
        action.Kind + " " + action.Path +
        (action.SecondaryPath is null ? string.Empty : " -> " + action.SecondaryPath);

    private void Record(List<ActionOutcome> outcomes, ActionOutcome outcome)
    {
        outcomes.Add(outcome);
        _log?.Action(
            outcome.Action.Kind.ToString(),
            outcome.Action.Path,
            outcome.Action.PathKey,
            outcome.Action.SecondaryPath,
            outcome.Status,
            outcome.DurationMs,
            outcome.Error,
            outcome.Verification);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Staging remnants are harmless and can be cleaned by the next run.
        }
    }
}
