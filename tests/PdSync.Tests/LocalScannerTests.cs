using FluentAssertions;
using PdSync.Config;
using PdSync.Scan;

namespace PdSync.Tests;

public sealed class LocalScannerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private SyncConfig Sync(string? extraSync = null, string? local = null)
    {
        var config = ConfigTestHelper.Load(
            _temp,
            ConfigTestHelper.MinimalYaml(_temp, local: local, extraSync: extraSync));
        return config.Syncs[0];
    }

    private string Local(params string[] parts) =>
        _temp.Combine(new[] { "local" }.Concat(parts).ToArray());

    private static LocalScan Scan(SyncConfig sync, ScanOptions? options = null) =>
        LocalScanner.Scan(sync, options ?? new ScanOptions(SettleSeconds: 0));

    [Fact]
    public void Scan_ReturnsFilesAndDirectories_InStableOrder()
    {
        _temp.Write("local/abc.txt", "abc");
        _temp.Write("local/sub/b.txt", "b");
        Directory.CreateDirectory(Local("empty"));

        var scan = Scan(Sync());

        scan.Root.Should().Be(Local());
        scan.Entries.Select(e => e.Path).Should().Equal(
            "abc.txt",
            "empty",
            "sub",
            "sub/b.txt");

        var file = scan.Entries.Single(e => e.Path == "abc.txt");
        file.Kind.Should().Be(LocalEntryKind.File);
        file.Size.Should().Be(3);
        file.Sha1.Should().Be("a9993e364706816aba3e25717850c26c9cd0d89d");
        file.PathKey.Should().Be("abc.txt");

        var directory = scan.Entries.Single(e => e.Path == "sub");
        directory.Kind.Should().Be(LocalEntryKind.Directory);
        directory.Size.Should().Be(0);
        directory.Sha1.Should().BeNull();
        directory.IsDirectory.Should().BeTrue();

        scan.Issues.Should().BeEmpty();
        scan.Skipped.Should().BeEmpty();
        scan.Collisions.Should().BeEmpty();
        scan.IsComplete.Should().BeTrue();
        scan.IgnoredCount.Should().Be(0);
    }

    [Fact]
    public void Scan_EmptyRoot_ProducesAnEmptyCompleteSnapshot()
    {
        var scan = Scan(Sync());

        scan.Entries.Should().BeEmpty();
        scan.ByKey.Should().BeEmpty();
        scan.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void Scan_UsesNfcDisplayPathsAndFoldedKeys()
    {
        _temp.Write("local/Cafe\u0301.txt", "x");

        var scan = Scan(Sync());

        scan.Entries.Should().ContainSingle();
        scan.Entries[0].Path.Should().Be("Caf\u00E9.txt");
        scan.Entries[0].PathKey.Should().Be("caf\u00E9.txt");
        scan.ByKey.Should().ContainKey("caf\u00E9.txt");
    }

    [Fact]
    public void Scan_UsesPdignoreAndPrunesIgnoredDirectories()
    {
        _temp.Write("local/.pdignore", "*.tmp\nlogs/\n!keep.tmp\n");
        _temp.Write("local/a.tmp", "ignored");
        _temp.Write("local/keep.tmp", "kept");
        _temp.Write("local/a.txt", "kept");
        _temp.Write("local/logs/secret.txt", "not scanned");
        _temp.Write("local/logs/nested/a.txt", "not scanned");

        var scan = Scan(Sync());

        scan.Entries.Select(e => e.Path).Should().Equal(".pdignore", "a.txt", "keep.tmp");
        scan.ByKey.Should().NotContainKey("logs");
        scan.ByKey.Should().NotContainKey("logs/secret.txt");
        scan.IgnoredCount.Should().Be(2);
        scan.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Scan_TreatsBackslashAsAFilenameCharacter_OnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        _temp.Write("local/a\\b.txt", "x");
        _temp.Write("local/.pdignore", "a/b*\n");

        var scan = Scan(Sync());

        scan.ByKey.Keys.Should().Contain("a\\b.txt");
        scan.Entries.Should().Contain(e => e.Path == "a\\b.txt");
    }

    [Fact]
    public void Scan_InlineExcludes_ApplyAfterTheIgnoreFile()
    {
        _temp.Write("local/a.tmp", "ignored");
        _temp.Write("local/keep.tmp", "kept");
        _temp.Write("local/a.log", "ignored");

        var sync = Sync("""
                exclude:
                  - "*.tmp"
                  - "*.log"
                  - "!keep.tmp"
            """);

        var scan = Scan(sync);

        scan.Entries.Select(e => e.Path).Should().Equal("keep.tmp");
    }

    [Fact]
    public void Scan_SyncsTheIgnoreFileUnlessItIgnoresItself()
    {
        _temp.Write("local/.pdignore", "*.tmp\n");
        _temp.Write("local/a.tmp", "ignored");

        var scan = Scan(Sync());

        scan.Entries.Select(e => e.Path).Should().Equal(".pdignore");
    }

    [Fact]
    public void Scan_CanIgnoreTheIgnoreFileItself()
    {
        _temp.Write("local/.pdignore", ".pdignore\n");

        var scan = Scan(Sync());

        scan.Entries.Should().BeEmpty();
        scan.IgnoredCount.Should().Be(1);
    }

    [Fact]
    public void Scan_RecentlyModifiedFile_IsSkippedUntilTheNextRun()
    {
        var path = _temp.Write("local/fresh.txt", "x");
        var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        var sync = Sync();

        var skippedScan = Scan(sync, new ScanOptions(
            SettleSeconds: 60,
            Now: mtime.AddSeconds(1)));

        skippedScan.ByKey.Should().NotContainKey("fresh.txt");
        skippedScan.Skipped.Should().ContainSingle(s =>
            s.Path == "fresh.txt" && s.Reason == ScanSkipReason.Unsettled);
        skippedScan.IsComplete.Should().BeTrue();

        var includedScan = Scan(sync, new ScanOptions(
            SettleSeconds: 60,
            Now: mtime.AddSeconds(61)));

        includedScan.ByKey.Should().ContainKey("fresh.txt");
        includedScan.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Scan_SettleZero_IncludesFreshlyWrittenFiles()
    {
        _temp.Write("local/fresh.txt", "x");

        var scan = Scan(Sync());

        scan.ByKey.Should().ContainKey("fresh.txt");
    }

    [Fact]
    public void Scan_SkipsSymlinksWithoutFollowingThem()
    {
        var target = _temp.Write("local/target.txt", "target");
        var link = Local("link.txt");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var scan = Scan(Sync());

        scan.ByKey.Should().NotContainKey("link.txt");
        scan.Skipped.Should().ContainSingle(s =>
            s.Path == "link.txt" && s.Reason == ScanSkipReason.Symlink);
        scan.Entries.Should().Contain(e => e.Path == "target.txt");
    }

    [Fact]
    public void Scan_DetectsCaseCollisionsInTheSameDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        _temp.Write("local/Name.txt", "first");
        _temp.Write("local/name.txt", "second");

        // Case-insensitive filesystems (typically macOS) silently merge these.
        if (Directory.GetFiles(Local()).Length != 2)
            return;

        var scan = Scan(Sync());

        scan.Collisions.Should().ContainSingle();
        scan.Collisions[0].FirstPath.Should().Be("Name.txt");
        scan.Collisions[0].SecondPath.Should().Be("name.txt");
        scan.Collisions[0].PathKey.Should().Be("name.txt");
        scan.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void Scan_MissingRoot_ThrowsRatherThanReturningAnEmptySnapshot()
    {
        var sync = Sync();
        Directory.Delete(Local(), recursive: true);

        var act = () => Scan(sync);

        act.Should().Throw<ScanException>().WithMessage("*does not exist*");
    }

    [Fact]
    public void Scan_RootSymlink_IsRefused()
    {
        var target = _temp.Combine("real-root");
        Directory.CreateDirectory(target);
        var link = _temp.Combine("linked-root");

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var sync = Sync(local: link);
        var act = () => Scan(sync);

        act.Should().Throw<ScanException>().WithMessage("*symlink*");
    }

    [Fact]
    public void Scan_CanSkipHashing()
    {
        _temp.Write("local/abc.txt", "abc");

        var scan = Scan(Sync(), new ScanOptions(SettleSeconds: 0, ComputeHashes: false));

        scan.Entries.Should().ContainSingle();
        scan.Entries[0].Sha1.Should().BeNull();
    }

    [Fact]
    public void Scan_ReusesHashesWhenSizeAndMtimeAreUnchanged()
    {
        _temp.Write("local/one.txt", "one");
        _temp.Write("local/two.txt", "two");

        var cache = new CountingHashCache();
        var sync = Sync();

        var first = Scan(sync, new ScanOptions(SettleSeconds: 0, HashCache: cache));
        cache.Sets.Should().Be(2);
        cache.Hits.Should().Be(0);

        var second = Scan(sync, new ScanOptions(SettleSeconds: 0, HashCache: cache));
        cache.Sets.Should().Be(2);
        cache.Hits.Should().Be(2);

        second.ByKey["one.txt"].Sha1.Should().Be(first.ByKey["one.txt"].Sha1);
    }

    [Fact]
    public void Scan_RehashesWhenSizeChanges()
    {
        _temp.Write("local/one.txt", "one");
        var cache = new CountingHashCache();
        var sync = Sync();

        var first = Scan(sync, new ScanOptions(SettleSeconds: 0, HashCache: cache));
        _temp.Write("local/one.txt", "much longer content");
        var second = Scan(sync, new ScanOptions(SettleSeconds: 0, HashCache: cache));

        cache.Sets.Should().Be(2);
        second.ByKey["one.txt"].Sha1.Should().NotBe(first.ByKey["one.txt"].Sha1);
        second.ByKey["one.txt"].Size.Should().Be(19);
    }

    private sealed class CountingHashCache : IHashCache
    {
        private readonly ContentHashCache _inner = new();

        public int Hits { get; private set; }
        public int Sets { get; private set; }

        public bool TryGet(string fullPath, long size, long mtimeUtcTicks, out string sha1)
        {
            var hit = _inner.TryGet(fullPath, size, mtimeUtcTicks, out sha1);
            if (hit)
                Hits++;
            return hit;
        }

        public void Set(string fullPath, long size, long mtimeUtcTicks, string sha1)
        {
            Sets++;
            _inner.Set(fullPath, size, mtimeUtcTicks, sha1);
        }
    }
}
