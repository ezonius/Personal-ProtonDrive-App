using System.Globalization;
using PdSync.Config;
using PdSync.Proton;
using PdSync.Remote;
using PdSync.Scan;
using PdSync.State;

namespace PdSync.Plan;

/// <summary>
/// Pure decision engine (SPEC §5). It performs no I/O: the caller supplies the
/// B snapshot, the scanned local tree, and the remote listing. The same result
/// therefore drives both <c>plan</c> and <c>run</c>.
/// </summary>
public static class Planner
{
    public static PlanResult Plan(
        IReadOnlyList<EntryRecord> baseEntries,
        LocalScan local,
        RemoteSnapshot remote,
        SyncConfig sync,
        PlanContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(baseEntries);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(sync);
        context ??= PlanContext.Default;

        var baseByKey = new Dictionary<string, EntryRecord>(StringComparer.Ordinal);
        var frozenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in baseEntries)
        {
            if (entry.Frozen)
            {
                frozenKeys.Add(entry.PathKey);
                continue;
            }

            if (!baseByKey.TryAdd(entry.PathKey, entry))
                throw new PlanException($"duplicate baseline path key: {entry.PathKey}");
        }

        // Case collisions are hard errors: no action may be executed.
        if (local.Collisions.Count > 0 || remote.Collisions.Count > 0)
            return CollisionResult(local, remote);

        var actions = new List<PlanAction>();
        var conflicts = new List<PlanConflict>();
        var skipped = new List<PlanSkip>();
        var adoptions = new List<PlanAdoption>();

        var ignoreRules = local.Rules;
        var ignoredPrefixes = local.IgnoredByKey.Keys.Select(k => k + "/").ToList();
        var skippedPrefixes = local.SkippedByKey.Keys.Select(k => k + "/").ToList();
        var deletedLocalPrefixes = new List<string>();
        var deletedRemotePrefixes = new List<string>();
        var blockedPrefixes = new List<string>();
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var conflictNames = new HashSet<string>(baseByKey.Keys, StringComparer.Ordinal);
        conflictNames.UnionWith(local.ByKey.Keys);
        conflictNames.UnionWith(remote.ByKey.Keys);

        var localRenames = new Dictionary<string, LocalEntry>(StringComparer.Ordinal);
        var remoteRenames = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
        var renameTargets = new HashSet<string>(StringComparer.Ordinal);
        var renameBlockedSources = new HashSet<string>(StringComparer.Ordinal);
        var renameBlockedTargets = new HashSet<string>(StringComparer.Ordinal);

        if (sync.Options.DetectRenames && local.IsComplete && remote.IsComplete)
        {
            localRenames = DetectLocalRenames(baseByKey, local, remote, ignoredPrefixes, skippedPrefixes);
            remoteRenames = DetectRemoteRenames(
                baseByKey, local, remote, ignoredPrefixes, skippedPrefixes, ignoreRules,
                conflicts, renameBlockedSources, renameBlockedTargets);
        }

        renameTargets.UnionWith(localRenames.Values.Select(e => e.PathKey));
        renameTargets.UnionWith(remoteRenames.Values.Select(e => e.PathKey));

        var keys = new SortedSet<string>(StringComparer.Ordinal);
        keys.UnionWith(baseByKey.Keys);
        keys.UnionWith(local.ByKey.Keys);
        keys.UnionWith(remote.ByKey.Keys);

        foreach (var key in keys)
        {
            var b = baseByKey.GetValueOrDefault(key);
            var l = local.ByKey.GetValueOrDefault(key);
            var r = remote.ByKey.GetValueOrDefault(key);

            if (b is null && l is null && r is null)
                continue;

            if (frozenKeys.Contains(key))
            {
                skipped.Add(new PlanSkip(DisplayPath(b, l, r, key), key, PlanSkipReason.Frozen,
                    "baseline entry is frozen; no action"));
                continue;
            }

            if (IsUnderAny(key, deletedLocalPrefixes) || IsUnderAny(key, deletedRemotePrefixes))
            {
                DropBaseIfNeeded(b, key, actions, dropped);
                continue;
            }

            if (IsUnderAny(key, blockedPrefixes))
                continue;

            if (local.IgnoredByKey.ContainsKey(key) || IsUnderAny(key, ignoredPrefixes))
            {
                skipped.Add(new PlanSkip(DisplayPath(b, l, r, key), key, PlanSkipReason.Ignored,
                    "path is ignored; existing baseline is frozen and not deleted"));
                continue;
            }

            if (r is not null && ignoreRules is not null &&
                ignoreRules.IsIgnored(r.Path, r.IsDirectory))
            {
                skipped.Add(new PlanSkip(r.Path, key, PlanSkipReason.Ignored,
                    "remote path is ignored; no download or deletion"));
                continue;
            }

            if (local.SkippedByKey.ContainsKey(key) || IsUnderAny(key, skippedPrefixes))
            {
                skipped.Add(new PlanSkip(DisplayPath(b, l, r, key), key, PlanSkipReason.LocalSkipped,
                    "local path was skipped; it is not treated as a deletion"));
                continue;
            }

            // A remote-only staging node with the exact staged-upload grammar is
            // a leftover from an interrupted rename/verification. Trash it
            // instead of inventing a local file. Baseline-tracked, adopted, and
            // any non-matching names keep their normal plan path.
            if (b is null && l is null && r is not null && !r.IsDirectory &&
                StagedUpload.IsStagingLeaf(Leaf(r.Path)))
            {
                actions.Add(new PlanAction(PlanActionKind.CleanupStagedUpload, r.Path, key,
                    RemoteUid: r.Uid,
                    Reason: "automatic cleanup: baseline-absent leftover remote staging node from a failed upload rename or verification"));
                continue;
            }

            // Opt-in only (run --cleanup-staged). A staging node that is already
            // recorded in the baseline (adopted or historical) is never trashed
            // automatically: it may have been user content. The explicit flag
            // allows trashing a remote-only artifact that would otherwise be
            // restored or conflict on every run.
            // local.IsComplete is required: an incomplete scan can leave a
            // present local file out of Entries, and trashing its remote node
            // would violate the local-absent guard (SPEC §8). The ordinary
            // IncompleteLocalScan deferral below handles that case.
            if (context.CleanupStaged && local.IsComplete &&
                b is not null && l is null && r is not null &&
                !r.IsDirectory && StagedUpload.IsStagingLeaf(Leaf(r.Path)))
            {
                actions.Add(new PlanAction(PlanActionKind.CleanupStagedUpload, r.Path, key,
                    RemoteUid: r.Uid,
                    Reason: "opt-in cleanup: adopted/historical remote staging node left by a pre-#21 or baseline-tracked upload"));
                continue;
            }

            if (b?.Frozen == true)
            {
                skipped.Add(new PlanSkip(b.Path, key, PlanSkipReason.Frozen, "baseline entry is frozen"));
                continue;
            }

            if (renameBlockedSources.Contains(key) || renameBlockedTargets.Contains(key) ||
                renameTargets.Contains(key))
                continue;

            if (localRenames.TryGetValue(key, out var renamedLocal))
            {
                var oldRemote = remote.Get(b!.PathKey);
                actions.Add(new PlanAction(PlanActionKind.RemoteMove, b.Path, key,
                    SecondaryPath: renamedLocal.Path,
                    RemoteUid: oldRemote?.Uid,
                    Reason: "local file was renamed; move the remote node"));
                renameTargets.Add(renamedLocal.PathKey);
                continue;
            }

            if (remoteRenames.TryGetValue(key, out var renamedRemote))
            {
                actions.Add(new PlanAction(PlanActionKind.LocalMove, b!.Path, key,
                    SecondaryPath: renamedRemote.Path,
                    RemoteUid: renamedRemote.Uid,
                    Reason: "remote file was renamed; move the local file"));
                renameTargets.Add(renamedRemote.PathKey);
                continue;
            }

            // A missing local/remote path is not a deletion when the corresponding
            // snapshot is incomplete.
            if (b is not null && l is null && !local.IsComplete)
            {
                skipped.Add(new PlanSkip(b.Path, key, PlanSkipReason.IncompleteLocalScan,
                    "local scan was incomplete; deletion is deferred"));
                continue;
            }

            if (b is not null && r is null && !remote.IsComplete)
            {
                skipped.Add(new PlanSkip(b.Path, key, PlanSkipReason.IncompleteRemoteScan,
                    "remote listing was incomplete; deletion is deferred"));
                continue;
            }

            if (r is null && remote.SkippedByKey.ContainsKey(key))
            {
                skipped.Add(new PlanSkip(b?.Path ?? key, key, PlanSkipReason.Unsupported,
                    "remote path is not representable; deletion is deferred"));
                continue;
            }

            PlanOne(
                sync,
                context,
                key,
                b,
                l,
                r,
                baseByKey,
                local,
                remote,
                actions,
                conflicts,
                skipped,
                adoptions,
                deletedLocalPrefixes,
                deletedRemotePrefixes,
                blockedPrefixes,
                dropped,
                conflictNames);
        }

        actions.Sort(CompareActions);
        conflicts.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        skipped.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        adoptions.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));

        return new PlanResult(actions, conflicts, skipped, adoptions, keys.Count);
    }

    private static void PlanOne(
        SyncConfig sync,
        PlanContext context,
        string key,
        EntryRecord? b,
        LocalEntry? l,
        RemoteEntry? r,
        IReadOnlyDictionary<string, EntryRecord> baseByKey,
        LocalScan local,
        RemoteSnapshot remote,
        List<PlanAction> actions,
        List<PlanConflict> conflicts,
        List<PlanSkip> skipped,
        List<PlanAdoption> adoptions,
        List<string> deletedLocalPrefixes,
        List<string> deletedRemotePrefixes,
        List<string> blockedPrefixes,
        HashSet<string> dropped,
        HashSet<string> conflictNames)
    {
        if (b is null)
        {
            if (l is null && r is null)
                return;

            if (l is null)
            {
                if (r!.IsDirectory)
                    actions.Add(Action(PlanActionKind.CreateLocalDirectory, r.Path, "remote directory has no local counterpart"));
                else
                    actions.Add(Action(PlanActionKind.DownloadCreate, r.Path, "remote-only file"));
                return;
            }

            if (r is null)
            {
                if (l.IsDirectory)
                    actions.Add(Action(PlanActionKind.CreateRemoteDirectory, l.Path, "local directory has no remote counterpart"));
                else
                    actions.Add(Action(PlanActionKind.UploadCreate, l.Path, "local-only file"));
                return;
            }

            // First sighting on both sides.
            if (l.IsDirectory != r.IsDirectory)
            {
                conflicts.Add(KindConflict(l.Path, key, l.IsDirectory, r.IsDirectory));
                blockedPrefixes.Add(key + "/");
                return;
            }

            if (l.IsDirectory)
            {
                adoptions.Add(new PlanAdoption(l.Path, key, EntryKind.Folder, "initial adoption: both sides are directories"));
                return;
            }

            if (Converged(l, r, sync.Options.MtimeToleranceMs))
            {
                adoptions.Add(new PlanAdoption(l.Path, key, EntryKind.File, "initial adoption: content already matches"));
                return;
            }

            ApplyInitialConflict(sync, context, key, l, r, actions, conflicts, conflictNames);
            return;
        }

        if (l is null && r is null)
        {
            DropBaseIfNeeded(b, key, actions, dropped);
            return;
        }

        if (l is null)
        {
            PlanLocalDelete(sync, key, b, r!, local, remote, baseByKey, actions, conflicts, skipped,
                deletedRemotePrefixes, blockedPrefixes, dropped);
            return;
        }

        if (r is null)
        {
            PlanRemoteDelete(sync, key, b, l, local, baseByKey, actions, conflicts, skipped,
                deletedLocalPrefixes, blockedPrefixes, dropped);
            return;
        }

        // Both sides exist and the baseline exists.
        if (b.Kind == EntryKind.Folder)
        {
            if (!r.IsDirectory || !l.IsDirectory)
            {
                conflicts.Add(KindConflict(l.Path, key, l.IsDirectory, r.IsDirectory));
                blockedPrefixes.Add(key + "/");
                return;
            }

            // Directory metadata (size/mtime) is not synchronized; children are
            // handled as their own entries.
            return;
        }

        if (l.IsDirectory || r.IsDirectory)
        {
            conflicts.Add(KindConflict(l.Path, key, l.IsDirectory, r.IsDirectory));
            blockedPrefixes.Add(key + "/");
            return;
        }

        var localChanged = LocalChanged(b, l);
        var remoteChanged = RemoteChanged(b, r);

        if (!localChanged && !remoteChanged)
            return;

        if (localChanged && !remoteChanged)
        {
            actions.Add(Action(PlanActionKind.UploadRevision, l.Path, "local content changed"));
            return;
        }

        if (!localChanged && remoteChanged)
        {
            actions.Add(Action(PlanActionKind.DownloadReplace, l.Path, "remote content changed", remoteUid: r.Uid));
            return;
        }

        if (Converged(l, r, sync.Options.MtimeToleranceMs))
        {
            adoptions.Add(new PlanAdoption(l.Path, key, EntryKind.File, "both sides changed but converged"));
            return;
        }

        ApplyBothModified(sync, context, key, l, r, actions, conflicts, conflictNames);
    }

    private static void PlanLocalDelete(
        SyncConfig sync,
        string key,
        EntryRecord b,
        RemoteEntry r,
        LocalScan local,
        RemoteSnapshot remote,
        IReadOnlyDictionary<string, EntryRecord> baseByKey,
        List<PlanAction> actions,
        List<PlanConflict> conflicts,
        List<PlanSkip> skipped,
        List<string> deletedRemotePrefixes,
        List<string> blockedPrefixes,
        HashSet<string> dropped)
    {
        if (b.Kind == EntryKind.Folder)
        {
            if (!r.IsDirectory)
            {
                conflicts.Add(KindConflict(b.Path, key, isLocalDirectory: false, isRemoteDirectory: true));
                blockedPrefixes.Add(key + "/");
                return;
            }

            if (sync.Options.OnLocalDelete == LocalDeletePolicy.Keep)
            {
                skipped.Add(new PlanSkip(b.Path, key, PlanSkipReason.PolicyKeep,
                    "local directory deletion is ignored by onLocalDelete: keep"));
                return;
            }

            if (!RemoteSubtreeUnchanged(baseByKey, remote, key))
            {
                conflicts.Add(new PlanConflict(b.Path, key, PlanConflictKind.DirectoryDeleteVsModify,
                    PlanResolution.Abort,
                    "local directory was deleted but the remote subtree changed; refusing to trash it"));
                blockedPrefixes.Add(key + "/");
                return;
            }

            actions.Add(Action(PlanActionKind.RemoteTrash, b.Path, "local directory was deleted", remoteUid: r.Uid));
            AddSubtreeDrops(baseByKey, key, actions, dropped);
            deletedRemotePrefixes.Add(key + "/");
            return;
        }

        if (r.IsDirectory)
        {
            conflicts.Add(KindConflict(b.Path, key, isLocalDirectory: false, isRemoteDirectory: true));
            return;
        }

        if (RemoteChanged(b, r))
        {
            if (sync.Options.OnLocalDelete == LocalDeletePolicy.Keep)
            {
                skipped.Add(new PlanSkip(b.Path, key, PlanSkipReason.PolicyKeep,
                    "local deletion is ignored; modified remote content survives"));
                return;
            }

            actions.Add(Action(PlanActionKind.DownloadRestore, b.Path,
                "local deletion raced a remote modification; remote content wins", remoteUid: r.Uid));
            conflicts.Add(new PlanConflict(b.Path, key, PlanConflictKind.DeleteVsModify,
                PlanResolution.RemoteWins,
                "local file was deleted but remote changed; restoring the remote file"));
            return;
        }

        if (sync.Options.OnLocalDelete == LocalDeletePolicy.Keep)
        {
            skipped.Add(new PlanSkip(b.Path, key, PlanSkipReason.PolicyKeep,
                "local file deletion is ignored by onLocalDelete: keep"));
            return;
        }

        actions.Add(Action(PlanActionKind.RemoteTrash, b.Path, "local file was deleted", remoteUid: r.Uid));
    }

    private static void PlanRemoteDelete(
        SyncConfig sync,
        string key,
        EntryRecord b,
        LocalEntry l,
        LocalScan local,
        IReadOnlyDictionary<string, EntryRecord> baseByKey,
        List<PlanAction> actions,
        List<PlanConflict> conflicts,
        List<PlanSkip> skipped,
        List<string> deletedLocalPrefixes,
        List<string> blockedPrefixes,
        HashSet<string> dropped)
    {
        if (b.Kind == EntryKind.Folder)
        {
            if (!l.IsDirectory)
            {
                conflicts.Add(KindConflict(l.Path, key, isLocalDirectory: false, isRemoteDirectory: false));
                blockedPrefixes.Add(key + "/");
                return;
            }

            if (sync.Options.OnRemoteDelete == RemoteDeletePolicy.Keep)
            {
                skipped.Add(new PlanSkip(l.Path, key, PlanSkipReason.PolicyKeep,
                    "remote directory deletion is ignored by onRemoteDelete: keep"));
                return;
            }

            if (local.Collisions.Count > 0 || !LocalSubtreeUnchanged(baseByKey, local, key))
            {
                conflicts.Add(new PlanConflict(l.Path, key, PlanConflictKind.DirectoryDeleteVsModify,
                    PlanResolution.Abort,
                    "remote directory was deleted but the local subtree changed; refusing to trash it"));
                blockedPrefixes.Add(key + "/");
                return;
            }

            actions.Add(Action(PlanActionKind.LocalTrash, l.Path, "remote directory was deleted"));
            AddSubtreeDrops(baseByKey, key, actions, dropped);
            deletedLocalPrefixes.Add(key + "/");
            return;
        }

        if (l.IsDirectory)
        {
            conflicts.Add(KindConflict(l.Path, key, isLocalDirectory: true, isRemoteDirectory: false));
            return;
        }

        if (LocalChanged(b, l))
        {
            actions.Add(Action(PlanActionKind.UploadCreate, l.Path,
                "remote deletion raced a local modification; local content wins"));
            conflicts.Add(new PlanConflict(l.Path, key, PlanConflictKind.DeleteVsModify,
                PlanResolution.LocalWins,
                "remote file was deleted but local changed; re-uploading the local file"));
            return;
        }

        if (sync.Options.OnRemoteDelete == RemoteDeletePolicy.Keep)
        {
            skipped.Add(new PlanSkip(l.Path, key, PlanSkipReason.PolicyKeep,
                "remote file deletion is ignored by onRemoteDelete: keep"));
            return;
        }

        actions.Add(Action(PlanActionKind.LocalTrash, l.Path, "remote file was deleted"));
    }

    private static Dictionary<string, LocalEntry> DetectLocalRenames(
        IReadOnlyDictionary<string, EntryRecord> baseByKey,
        LocalScan local,
        RemoteSnapshot remote,
        IReadOnlyList<string> ignoredPrefixes,
        IReadOnlyList<string> skippedPrefixes)
    {
        var result = new Dictionary<string, LocalEntry>(StringComparer.Ordinal);
        var usedTargets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var b in baseByKey.Values.OrderBy(e => e.PathKey, StringComparer.Ordinal))
        {
            if (b.Kind != EntryKind.File || b.Size is null || string.IsNullOrEmpty(b.Sha1))
                continue;
            if (local.ByKey.ContainsKey(b.PathKey))
                continue;
            if (KeyBlocked(b.PathKey, local, ignoredPrefixes, skippedPrefixes))
                continue;

            var oldRemote = remote.Get(b.PathKey);
            if (oldRemote is null || oldRemote.IsDirectory || RemoteChanged(b, oldRemote))
                continue;

            var candidates = local.ByKey.Values
                .Where(l => l.Kind == LocalEntryKind.File &&
                            !baseByKey.ContainsKey(l.PathKey) &&
                            remote.Get(l.PathKey) is null &&
                            l.Size == b.Size &&
                            !string.IsNullOrEmpty(l.Sha1) &&
                            string.Equals(l.Sha1, b.Sha1, StringComparison.OrdinalIgnoreCase) &&
                            !KeyBlocked(l.PathKey, local, ignoredPrefixes, skippedPrefixes) &&
                            !usedTargets.Contains(l.PathKey))
                .ToList();

            if (candidates.Count != 1)
                continue;

            var target = candidates[0];
            result[b.PathKey] = target;
            usedTargets.Add(target.PathKey);
        }

        return result;
    }

    private static Dictionary<string, RemoteEntry> DetectRemoteRenames(
        IReadOnlyDictionary<string, EntryRecord> baseByKey,
        LocalScan local,
        RemoteSnapshot remote,
        IReadOnlyList<string> ignoredPrefixes,
        IReadOnlyList<string> skippedPrefixes,
        IgnoreRules? ignoreRules,
        List<PlanConflict> conflicts,
        HashSet<string> blockedSources,
        HashSet<string> blockedTargets)
    {
        var result = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);

        foreach (var b in baseByKey.Values.OrderBy(e => e.PathKey, StringComparer.Ordinal))
        {
            if (b.Kind != EntryKind.File || string.IsNullOrEmpty(b.RemoteUid))
                continue;
            if (remote.Get(b.PathKey) is not null)
                continue;

            var moved = remote.GetByUid(b.RemoteUid);
            if (moved is null || moved.IsDirectory || moved.PathKey == b.PathKey)
                continue;
            if (KeyBlocked(moved.PathKey, local, ignoredPrefixes, skippedPrefixes))
                continue;
            if (ignoreRules is not null && ignoreRules.IsIgnored(moved.Path, moved.IsDirectory))
                continue;

            var localOld = local.ByKey.GetValueOrDefault(b.PathKey);
            if (localOld is null)
                continue; // the remote moved; normal remote-only handling downloads the new path

            if (RemoteChanged(b, moved))
            {
                conflicts.Add(new PlanConflict(b.Path, b.PathKey, PlanConflictKind.RenameConflict,
                    PlanResolution.Abort,
                    "remote file was renamed and modified; refusing an ambiguous local move"));
                blockedSources.Add(b.PathKey);
                blockedTargets.Add(moved.PathKey);
                continue;
            }

            if (localOld.IsDirectory || LocalChanged(b, localOld))
            {
                conflicts.Add(new PlanConflict(b.Path, b.PathKey, PlanConflictKind.RenameConflict,
                    PlanResolution.Abort,
                    "remote file was renamed but the local copy changed; refusing to move it"));
                blockedSources.Add(b.PathKey);
                blockedTargets.Add(moved.PathKey);
                continue;
            }

            if (local.ByKey.ContainsKey(moved.PathKey) || baseByKey.ContainsKey(moved.PathKey))
            {
                conflicts.Add(new PlanConflict(b.Path, b.PathKey, PlanConflictKind.RenameConflict,
                    PlanResolution.Abort,
                    "remote file was renamed onto a path that already exists locally or in the baseline; refusing to overwrite it"));
                blockedSources.Add(b.PathKey);
                blockedTargets.Add(moved.PathKey);
                continue;
            }

            result[b.PathKey] = moved;
        }

        return result;
    }

    private static bool KeyBlocked(
        string key,
        LocalScan local,
        IReadOnlyList<string> ignoredPrefixes,
        IReadOnlyList<string> skippedPrefixes) =>
        local.IgnoredByKey.ContainsKey(key) || IsUnderAny(key, ignoredPrefixes) ||
        local.SkippedByKey.ContainsKey(key) || IsUnderAny(key, skippedPrefixes);

    private static void ApplyInitialConflict(
        SyncConfig sync,
        PlanContext context,
        string key,
        LocalEntry l,
        RemoteEntry r,
        List<PlanAction> actions,
        List<PlanConflict> conflicts,
        HashSet<string> conflictNames)
    {
        switch (sync.Options.InitialSync)
        {
            case InitialSyncPolicy.LocalWins:
                actions.Add(Action(PlanActionKind.UploadRevision, l.Path, "initial sync: local wins", remoteUid: r.Uid));
                conflicts.Add(new PlanConflict(l.Path, key, PlanConflictKind.BothModified, PlanResolution.LocalWins,
                    "initial sync found different content; local wins", l.Mtime, r.ClaimedMtime));
                return;
            case InitialSyncPolicy.RemoteWins:
                actions.Add(Action(PlanActionKind.DownloadReplace, l.Path, "initial sync: remote wins", remoteUid: r.Uid));
                conflicts.Add(new PlanConflict(l.Path, key, PlanConflictKind.BothModified, PlanResolution.RemoteWins,
                    "initial sync found different content; remote wins", l.Mtime, r.ClaimedMtime));
                return;
            default:
                EmitKeepBoth(context, key, l, r, actions, conflicts, conflictNames,
                    "initial sync found different content; keeping both", PlanConflictKind.BothModified);
                return;
        }
    }

    private static void ApplyBothModified(
        SyncConfig sync,
        PlanContext context,
        string key,
        LocalEntry l,
        RemoteEntry r,
        List<PlanAction> actions,
        List<PlanConflict> conflicts,
        HashSet<string> conflictNames)
    {
        switch (sync.Options.Conflict)
        {
            case ConflictPolicy.LocalWins:
                actions.Add(Action(PlanActionKind.UploadRevision, l.Path, "conflict: local wins", remoteUid: r.Uid));
                conflicts.Add(new PlanConflict(l.Path, key, PlanConflictKind.BothModified, PlanResolution.LocalWins,
                    "both sides changed; local wins", l.Mtime, r.ClaimedMtime));
                return;
            case ConflictPolicy.RemoteWins:
                actions.Add(Action(PlanActionKind.DownloadReplace, l.Path, "conflict: remote wins", remoteUid: r.Uid));
                conflicts.Add(new PlanConflict(l.Path, key, PlanConflictKind.BothModified, PlanResolution.RemoteWins,
                    "both sides changed; remote wins", l.Mtime, r.ClaimedMtime));
                return;
            case ConflictPolicy.KeepBoth:
                EmitKeepBoth(context, key, l, r, actions, conflicts, conflictNames,
                    "both sides changed; keeping both", PlanConflictKind.BothModified);
                return;
            case ConflictPolicy.Ask:
                if (context.Interactive)
                {
                    conflicts.Add(new PlanConflict(l.Path, key, PlanConflictKind.BothModified, PlanResolution.Abort,
                        "both sides changed; interactive resolution required", l.Mtime, r.ClaimedMtime));
                    return;
                }
                EmitKeepBoth(context, key, l, r, actions, conflicts, conflictNames,
                    "both sides changed; non-interactive ask falls back to keep-both", PlanConflictKind.BothModified);
                return;
            case ConflictPolicy.NewerWins:
            default:
                ResolveNewerWins(sync, context, key, l, r, actions, conflicts, conflictNames);
                return;
        }
    }

    private static void ResolveNewerWins(
        SyncConfig sync,
        PlanContext context,
        string key,
        LocalEntry l,
        RemoteEntry r,
        List<PlanAction> actions,
        List<PlanConflict> conflicts,
        HashSet<string> conflictNames)
    {
        if (r.ClaimedMtime is null)
        {
            EmitKeepBoth(context, key, l, r, actions, conflicts, conflictNames,
                "both sides changed and remote mtime is missing; keeping both", PlanConflictKind.BothModified);
            return;
        }

        var tolerance = sync.Options.MtimeToleranceMs;
        var localMs = l.Mtime.ToUnixTimeMilliseconds();
        var remoteMs = r.ClaimedMtime.Value.ToUnixTimeMilliseconds();
        var difference = Math.Abs(localMs - remoteMs);
        var future = l.Mtime > context.Now.AddMilliseconds(tolerance) ||
                     r.ClaimedMtime.Value > context.Now.AddMilliseconds(tolerance);

        if (difference <= tolerance || future)
        {
            EmitKeepBoth(context, key, l, r, actions, conflicts, conflictNames,
                "both sides changed within the mtime tolerance (or clock skew); keeping both",
                PlanConflictKind.BothModified);
            return;
        }

        if (localMs > remoteMs)
        {
            actions.Add(Action(PlanActionKind.UploadRevision, l.Path, "conflict: local is newer", remoteUid: r.Uid));
            conflicts.Add(new PlanConflict(l.Path, key, PlanConflictKind.BothModified, PlanResolution.LocalWins,
                "both sides changed; local is newer", l.Mtime, r.ClaimedMtime));
        }
        else
        {
            actions.Add(Action(PlanActionKind.DownloadReplace, l.Path, "conflict: remote is newer", remoteUid: r.Uid));
            conflicts.Add(new PlanConflict(l.Path, key, PlanConflictKind.BothModified, PlanResolution.RemoteWins,
                "both sides changed; remote is newer", l.Mtime, r.ClaimedMtime));
        }
    }

    private static void EmitKeepBoth(
        PlanContext context,
        string key,
        LocalEntry l,
        RemoteEntry r,
        List<PlanAction> actions,
        List<PlanConflict> conflicts,
        HashSet<string> conflictNames,
        string message,
        PlanConflictKind kind)
    {
        var conflictPath = BuildConflictPath(l.Path, context.Now, conflictNames);
        var conflictKey = PathKey.Fold(conflictPath);

        actions.Add(new PlanAction(PlanActionKind.LocalConflictRename, l.Path, key,
            SecondaryPath: conflictPath,
            Reason: "rename the local copy before downloading the remote original"));
        actions.Add(new PlanAction(PlanActionKind.DownloadReplace, l.Path, key,
            RemoteUid: r.Uid,
            Reason: "download the remote original"));
        actions.Add(new PlanAction(PlanActionKind.KeepBothUpload, conflictPath, conflictKey,
            SecondaryPath: l.Path,
            Reason: "upload the renamed local conflict copy"));

        conflicts.Add(new PlanConflict(l.Path, key, kind, PlanResolution.KeepBoth,
            message, l.Mtime, r.ClaimedMtime));
    }

    private static PlanConflict KindConflict(string path, string key, bool isLocalDirectory, bool isRemoteDirectory) =>
        new(path, key, PlanConflictKind.KindChange, PlanResolution.Abort,
            $"kind changed (local {(isLocalDirectory ? "directory" : "file")}, " +
            $"remote {(isRemoteDirectory ? "directory" : "file")}); refusing to replace silently");

    private static PlanResult CollisionResult(LocalScan local, RemoteSnapshot remote)
    {
        var conflicts = new List<PlanConflict>();
        foreach (var collision in local.Collisions)
        {
            conflicts.Add(new PlanConflict(collision.FirstPath, collision.PathKey,
                PlanConflictKind.CaseCollision, PlanResolution.Abort,
                $"case collision between '{collision.FirstPath}' and '{collision.SecondPath}'"));
        }

        foreach (var collision in remote.Collisions)
        {
            conflicts.Add(new PlanConflict(collision.FirstPath, collision.PathKey,
                PlanConflictKind.CaseCollision, PlanResolution.Abort,
                $"remote case collision between '{collision.FirstPath}' and '{collision.SecondPath}'"));
        }

        return new PlanResult(
            Array.Empty<PlanAction>(),
            conflicts,
            Array.Empty<PlanSkip>(),
            Array.Empty<PlanAdoption>(),
            considered: 0);
    }

    private static PlanAction Action(
        PlanActionKind kind,
        string path,
        string reason,
        string? remoteUid = null) =>
        new(kind, path, PathKey.Fold(path), RemoteUid: remoteUid, Reason: reason);

    private static void DropBaseIfNeeded(
        EntryRecord? b,
        string key,
        List<PlanAction> actions,
        HashSet<string> dropped)
    {
        if (b is null || !dropped.Add(key))
            return;

        actions.Add(new PlanAction(PlanActionKind.DropBase, b.Path, key,
            Reason: "baseline path is no longer present on either side"));
    }

    private static void AddSubtreeDrops(
        IReadOnlyDictionary<string, EntryRecord> baseByKey,
        string rootKey,
        List<PlanAction> actions,
        HashSet<string> dropped)
    {
        foreach (var entry in baseByKey.Values)
        {
            if (entry.PathKey == rootKey || IsUnder(entry.PathKey, rootKey + "/"))
                DropBaseIfNeeded(entry, entry.PathKey, actions, dropped);
        }
    }

    private static bool LocalSubtreeUnchanged(
        IReadOnlyDictionary<string, EntryRecord> baseByKey,
        LocalScan local,
        string rootKey)
    {
        var prefix = rootKey + "/";
        foreach (var entry in baseByKey.Values)
        {
            if (entry.PathKey != rootKey && !IsUnder(entry.PathKey, prefix))
                continue;

            var l = local.ByKey.GetValueOrDefault(entry.PathKey);
            if (l is null || LocalChanged(entry, l))
                return false;
        }

        foreach (var l in local.ByKey.Values)
        {
            if (IsUnder(l.PathKey, prefix) && !baseByKey.ContainsKey(l.PathKey))
                return false;
        }

        return true;
    }

    private static bool RemoteSubtreeUnchanged(
        IReadOnlyDictionary<string, EntryRecord> baseByKey,
        RemoteSnapshot remote,
        string rootKey)
    {
        var prefix = rootKey + "/";
        foreach (var entry in baseByKey.Values)
        {
            if (entry.PathKey != rootKey && !IsUnder(entry.PathKey, prefix))
                continue;

            var r = remote.ByKey.GetValueOrDefault(entry.PathKey);
            if (r is null || RemoteChanged(entry, r))
                return false;
        }

        foreach (var r in remote.ByKey.Values)
        {
            if (IsUnder(r.PathKey, prefix) && !baseByKey.ContainsKey(r.PathKey))
                return false;
        }

        return true;
    }

    private static bool LocalChanged(EntryRecord b, LocalEntry l)
    {
        if (b.Kind == EntryKind.Folder)
            return !l.IsDirectory;

        if (l.IsDirectory)
            return true;

        if (b.Size != l.Size)
            return true;

        if (!string.IsNullOrEmpty(b.Sha1) && !string.IsNullOrEmpty(l.Sha1))
            return !string.Equals(b.Sha1, l.Sha1, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static bool RemoteChanged(EntryRecord b, RemoteEntry r)
    {
        if (b.Kind == EntryKind.Folder)
        {
            if (!r.IsDirectory)
                return true;
            return !string.IsNullOrEmpty(b.RemoteUid) &&
                   !string.Equals(b.RemoteUid, r.Uid, StringComparison.Ordinal);
        }

        if (r.IsDirectory)
            return true;

        if (!string.IsNullOrEmpty(b.RemoteUid) &&
            !string.Equals(b.RemoteUid, r.Uid, StringComparison.Ordinal))
            return true;

        if (!string.IsNullOrEmpty(b.RemoteRevUid) || !string.IsNullOrEmpty(r.RevisionUid))
        {
            if (!string.Equals(b.RemoteRevUid, r.RevisionUid, StringComparison.Ordinal))
                return true;
        }

        if (b.RemoteSize != r.Size)
            return true;

        if (!string.IsNullOrEmpty(b.RemoteSha1) && !string.IsNullOrEmpty(r.Sha1))
            return !string.Equals(b.RemoteSha1, r.Sha1, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static bool Converged(LocalEntry l, RemoteEntry r, int toleranceMs)
    {
        if (l.IsDirectory || r.IsDirectory)
            return false;

        if (!string.IsNullOrEmpty(l.Sha1) && !string.IsNullOrEmpty(r.Sha1))
            return string.Equals(l.Sha1, r.Sha1, StringComparison.OrdinalIgnoreCase);

        if (l.Size != r.Size || r.ClaimedMtime is null)
            return false;

        return Math.Abs(l.Mtime.ToUnixTimeMilliseconds() - r.ClaimedMtime.Value.ToUnixTimeMilliseconds()) <= toleranceMs;
    }

    private static string BuildConflictPath(string path, DateTimeOffset now, HashSet<string> used)
    {
        var slash = path.LastIndexOf('/');
        var directory = slash >= 0 ? path[..slash] : string.Empty;
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;
        var stamp = now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);

        for (var attempt = 1; attempt < 1000; attempt++)
        {
            var suffix = attempt == 1 ? string.Empty : $" ({attempt})";
            var candidateName = $"{stem} (conflict {stamp}){suffix}{extension}";
            var candidate = directory.Length > 0 ? directory + "/" + candidateName : candidateName;
            if (used.Add(PathKey.Fold(candidate)))
                return candidate;
        }

        throw new PlanException($"could not allocate a conflict name for {path}");
    }

    private static string DisplayPath(EntryRecord? b, LocalEntry? l, RemoteEntry? r, string key) =>
        l?.Path ?? r?.Path ?? b?.Path ?? key;

    private static string Leaf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }

    private static bool IsUnderAny(string key, IEnumerable<string> prefixes) =>
        prefixes.Any(prefix => IsUnder(key, prefix));

    private static bool IsUnder(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.Ordinal);

    private static int CompareActions(PlanAction a, PlanAction b)
    {
        var byPhase = a.Phase.CompareTo(b.Phase);
        if (byPhase != 0)
            return byPhase;

        var byOrder = a.Order.CompareTo(b.Order);
        if (byOrder != 0)
            return byOrder;

        var byPath = StringComparer.Ordinal.Compare(a.Path, b.Path);
        if (byPath != 0)
            return byPath;

        return StringComparer.Ordinal.Compare(a.Kind.ToString(), b.Kind.ToString());
    }
}
