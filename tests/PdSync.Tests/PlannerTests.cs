using FluentAssertions;
using PdSync.Config;
using PdSync.Plan;
using PdSync.Proton;
using PdSync.Scan;
using PdSync.State;

namespace PdSync.Tests;

public sealed class PlannerTests
{
    private static readonly DateTimeOffset Now = new(2024, 1, 2, 12, 0, 0, TimeSpan.Zero);
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void LocalOnlyFile_Uploads()
    {
        var result = Plan(
            baseEntries: [],
            local: Scan(Local("a.txt", "a", 1000)),
            remote: Remote());

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.UploadCreate);
        result.Actions[0].Path.Should().Be("a.txt");
        result.Conflicts.Should().BeEmpty();
        result.CanExecute.Should().BeTrue();
    }

    [Fact]
    public void RemoteOnlyFile_Downloads()
    {
        var result = Plan(
            baseEntries: [],
            local: Scan(),
            remote: Remote(RemoteFile("a.txt", "uid-a", "rev-a", "a", ShaA)));

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.DownloadCreate);
        result.Actions[0].Path.Should().Be("a.txt");
    }

    [Fact]
    public void BothSidesPresentWithSameContent_IsAdoptedWithoutTransfer()
    {
        var result = Plan(
            baseEntries: [],
            local: Scan(Local("a.txt", "a", 1000)),
            remote: Remote(RemoteFile("a.txt", "uid-a", "rev-a", "a", ShaA)));

        result.Actions.Should().BeEmpty();
        result.Adoptions.Should().ContainSingle()
            .Which.Path.Should().Be("a.txt");
        result.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void InitialDifference_FallsBackToKeepBothUnderAdopt()
    {
        var result = Plan(
            baseEntries: [],
            local: Scan(Local("a.txt", "local", 1000)),
            remote: Remote(RemoteFile("a.txt", "uid-a", "rev-a", "remote", ShaA)));

        result.Conflicts.Should().ContainSingle()
            .Which.Resolution.Should().Be(PlanResolution.KeepBoth);
        result.Actions.Select(a => a.Kind).Should().Contain(new[]
        {
            PlanActionKind.LocalConflictRename,
            PlanActionKind.DownloadReplace,
            PlanActionKind.KeepBothUpload,
        });
        result.Adoptions.Should().BeEmpty();
    }

    [Fact]
    public void UnchangedOnBothSides_DoesNothing()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan([b], Scan(Local("a.txt", "a", 1000)), Remote(RemoteFile("a.txt", "uid-a", "rev-a", "a", ShaA)));

        result.Actions.Should().BeEmpty();
        result.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void LocalChangeOnly_UploadsRevision()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan([b], Scan(Local("a.txt", "changed", 2000)), Remote(RemoteFile("a.txt", "uid-a", "rev-a", "a", ShaA)));

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.UploadRevision);
        result.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void RemoteChangeOnly_DownloadsReplacement()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan([b], Scan(Local("a.txt", "a", 1000)), Remote(RemoteFile("a.txt", "uid-a", "rev-b", "b", ShaB)));

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.DownloadReplace);
        result.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void BothChanged_NewerLocalWinsByDefault()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var local = Local("a.txt", "local", Now.AddMinutes(-2).ToUnixTimeMilliseconds());
        var remote = RemoteFile("a.txt", "uid-a", "rev-b", "remote", ShaB, claimedMtime: Now.AddMinutes(-5));

        var result = Plan([b], Scan(local), Remote(remote));

        result.Conflicts.Should().ContainSingle()
            .Which.Resolution.Should().Be(PlanResolution.LocalWins);
        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.UploadRevision);
    }

    [Fact]
    public void BothChanged_WithinTolerance_KeepsBoth()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var local = Local("a.txt", "local", Now.AddMinutes(-1).ToUnixTimeMilliseconds());
        var remote = RemoteFile("a.txt", "uid-a", "rev-b", "remote", ShaB,
            claimedMtime: Now.AddMinutes(-1).AddMilliseconds(500));

        var result = Plan([b], Scan(local), Remote(remote));

        result.Conflicts.Should().ContainSingle()
            .Which.Resolution.Should().Be(PlanResolution.KeepBoth);
        result.Actions.Select(a => a.Kind).Should().Contain(new[]
        {
            PlanActionKind.LocalConflictRename,
            PlanActionKind.DownloadReplace,
            PlanActionKind.KeepBothUpload,
        });
    }

    [Fact]
    public void LocalDeleteWithUnchangedRemote_TrashesRemote()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan([b], Scan(), Remote(RemoteFile("a.txt", "uid-a", "rev-a", "a", ShaA)));

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.RemoteTrash);
    }

    [Fact]
    public void LocalDeleteWithChangedRemote_RestoresRemote()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan([b], Scan(), Remote(RemoteFile("a.txt", "uid-a", "rev-b", "b", ShaB)));

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.DownloadRestore);
        result.Conflicts.Should().ContainSingle()
            .Which.Resolution.Should().Be(PlanResolution.RemoteWins);
    }

    [Fact]
    public void RemoteDeleteWithUnchangedLocal_TrashesLocal()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan([b], Scan(Local("a.txt", "a", 1000)), Remote());

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.LocalTrash);
    }

    [Fact]
    public void RemoteDeleteWithChangedLocal_Reuploads()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan([b], Scan(Local("a.txt", "changed", 2000)), Remote());

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.UploadCreate);
        result.Conflicts.Should().ContainSingle()
            .Which.Resolution.Should().Be(PlanResolution.LocalWins);
    }

    [Fact]
    public void BothDeleted_DropsBaseline()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan([b], Scan(), Remote());

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.DropBase);
    }

    [Fact]
    public void KindChange_IsABlockingConflict()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan([b], Scan(LocalDirectory("a.txt")), Remote(RemoteFile("a.txt", "uid-a", "rev-a", "a", ShaA)));

        result.HasBlockingConflicts.Should().BeTrue();
        result.Conflicts.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanConflictKind.KindChange);
        result.Actions.Should().BeEmpty();
    }

    [Fact]
    public void LocalCaseCollision_BlocksEverything()
    {
        var first = Local("Name.txt", "a", 1000);
        var second = Local("name.txt", "b", 1000);
        var local = ScanWith([first, second], collisions:
        [
            new CaseCollision("Name.txt", "name.txt", "name.txt"),
        ]);

        var result = Plan([], local, Remote(RemoteFile("other.txt", "uid", "rev", "x", ShaA)));

        result.HasBlockingConflicts.Should().BeTrue();
        result.Actions.Should().BeEmpty();
        result.Conflicts.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanConflictKind.CaseCollision);
    }

    [Fact]
    public void IgnoredBaseline_IsFrozenInsteadOfDeleted()
    {
        var b = BaselineFile("build/a.txt", "a", ShaA, "uid-a", "rev-a");
        var local = ScanWith(ignored: ["build"]);
        var remote = Remote(RemoteFile("build/a.txt", "uid-a", "rev-a", "a", ShaA));

        var result = Plan([b], local, remote);

        result.Actions.Should().BeEmpty();
        result.Skipped.Should().ContainSingle()
            .Which.Reason.Should().Be(PlanSkipReason.Ignored);
    }

    [Fact]
    public void FrozenBaseline_IsNeverResurrectedOrDeleted()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a") with { Frozen = true };
        var result = Plan([b], Scan(Local("a.txt", "changed", 2000)), Remote(RemoteFile("a.txt", "uid-a", "rev-a", "a", ShaA)));

        result.Actions.Should().BeEmpty();
        result.Skipped.Should().ContainSingle()
            .Which.Reason.Should().Be(PlanSkipReason.Frozen);
    }

    [Fact]
    public void IncompleteLocalScan_DoesNotInferDeletion()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var local = ScanWith(issues: [new ScanIssue("a.txt", "access denied")]);
        var result = Plan([b], local, Remote(RemoteFile("a.txt", "uid-a", "rev-a", "a", ShaA)));

        result.Actions.Should().BeEmpty();
        result.Skipped.Should().ContainSingle()
            .Which.Reason.Should().Be(PlanSkipReason.IncompleteLocalScan);
    }

    [Fact]
    public void RemoteOnlyIgnoredPath_IsNotDownloaded()
    {
        using var temp = new TempDirectory();
        var ignorePath = temp.Write(".pdignore", "ignored.txt\n");
        var rules = IgnoreRules.Load(ignorePath, Array.Empty<string>(), IgnoreCasePolicy.Sensitive);

        var result = Plan(
            [],
            ScanWith(rules: rules),
            Remote(RemoteFile("ignored.txt", "uid-a", "rev-a", "a", ShaA)));

        result.Actions.Should().BeEmpty();
        result.Skipped.Should().ContainSingle()
            .Which.Reason.Should().Be(PlanSkipReason.Ignored);
    }

    [Fact]
    public void IncompleteRemoteScan_DoesNotInferDeletion()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var remote = RemoteWith(skipped: [new RemoteSkip(null, "undecryptable")]);
        var result = Plan([b], Scan(Local("a.txt", "a", 1000)), remote);

        result.Actions.Should().BeEmpty();
        result.Skipped.Should().ContainSingle()
            .Which.Reason.Should().Be(PlanSkipReason.IncompleteRemoteScan);
    }

    [Fact]
    public void LocalDirectory_CreatesRemoteDirectoryBeforeUploadingItsFile()
    {
        var result = Plan(
            [],
            Scan(LocalDirectory("docs"), Local("docs/a.txt", "a", 1000)),
            Remote());

        result.Actions.Select(a => a.Kind).Should().Equal(
            PlanActionKind.CreateRemoteDirectory,
            PlanActionKind.UploadCreate);
    }

    [Fact]
    public void RemoteDirectory_CreatesLocalDirectoryBeforeDownloadingItsFile()
    {
        var result = Plan(
            [],
            Scan(),
            Remote(RemoteDirectory("docs"), RemoteFile("docs/a.txt", "uid-a", "rev-a", "a", ShaA)));

        result.Actions.Select(a => a.Kind).Should().Equal(
            PlanActionKind.CreateLocalDirectory,
            PlanActionKind.DownloadCreate);
    }

    [Fact]
    public void KeepBoth_OrdersRenameThenDownloadThenUpload()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan(
            [b],
            Scan(Local("a.txt", "local", Now.AddMinutes(-1).ToUnixTimeMilliseconds())),
            Remote(RemoteFile("a.txt", "uid-a", "rev-b", "remote", ShaB,
                claimedMtime: Now.AddMinutes(-1).AddMilliseconds(100))));

        var actions = result.Actions;
        actions.Should().HaveCount(3);
        actions[0].Kind.Should().Be(PlanActionKind.LocalConflictRename);
        actions[1].Kind.Should().Be(PlanActionKind.DownloadReplace);
        actions[2].Kind.Should().Be(PlanActionKind.KeepBothUpload);
        actions[0].SecondaryPath.Should().Be(actions[2].Path);
        actions[2].SecondaryPath.Should().Be("a.txt");
    }

    [Fact]
    public void ConflictPolicyLocalWins_Uploads()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan(
            [b],
            Scan(Local("a.txt", "local", 2000)),
            Remote(RemoteFile("a.txt", "uid-a", "rev-b", "remote", ShaB)),
            options => options with { Conflict = ConflictPolicy.LocalWins });

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.UploadRevision);
        result.Conflicts.Should().ContainSingle()
            .Which.Resolution.Should().Be(PlanResolution.LocalWins);
    }

    [Fact]
    public void ConflictPolicyRemoteWins_Downloads()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan(
            [b],
            Scan(Local("a.txt", "local", 2000)),
            Remote(RemoteFile("a.txt", "uid-a", "rev-b", "remote", ShaB)),
            options => options with { Conflict = ConflictPolicy.RemoteWins });

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.DownloadReplace);
        result.Conflicts.Should().ContainSingle()
            .Which.Resolution.Should().Be(PlanResolution.RemoteWins);
    }

    [Fact]
    public void LocalRename_MovesTheRemoteNodeInsteadOfTrashingAndUploading()
    {
        var b = BaselineFile("old.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan(
            [b],
            Scan(Local("new.txt", "a", 1000)),
            Remote(RemoteFile("old.txt", "uid-a", "rev-a", "a", ShaA)));

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.RemoteMove);
        result.Actions[0].Path.Should().Be("old.txt");
        result.Actions[0].SecondaryPath.Should().Be("new.txt");
    }

    [Fact]
    public void LocalRename_CanBeDisabled()
    {
        var b = BaselineFile("old.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan(
            [b],
            Scan(Local("new.txt", "a", 1000)),
            Remote(RemoteFile("old.txt", "uid-a", "rev-a", "a", ShaA)),
            options => options with { DetectRenames = false });

        result.Actions.Select(a => a.Kind).Should().Contain(new[]
        {
            PlanActionKind.RemoteTrash,
            PlanActionKind.UploadCreate,
        });
        result.Actions.Should().NotContain(a => a.Kind == PlanActionKind.RemoteMove);
    }

    [Fact]
    public void RemoteRename_MovesTheLocalFileInsteadOfTrashingAndDownloading()
    {
        var b = BaselineFile("old.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan(
            [b],
            Scan(Local("old.txt", "a", 1000)),
            Remote(RemoteFile("new.txt", "uid-a", "rev-a", "a", ShaA)));

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.LocalMove);
        result.Actions[0].Path.Should().Be("old.txt");
        result.Actions[0].SecondaryPath.Should().Be("new.txt");
    }

    [Fact]
    public void RemoteRenameWithChangedLocal_IsABlockingConflict()
    {
        var b = BaselineFile("old.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan(
            [b],
            Scan(Local("old.txt", "changed", 2000)),
            Remote(RemoteFile("new.txt", "uid-a", "rev-a", "a", ShaA)));

        result.HasBlockingConflicts.Should().BeTrue();
        result.Actions.Should().BeEmpty();
        result.Conflicts.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanConflictKind.RenameConflict);
    }

    [Fact]
    public void AmbiguousLocalRename_FallsBackToTrashAndUpload()
    {
        var b = BaselineFile("old.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan(
            [b],
            Scan(Local("one.txt", "a", 1000), Local("two.txt", "a", 1000)),
            Remote(RemoteFile("old.txt", "uid-a", "rev-a", "a", ShaA)));

        result.Actions.Should().NotContain(a =>
            a.Kind == PlanActionKind.RemoteMove || a.Kind == PlanActionKind.LocalMove);
        result.Actions.Should().Contain(a => a.Kind == PlanActionKind.RemoteTrash);
        result.Actions.Count(a => a.Kind == PlanActionKind.UploadCreate).Should().Be(2);
    }

    [Fact]
    public void OnLocalDeleteKeep_DoesNotTrashRemote()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan(
            [b],
            Scan(),
            Remote(RemoteFile("a.txt", "uid-a", "rev-a", "a", ShaA)),
            options => options with { OnLocalDelete = LocalDeletePolicy.Keep });

        result.Actions.Should().BeEmpty();
        result.Skipped.Should().ContainSingle()
            .Which.Reason.Should().Be(PlanSkipReason.PolicyKeep);
    }

    [Fact]
    public void OnRemoteDeleteKeep_DoesNotTrashLocal()
    {
        var b = BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a");
        var result = Plan(
            [b],
            Scan(Local("a.txt", "a", 1000)),
            Remote(),
            options => options with { OnRemoteDelete = RemoteDeletePolicy.Keep });

        result.Actions.Should().BeEmpty();
        result.Skipped.Should().ContainSingle()
            .Which.Reason.Should().Be(PlanSkipReason.PolicyKeep);
    }

    private static PlanResult Plan(
        IReadOnlyList<EntryRecord> baseEntries,
        LocalScan local,
        RemoteSnapshot remote,
        Func<ResolvedOptions, ResolvedOptions>? configure = null)
    {
        var options = Options();
        if (configure is not null)
            options = configure(options);

        var sync = new SyncConfig(
            "test",
            Path.Combine(Path.GetTempPath(), "local"),
            "/my-files/root",
            Enabled: true,
            IgnoreFile: ".pdignore",
            Exclude: Array.Empty<string>(),
            options);

        return Planner.Plan(baseEntries, local, remote, sync, new PlanContext(Now));
    }

    private static ResolvedOptions Options() => new(
        SyncDirection.TwoWay,
        ConflictPolicy.NewerWins,
        LocalDeletePolicy.RemoteTrash,
        RemoteDeletePolicy.LocalTrash,
        InitialSyncPolicy.Adopt,
        SettleSeconds: 3,
        MtimeToleranceMs: 2000,
        DetectRenames: true,
        IgnoreCase: IgnoreCasePolicy.Sensitive,
        PruneEmptyRemoteDirs: true,
        Fast: false,
        FastFullEveryRuns: 20,
        new SafetyOptions(0, 100),
        new VerifyOptions(VerifyMode.Hash, 10, 0),
        new LocalTrashOptions(null));

    private static LocalScan Scan(
        params LocalEntry[] entries) => ScanWith(entries);

    private static LocalScan ScanWith(
        LocalEntry[]? entries = null,
        IReadOnlyList<CaseCollision>? collisions = null,
        IReadOnlyList<ScanIssue>? issues = null,
        IReadOnlyList<string>? ignored = null,
        IgnoreRules? rules = null)
    {
        entries ??= Array.Empty<LocalEntry>();
        return new LocalScan(
            root: Path.Combine(Path.GetTempPath(), "local"),
            entries,
            Array.Empty<ScanSkip>(),
            issues ?? Array.Empty<ScanIssue>(),
            collisions ?? Array.Empty<CaseCollision>(),
            ignored ?? Array.Empty<string>(),
            Now,
            TimeSpan.Zero,
            rules);
    }

    private static LocalEntry Local(string path, string content, long mtimeMs) => new(
        path,
        Path.Combine(Path.GetTempPath(), "local", path.Replace('/', Path.DirectorySeparatorChar)),
        PathKey.Fold(path),
        LocalEntryKind.File,
        content.Length,
        DateTimeOffset.FromUnixTimeMilliseconds(mtimeMs),
        content == "a" ? ShaA : content == "b" ? ShaB : ShaOf(content));

    private static LocalEntry LocalDirectory(string path) => new(
        path,
        Path.Combine(Path.GetTempPath(), "local", path.Replace('/', Path.DirectorySeparatorChar)),
        PathKey.Fold(path),
        LocalEntryKind.Directory,
        0,
        DateTimeOffset.FromUnixTimeMilliseconds(1000),
        null);

    private static EntryRecord BaselineFile(
        string path,
        string content,
        string sha,
        string uid,
        string rev) => new(
        PathKey.Fold(path),
        path,
        EntryKind.File,
        content.Length,
        1000,
        sha,
        uid,
        rev,
        content.Length,
        1000,
        sha,
        LocalPresent: true,
        RemotePresent: true,
        Frozen: false,
        LastSyncedAt: Now);

    private static RemoteSnapshot Remote(
        params RemoteEntry[] entries) => RemoteWith(entries);

    private static RemoteSnapshot RemoteWith(
        RemoteEntry[]? entries = null,
        IReadOnlyList<RemoteSkip>? skipped = null,
        IReadOnlyList<RemoteCaseCollision>? collisions = null) => new(
        "/my-files/root",
        entries ?? Array.Empty<RemoteEntry>(),
        skipped ?? Array.Empty<RemoteSkip>(),
        collisions ?? Array.Empty<RemoteCaseCollision>(),
        Now);

    private static RemoteEntry RemoteFile(
        string path,
        string uid,
        string rev,
        string content,
        string sha,
        DateTimeOffset? claimedMtime = null) => new(
        path,
        PathKey.Fold(path),
        RemoteEntryKind.File,
        uid,
        rev,
        content.Length,
        Now,
        claimedMtime ?? Now.AddMinutes(-10),
        sha,
        Sha1Verified: false);

    private static RemoteEntry RemoteDirectory(string path) => new(
        path,
        PathKey.Fold(path),
        RemoteEntryKind.Folder,
        "uid-" + path,
        null,
        0,
        Now,
        null,
        null,
        false);

    private static string ShaOf(string content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
}
