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

    private static (EntryRecord Baseline, LocalEntry Local, RemoteEntry Remote) BothChanged() =>
        (BaselineFile("a.txt", "a", ShaA, "uid-a", "rev-a"),
         Local("a.txt", "local", Now.AddMinutes(-2).ToUnixTimeMilliseconds()),
         RemoteFile("a.txt", "uid-a", "rev-b", "remote", ShaB, claimedMtime: Now.AddMinutes(-5)));

    [Fact]
    public void AskWithLocalOverride_UploadsRevision()
    {
        var (b, l, r) = BothChanged();
        var result = Plan([b], Scan(l), Remote(r),
            conflict: ConflictPolicy.Ask, interactive: true,
            overrides: Overrides(PlanResolution.LocalWins));

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.UploadRevision);
        result.Conflicts.Should().ContainSingle()
            .Which.Resolution.Should().Be(PlanResolution.LocalWins);
        result.HasBlockingConflicts.Should().BeFalse();
        result.HasDeferredConflicts.Should().BeFalse();
    }

    [Fact]
    public void AskWithRemoteOverride_DownloadsReplacement()
    {
        var (b, l, r) = BothChanged();
        var result = Plan([b], Scan(l), Remote(r),
            conflict: ConflictPolicy.Ask, interactive: true,
            overrides: Overrides(PlanResolution.RemoteWins));

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.DownloadReplace);
        result.Conflicts.Should().ContainSingle()
            .Which.Resolution.Should().Be(PlanResolution.RemoteWins);
    }

    [Fact]
    public void AskWithKeepBothOverride_EmitsKeepBothActions()
    {
        var (b, l, r) = BothChanged();
        var result = Plan([b], Scan(l), Remote(r),
            conflict: ConflictPolicy.Ask, interactive: true,
            overrides: Overrides(PlanResolution.KeepBoth));

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
    public void AskWithDeferOverride_SkipsWithoutActionAndIsNotBlocking()
    {
        var (b, l, r) = BothChanged();
        var result = Plan([b], Scan(l), Remote(r),
            conflict: ConflictPolicy.Ask, interactive: true,
            overrides: Overrides(PlanResolution.Defer));

        result.Actions.Should().BeEmpty();
        result.Conflicts.Should().BeEmpty();
        result.Skipped.Should().ContainSingle()
            .Which.Reason.Should().Be(PlanSkipReason.Deferred);
        result.HasBlockingConflicts.Should().BeFalse();
        result.HasDeferredConflicts.Should().BeTrue();
    }

    [Fact]
    public void AskWithUnknownOverrideKeyStaysBlocking()
    {
        var (b, l, r) = BothChanged();
        var result = Plan([b], Scan(l), Remote(r),
            conflict: ConflictPolicy.Ask, interactive: true,
            overrides: Overrides(PlanResolution.LocalWins, key: "other.txt"));

        result.Conflicts.Should().ContainSingle()
            .Which.Resolution.Should().Be(PlanResolution.Abort);
        result.HasBlockingConflicts.Should().BeTrue();
    }

    [Fact]
    public void OverridesDoNotAffectNonAskPolicies()
    {
        var (b, l, r) = BothChanged();
        var result = Plan([b], Scan(l), Remote(r),
            conflict: ConflictPolicy.RemoteWins,
            overrides: Overrides(PlanResolution.LocalWins));

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.DownloadReplace);
        result.Conflicts.Should().ContainSingle()
            .Which.Resolution.Should().Be(PlanResolution.RemoteWins);
    }

    [Fact]
    public void AskNonInteractive_KeepsBothButRemainsUnresolved()
    {
        var (b, l, r) = BothChanged();
        var result = Plan([b], Scan(l), Remote(r), conflict: ConflictPolicy.Ask);

        result.Conflicts.Should().ContainSingle()
            .Which.Resolution.Should().Be(PlanResolution.KeepBoth);
        result.HasAskFallback.Should().BeTrue();
        result.HasUnresolvedConflicts.Should().BeTrue();
    }

    private static IReadOnlyDictionary<string, PlanResolution> Overrides(
        PlanResolution resolution, string key = "a.txt") =>
        new Dictionary<string, PlanResolution>(StringComparer.Ordinal)
        {
            [PathKey.Fold(key)] = resolution,
        };

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
    public void KeepBoth_LongAsciiName_TruncatesStemToFitWindowsLimit()
    {
        var stem = new string('x', 240);
        var name = stem + ".txt";
        var b = BaselineFile(name, "a", ShaA, "uid-a", "rev-a");
        var result = Plan(
            [b],
            Scan(Local(name, "local", Now.AddMinutes(-1).ToUnixTimeMilliseconds())),
            Remote(RemoteFile(name, "uid-a", "rev-b", "remote", ShaB,
                claimedMtime: Now.AddMinutes(-1).AddMilliseconds(100))));

        var rename = result.Actions.Single(a => a.Kind == PlanActionKind.LocalConflictRename);
        var conflictName = rename.SecondaryPath!;
        conflictName.Length.Should().BeLessThanOrEqualTo(255);
        conflictName.Should().EndWith(".txt");
        conflictName.Should().Contain("(conflict ");
        conflictName.Should().NotBe(name);
        conflictName.Should().StartWith("xxxx");
        var suffixLength = conflictName.Length - conflictName.IndexOf(" (conflict ", StringComparison.Ordinal);
        (conflictName.Length - suffixLength).Should().Be(255 - suffixLength, "the stem is trimmed to exactly fill the leaf limit");
    }

    [Fact]
    public void KeepBoth_MultibyteName_RespectsUtf8ByteLimit()
    {
        // 120 'é' characters = 240 UTF-8 bytes, so the UTF-8 byte cap binds before
        // the UTF-16 unit cap.
        var stem = new string('é', 120);
        var name = stem + ".txt";
        var b = BaselineFile(name, "a", ShaA, "uid-a", "rev-a");
        var result = Plan(
            [b],
            Scan(Local(name, "local", Now.AddMinutes(-1).ToUnixTimeMilliseconds())),
            Remote(RemoteFile(name, "uid-a", "rev-b", "remote", ShaB,
                claimedMtime: Now.AddMinutes(-1).AddMilliseconds(100))));

        var conflictName = result.Actions.Single(a => a.Kind == PlanActionKind.LocalConflictRename).SecondaryPath!;
        System.Text.Encoding.UTF8.GetByteCount(conflictName).Should().BeLessThanOrEqualTo(255);
        conflictName.Length.Should().BeLessThanOrEqualTo(255);
        conflictName.Should().EndWith(".txt");
        conflictName.Should().Contain("(conflict ");
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
    public void LocalDirectoryRename_MovesTheRemoteSubtreeAsOneAction()
    {
        var baseline = new[]
        {
            BaselineFolder("docs", "uid-docs"),
            BaselineFile("docs/a.txt", "a", ShaA, "uid-a", "rev-a"),
            BaselineFolder("docs/sub", "uid-sub"),
            BaselineFile("docs/sub/b.txt", "b", ShaB, "uid-b", "rev-b"),
        };
        var local = Scan(
            LocalDirectory("renamed"),
            Local("renamed/a.txt", "a", 1000),
            LocalDirectory("renamed/sub"),
            Local("renamed/sub/b.txt", "b", 1000));
        var remote = Remote(
            RemoteDirectory("docs", "uid-docs"),
            RemoteFile("docs/a.txt", "uid-a", "rev-a", "a", ShaA),
            RemoteDirectory("docs/sub", "uid-sub"),
            RemoteFile("docs/sub/b.txt", "uid-b", "rev-b", "b", ShaB));

        var result = Plan(baseline, local, remote);

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.RemoteDirectoryMove);
        result.Actions[0].Path.Should().Be("docs");
        result.Actions[0].SecondaryPath.Should().Be("renamed");
        result.Actions.Should().NotContain(a => a.IsDelete);
        result.Actions.Should().NotContain(a => a.IsUpload || a.IsDownload);
        result.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void LocalDirectoryRename_CaseOnlyIsANoOp()
    {
        var baseline = new[]
        {
            BaselineFolder("docs", "uid-docs"),
            BaselineFile("docs/a.txt", "a", ShaA, "uid-a", "rev-a"),
        };
        var local = Scan(LocalDirectory("Docs"), Local("Docs/a.txt", "a", 1000));
        var remote = Remote(
            RemoteDirectory("docs", "uid-docs"),
            RemoteFile("docs/a.txt", "uid-a", "rev-a", "a", ShaA));

        var result = Plan(baseline, local, remote);

        result.Actions.Should().BeEmpty();
        result.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void LocalDirectoryRename_AmbiguousCandidatesFallBackToTrashAndUpload()
    {
        var baseline = new[]
        {
            BaselineFolder("docs", "uid-docs"),
            BaselineFile("docs/a.txt", "a", ShaA, "uid-a", "rev-a"),
        };
        var local = Scan(
            LocalDirectory("one"), Local("one/a.txt", "a", 1000),
            LocalDirectory("two"), Local("two/a.txt", "a", 1000));
        var remote = Remote(
            RemoteDirectory("docs", "uid-docs"),
            RemoteFile("docs/a.txt", "uid-a", "rev-a", "a", ShaA));

        var result = Plan(baseline, local, remote);

        result.Actions.Should().NotContain(a =>
            a.Kind == PlanActionKind.RemoteDirectoryMove ||
            a.Kind == PlanActionKind.LocalDirectoryMove);
        result.Actions.Should().Contain(a => a.Kind == PlanActionKind.RemoteTrash);
        result.Actions.Count(a => a.Kind == PlanActionKind.UploadCreate).Should().Be(2);
    }

    [Fact]
    public void LocalDirectoryRename_ChildContentChangedFallsBack()
    {
        var baseline = new[]
        {
            BaselineFolder("docs", "uid-docs"),
            BaselineFile("docs/a.txt", "a", ShaA, "uid-a", "rev-a"),
        };
        var local = Scan(LocalDirectory("renamed"), Local("renamed/a.txt", "changed", 2000));
        var remote = Remote(
            RemoteDirectory("docs", "uid-docs"),
            RemoteFile("docs/a.txt", "uid-a", "rev-a", "a", ShaA));

        var result = Plan(baseline, local, remote);

        result.Actions.Should().NotContain(a =>
            a.Kind == PlanActionKind.RemoteDirectoryMove ||
            a.Kind == PlanActionKind.LocalDirectoryMove);
        result.Actions.Should().Contain(a => a.Kind == PlanActionKind.RemoteTrash);
    }

    [Fact]
    public void LocalDirectoryRename_IgnoredChildBlocksTheMove()
    {
        var baseline = new[]
        {
            BaselineFolder("docs", "uid-docs"),
            BaselineFile("docs/a.txt", "a", ShaA, "uid-a", "rev-a"),
        };
        var local = ScanWith(
            entries: [LocalDirectory("renamed"), Local("renamed/a.txt", "a", 1000)],
            ignored: ["renamed/secret.txt"]);
        var remote = Remote(
            RemoteDirectory("docs", "uid-docs"),
            RemoteFile("docs/a.txt", "uid-a", "rev-a", "a", ShaA));

        var result = Plan(baseline, local, remote);

        result.Actions.Should().NotContain(a => a.Kind == PlanActionKind.RemoteDirectoryMove);
        result.Actions.Should().Contain(a => a.Kind == PlanActionKind.RemoteTrash);
    }

    [Fact]
    public void RemoteDirectoryRename_MovesTheLocalSubtreeAsOneAction()
    {
        var baseline = new[]
        {
            BaselineFolder("docs", "uid-docs"),
            BaselineFile("docs/a.txt", "a", ShaA, "uid-a", "rev-a"),
            BaselineFolder("docs/sub", "uid-sub"),
            BaselineFile("docs/sub/b.txt", "b", ShaB, "uid-b", "rev-b"),
        };
        var local = Scan(
            LocalDirectory("docs"),
            Local("docs/a.txt", "a", 1000),
            LocalDirectory("docs/sub"),
            Local("docs/sub/b.txt", "b", 1000));
        var remote = Remote(
            RemoteDirectory("renamed", "uid-docs"),
            RemoteFile("renamed/a.txt", "uid-a", "rev-a", "a", ShaA),
            RemoteDirectory("renamed/sub", "uid-sub"),
            RemoteFile("renamed/sub/b.txt", "uid-b", "rev-b", "b", ShaB));

        var result = Plan(baseline, local, remote);

        result.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanActionKind.LocalDirectoryMove);
        result.Actions[0].Path.Should().Be("docs");
        result.Actions[0].SecondaryPath.Should().Be("renamed");
        result.Actions.Should().NotContain(a => a.IsDelete);
        result.Actions.Should().NotContain(a => a.IsUpload || a.IsDownload);
    }

    [Fact]
    public void RemoteDirectoryRename_ChangedLocalSubtreeIsABlockingConflict()
    {
        var baseline = new[]
        {
            BaselineFolder("docs", "uid-docs"),
            BaselineFile("docs/a.txt", "a", ShaA, "uid-a", "rev-a"),
        };
        var local = Scan(LocalDirectory("docs"), Local("docs/a.txt", "changed", 2000));
        var remote = Remote(
            RemoteDirectory("renamed", "uid-docs"),
            RemoteFile("renamed/a.txt", "uid-a", "rev-a", "a", ShaA));

        var result = Plan(baseline, local, remote);

        result.HasBlockingConflicts.Should().BeTrue();
        result.Actions.Should().BeEmpty();
        result.Conflicts.Should().ContainSingle()
            .Which.Kind.Should().Be(PlanConflictKind.RenameConflict);
    }

    [Fact]
    public void LocalDirectoryRename_IncompleteScanDisablesDetection()
    {
        var baseline = new[]
        {
            BaselineFolder("docs", "uid-docs"),
            BaselineFile("docs/a.txt", "a", ShaA, "uid-a", "rev-a"),
        };
        var local = ScanWith(
            entries: [LocalDirectory("renamed"), Local("renamed/a.txt", "a", 1000)],
            issues: [new ScanIssue("renamed", "access denied")]);
        var remote = Remote(
            RemoteDirectory("docs", "uid-docs"),
            RemoteFile("docs/a.txt", "uid-a", "rev-a", "a", ShaA));

        var result = Plan(baseline, local, remote);

        result.Actions.Should().NotContain(a =>
            a.Kind == PlanActionKind.RemoteDirectoryMove ||
            a.Kind == PlanActionKind.LocalDirectoryMove);
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

    [Fact]
    public void CleanupStagedFlag_OnlyAffectsAdoptedLeftover_WithDistinctReason()
    {
        var b = BaselineFile("pdsync-upload-0123456789ab", "a", ShaA, "uid-old", "rev-old");
        var remote = RemoteFile("pdsync-upload-0123456789ab", "uid-new", "rev-new", "leftover", ShaOf("leftover"));

        var automatic = Plan([b], Scan(), Remote(remote));
        automatic.Actions.Should().NotContain(action =>
            action.Kind == PlanActionKind.CleanupStagedUpload);

        var optIn = Plan([b], Scan(), Remote(remote), cleanupStaged: true);
        var cleanup = optIn.Actions.Should().ContainSingle(action =>
            action.Kind == PlanActionKind.CleanupStagedUpload).Which;
        cleanup.Path.Should().Be("pdsync-upload-0123456789ab");
        cleanup.Reason.Should().Contain("opt-in cleanup");
        cleanup.Reason.Should().NotContain("automatic cleanup");
    }

    [Fact]
    public void CleanupStagedFlag_DefersWhenLocalScanIsIncomplete()
    {
        var path = "pdsync-upload-0123456789ab";
        var b = BaselineFile(path, "a", ShaA, "uid-old", "rev-old");
        var remote = RemoteFile(path, "uid-new", "rev-new", "leftover", ShaOf("leftover"));

        var result = Plan(
            [b],
            ScanWith(issues: [new ScanIssue(path, "access denied")]),
            Remote(remote),
            cleanupStaged: true);

        result.Actions.Should().NotContain(action =>
            action.Kind == PlanActionKind.CleanupStagedUpload);
        result.Skipped.Should().ContainSingle(skip =>
            skip.Reason == PlanSkipReason.IncompleteLocalScan && skip.PathKey == PathKey.Fold(path));
    }

    [Fact]
    public void AutomaticCleanup_DefersWhenLocalScanIsIncomplete()
    {
        var path = "pdsync-upload-0123456789ab";
        var remote = RemoteFile(path, "uid-new", "rev-new", "leftover", ShaOf("leftover"));

        var result = Plan(
            baseEntries: [],
            local: ScanWith(issues: [new ScanIssue(path, "access denied")]),
            remote: Remote(remote));

        result.Actions.Should().NotContain(action =>
            action.Kind == PlanActionKind.CleanupStagedUpload && action.PathKey == PathKey.Fold(path));
        result.Actions.Should().NotContain(action =>
            action.PathKey == PathKey.Fold(path) &&
            (action.Kind == PlanActionKind.DownloadCreate ||
             action.Kind == PlanActionKind.DownloadReplace));
        result.Skipped.Should().ContainSingle(skip =>
            skip.Reason == PlanSkipReason.IncompleteLocalScan && skip.PathKey == PathKey.Fold(path));
    }

    private static PlanResult Plan(
        IReadOnlyList<EntryRecord> baseEntries,
        LocalScan local,
        RemoteSnapshot remote,
        Func<ResolvedOptions, ResolvedOptions>? configure = null,
        bool cleanupStaged = false,
        ConflictPolicy? conflict = null,
        bool interactive = false,
        IReadOnlyDictionary<string, PlanResolution>? overrides = null)
    {
        var options = Options();
        if (configure is not null)
            options = configure(options);
        if (conflict is not null)
            options = options with { Conflict = conflict.Value };

        var sync = new SyncConfig(
            "test",
            Path.Combine(Path.GetTempPath(), "local"),
            "/my-files/root",
            Enabled: true,
            IgnoreFile: ".pdignore",
            Exclude: Array.Empty<string>(),
            options);

        return Planner.Plan(baseEntries, local, remote, sync,
            new PlanContext(Now, interactive, cleanupStaged, overrides));
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

    private static EntryRecord BaselineFolder(string path, string uid) => new(
        PathKey.Fold(path),
        path,
        EntryKind.Folder,
        Size: null,
        MtimeMs: null,
        Sha1: null,
        RemoteUid: uid,
        RemoteRevUid: null,
        RemoteSize: 0,
        RemoteMtimeMs: 1000,
        RemoteSha1: null,
        LocalPresent: true,
        RemotePresent: true,
        Frozen: false,
        LastSyncedAt: Now);

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

    private static RemoteEntry RemoteDirectory(string path, string? uid = null) => new(
        path,
        PathKey.Fold(path),
        RemoteEntryKind.Folder,
        uid ?? "uid-" + path,
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
