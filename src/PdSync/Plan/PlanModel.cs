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
    LocalTrash,
    RemoteTrash,
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
        PlanActionKind.RemoteMove => 4,
        PlanActionKind.LocalMove => 5,
        PlanActionKind.LocalTrash => 6,
        PlanActionKind.RemoteTrash => 7,
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
        PlanActionKind.LocalMove => 60,
        PlanActionKind.LocalTrash => 70,
        PlanActionKind.RemoteTrash => 80,
        PlanActionKind.DropBase => 90,
        _ => 90,
    };

    public bool IsUpload => Kind is PlanActionKind.UploadCreate or PlanActionKind.UploadRevision or PlanActionKind.KeepBothUpload;
    public bool IsDownload => Kind is PlanActionKind.DownloadCreate or PlanActionKind.DownloadReplace or PlanActionKind.DownloadRestore;
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
}

public sealed record PlanConflict(
    string Path,
    string PathKey,
    PlanConflictKind Kind,
    PlanResolution Resolution,
    string Message,
    DateTimeOffset? LocalMtime = null,
    DateTimeOffset? RemoteMtime = null);

public enum PlanSkipReason
{
    Frozen,
    Ignored,
    LocalSkipped,
    IncompleteLocalScan,
    IncompleteRemoteScan,
    PolicyKeep,
    Unsupported,
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
}

public sealed record PlanContext(DateTimeOffset Now, bool Interactive = false)
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
