using PdSync.State;

namespace PdSync.Plan;

/// <summary>
/// One executable step produced by the pure planner. The executor resolves
/// <see cref="Path"/> (relative, POSIX, NFC) against the local/remote roots.
/// </summary>
public enum PlanActionKind
{
    CreateRemoteDirectory,
    CreateLocalDirectory,
    DownloadCreate,
    DownloadReplace,
    DownloadRestore,
    LocalConflictRename,
    UploadCreate,
    UploadRevision,
    KeepBothUpload,
    RemoteMove,
    LocalMove,
    RemoteDirectoryMove,
    LocalDirectoryMove,
    LocalTrash,
    RemoteTrash,
    CleanupStagedUpload,
    DropBase,
}

public sealed record PlanAction(
    PlanActionKind Kind,
    string Path,
    string PathKey,
    string? SecondaryPath = null,
    string? RemoteUid = null,
    string? Reason = null)
{
    /// <summary>Dependency-safe execution phase (SPEC §5.5).</summary>
    public int Phase => Kind switch
    {
        PlanActionKind.CreateRemoteDirectory or PlanActionKind.CreateLocalDirectory => 1,
        PlanActionKind.LocalConflictRename => 2,
        PlanActionKind.DownloadCreate or PlanActionKind.DownloadReplace or PlanActionKind.DownloadRestore => 2,
        PlanActionKind.UploadCreate or PlanActionKind.UploadRevision or PlanActionKind.KeepBothUpload => 3,
        PlanActionKind.RemoteMove or PlanActionKind.RemoteDirectoryMove => 4,
        PlanActionKind.LocalMove or PlanActionKind.LocalDirectoryMove => 5,
        PlanActionKind.LocalTrash => 6,
        PlanActionKind.RemoteTrash or PlanActionKind.CleanupStagedUpload => 7,
        PlanActionKind.DropBase => 9,
        _ => 9,
    };

    /// <summary>Tie-breaker inside a phase; keeps rename-before-download ordering.</summary>
    public int Order => Kind switch
    {
        PlanActionKind.CreateRemoteDirectory or PlanActionKind.CreateLocalDirectory => 10,
        PlanActionKind.LocalConflictRename => 20,
        PlanActionKind.DownloadCreate or PlanActionKind.DownloadReplace or PlanActionKind.DownloadRestore => 30,
        PlanActionKind.UploadCreate or PlanActionKind.UploadRevision or PlanActionKind.KeepBothUpload => 40,
        PlanActionKind.RemoteMove => 50,
        PlanActionKind.RemoteDirectoryMove => 51,
        PlanActionKind.LocalMove => 60,
        PlanActionKind.LocalDirectoryMove => 61,
        PlanActionKind.LocalTrash => 70,
        PlanActionKind.RemoteTrash => 80,
        PlanActionKind.CleanupStagedUpload => 85,
        PlanActionKind.DropBase => 90,
        _ => 90,
    };

    public bool IsUpload => Kind is PlanActionKind.UploadCreate or PlanActionKind.UploadRevision or PlanActionKind.KeepBothUpload;
    public bool IsDownload => Kind is PlanActionKind.DownloadCreate or PlanActionKind.DownloadReplace or PlanActionKind.DownloadRestore;

    /// <summary>
    /// True for user-driven deletions subject to the safety budget. Staged-upload
    /// orphan cleanup is deliberately excluded: it is not a user deletion, does
    /// not count in <see cref="PlanStats.Deletions"/>, and a backlog of leftovers
    /// must never trip the budget that protects user data.
    /// </summary>
    public bool IsDelete => Kind is PlanActionKind.LocalTrash or PlanActionKind.RemoteTrash;
}

public enum PlanConflictKind
{
    BothModified,
    DeleteVsModify,
    KindChange,
    CaseCollision,
    DirectoryDeleteVsModify,
    RenameConflict,
}

public enum PlanResolution
{
    KeepBoth,
    LocalWins,
    RemoteWins,
    Abort,

    /// <summary>
    /// The user postponed this path for the current run. No action is taken and
    /// no baseline row is committed; the next run evaluates it again.
    /// </summary>
    Defer,
}

public sealed record PlanConflict(
    string Path,
    string PathKey,
    PlanConflictKind Kind,
    PlanResolution Resolution,
    string Message,
    DateTimeOffset? LocalMtime = null,
    DateTimeOffset? RemoteMtime = null,
    long? LocalSize = null,
    long? RemoteSize = null,
    bool? DigestsDiffer = null,
    bool AskFallback = false);

/// <summary>
/// CLI-supplied conflict choices for one run. The planner remains pure: it only
/// consumes the resolution map, never prompts or reads a clock.
/// </summary>
public delegate IReadOnlyDictionary<string, PlanResolution> ConflictResolver(
    IReadOnlyList<PlanConflict> blockingConflicts);

public enum PlanSkipReason
{
    Frozen,
    Ignored,
    LocalSkipped,
    IncompleteLocalScan,
    IncompleteRemoteScan,
    PolicyKeep,
    Unsupported,
    Deferred,
}

public sealed record PlanSkip(string Path, string PathKey, PlanSkipReason Reason, string Message);

/// <summary>A path whose baseline can be recorded without transferring content.</summary>
public sealed record PlanAdoption(string Path, string PathKey, EntryKind Kind, string Reason);

public sealed record PlanStats(
    int Considered,
    int Actions,
    int Uploads,
    int Downloads,
    int Deletions,
    int Adoptions,
    int Conflicts,
    int Skipped)
{
    public static PlanStats From(PlanResult result) => new(
        result.Considered,
        result.Actions.Count,
        result.Actions.Count(a => a.IsUpload),
        result.Actions.Count(a => a.IsDownload),
        result.Actions.Count(a => a.IsDelete),
        result.Adoptions.Count,
        result.Conflicts.Count,
        result.Skipped.Count);
}

public sealed class PlanResult
{
    public PlanResult(
        IReadOnlyList<PlanAction> actions,
        IReadOnlyList<PlanConflict> conflicts,
        IReadOnlyList<PlanSkip> skipped,
        IReadOnlyList<PlanAdoption> adoptions,
        int considered)
    {
        Actions = actions;
        Conflicts = conflicts;
        Skipped = skipped;
        Adoptions = adoptions;
        Considered = considered;
        Stats = PlanStats.From(this);
    }

    public IReadOnlyList<PlanAction> Actions { get; }
    public IReadOnlyList<PlanConflict> Conflicts { get; }
    public IReadOnlyList<PlanSkip> Skipped { get; }
    public IReadOnlyList<PlanAdoption> Adoptions { get; }
    public int Considered { get; }
    public PlanStats Stats { get; }

    /// <summary>An aborting conflict means the caller must not execute any action.</summary>
    public bool HasBlockingConflicts => Conflicts.Any(c => c.Resolution == PlanResolution.Abort);

    public bool CanExecute => !HasBlockingConflicts;

    /// <summary>
    /// Paths the user explicitly deferred through an interactive prompt. They are
    /// not blocking conflicts (no action was attempted), but the run is still
    /// unresolved and must exit 2.
    /// </summary>
    public bool HasDeferredConflicts => Skipped.Any(s => s.Reason == PlanSkipReason.Deferred);

    /// <summary>
    /// A non-interactive <c>ask</c> run that fell back to keep-both. The plan can
    /// execute, but the conflict was not actually resolved, so the caller exits 2
    /// exactly as the ask policy documents.
    /// </summary>
    public bool HasAskFallback => Conflicts.Any(c => c.AskFallback);

    public bool HasUnresolvedConflicts =>
        HasBlockingConflicts || HasDeferredConflicts || HasAskFallback;
}

public sealed record PlanContext(
    DateTimeOffset Now,
    bool Interactive = false,
    bool CleanupStaged = false,
    IReadOnlyDictionary<string, PlanResolution>? ConflictOverrides = null)
{
    public static PlanContext Default => new(DateTimeOffset.UtcNow);
}

public sealed class PlanException : Exception
{
    public PlanException(string message)
        : base(message)
    {
    }
}
