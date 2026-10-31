using System.Diagnostics;
using System.Security;
using PdSync.Config;

namespace PdSync.Scan;

public enum LocalEntryKind
{
    File,
    Directory,
    Symlink,
    Other,
}

/// <summary>
/// One local filesystem entry. <see cref="Path"/> is the display path (relative,
/// POSIX separators, NFC); <see cref="PathKey"/> is the case-folded identity.
/// </summary>
public sealed record LocalEntry(
    string Path,
    string FullPath,
    string PathKey,
    LocalEntryKind Kind,
    long Size,
    DateTimeOffset Mtime,
    string? Sha1)
{
    public long MtimeUtcTicks => Mtime.UtcTicks;

    public bool IsDirectory => Kind == LocalEntryKind.Directory;
}

public enum ScanSkipReason
{
    Symlink,
    SpecialFile,
    Unsettled,
    ChangedDuringScan,
    Unreadable,
}

public sealed record ScanSkip(string Path, ScanSkipReason Reason, string? Detail = null);

public sealed record ScanIssue(string Path, string Message);

public sealed record CaseCollision(string FirstPath, string SecondPath, string PathKey);

public sealed class LocalScan
{
    public LocalScan(
        string root,
        IReadOnlyList<LocalEntry> entries,
        IReadOnlyList<ScanSkip> skipped,
        IReadOnlyList<ScanIssue> issues,
        IReadOnlyList<CaseCollision> collisions,
        int ignoredCount,
        DateTimeOffset scannedAt,
        TimeSpan duration)
    {
        Root = root;
        Entries = entries;
        Skipped = skipped;
        Issues = issues;
        Collisions = collisions;
        IgnoredCount = ignoredCount;
        ScannedAt = scannedAt;
        Duration = duration;

        var byKey = new Dictionary<string, LocalEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
            byKey[entry.PathKey] = entry;
        ByKey = byKey;

        var skippedByKey = new Dictionary<string, ScanSkip>(StringComparer.Ordinal);
        foreach (var skip in skipped)
            skippedByKey[PathKey.Fold(skip.Path)] = skip;
        SkippedByKey = skippedByKey;
    }

    public string Root { get; }

    /// <summary>Entries sorted by display path (ordinal). Directories are included.</summary>
    public IReadOnlyList<LocalEntry> Entries { get; }

    public IReadOnlyDictionary<string, LocalEntry> ByKey { get; }

    /// <summary>Paths deliberately not represented in <see cref="Entries"/> (symlinks, unsettled files, …).</summary>
    public IReadOnlyList<ScanSkip> Skipped { get; }

    /// <summary>Enumeration/hash failures. A non-empty list makes deletion planning unsafe.</summary>
    public IReadOnlyList<ScanIssue> Issues { get; }

    public IReadOnlyList<CaseCollision> Collisions { get; }

    /// <summary>Lookup for paths deliberately absent from <see cref="ByKey"/>.</summary>
    public IReadOnlyDictionary<string, ScanSkip> SkippedByKey { get; }

    public int IgnoredCount { get; }

    public DateTimeOffset ScannedAt { get; }

    public TimeSpan Duration { get; }

    /// <summary>
    /// False when enumeration/hashing failed or a case collision was found. This
    /// covers failures only; deliberate skips are in <see cref="SkippedByKey"/> and
    /// must also be excluded from deletion inference by the planner.
    /// </summary>
    public bool IsComplete => Issues.Count == 0 && Collisions.Count == 0;
}

public sealed record ScanOptions(
    int? SettleSeconds = null,
    DateTimeOffset? Now = null,
    IHashCache? HashCache = null,
    bool ComputeHashes = true,
    CancellationToken CancellationToken = default);

public sealed class ScanException : Exception
{
    public ScanException(string path, string message, Exception? innerException = null)
        : base($"{path}: {message}", innerException)
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// Walks one local sync root and produces the L snapshot (SPEC §4/§5). Files
/// modified inside the settle window are skipped rather than treated as absent,
/// symlinks/special files are never followed, and case-folded name collisions
/// abort the snapshot.
/// </summary>
public static class LocalScanner
{
    public static LocalScan Scan(SyncConfig sync, ScanOptions? options = null) =>
        Scan(sync, IgnoreRules.Load(sync), options);

    public static LocalScan Scan(SyncConfig sync, IgnoreRules ignore, ScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(ignore);
        options ??= new ScanOptions();

        var root = PathUtil.TrimEnd(Path.GetFullPath(sync.LocalRoot));
        var rootInfo = new DirectoryInfo(root);

        if (!rootInfo.Exists)
            throw new ScanException(root, "local root does not exist");

        FileAttributes rootAttributes;
        try
        {
            rootAttributes = rootInfo.Attributes;
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            throw new ScanException(root, "cannot read local root", ex);
        }
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new ScanException(root, "local root is a symlink/reparse point; refusing to scan");

        var now = options.Now ?? DateTimeOffset.UtcNow;
        var settleSeconds = Math.Max(0, options.SettleSeconds ?? sync.Options.SettleSeconds);
        var settleCutoff = now.AddSeconds(-settleSeconds);

        var started = Stopwatch.StartNew();
        var entries = new List<LocalEntry>();
        var skipped = new List<ScanSkip>();
        var issues = new List<ScanIssue>();
        var collisions = new List<CaseCollision>();
        var ignoredCount = 0;

        var stack = new Stack<PendingDirectory>();
        stack.Push(new PendingDirectory(rootInfo, string.Empty));

        while (stack.Count > 0)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            List<FileSystemInfo> children;
            try
            {
                children = current.Info
                    .EnumerateFileSystemInfos()
                    .OrderBy(x => x.Name, StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                issues.Add(new ScanIssue(Display(current.RelativePath), ex.Message));
                continue;
            }

            var seenNames = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var child in children)
            {
                options.CancellationToken.ThrowIfCancellationRequested();

                FileAttributes attributes;
                try
                {
                    attributes = child.Attributes;
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    issues.Add(new ScanIssue(Display(Relative(current.RelativePath, child.Name)), ex.Message));
                    continue;
                }

                var name = PathKey.Nfc(child.Name);
                var relativePath = Relative(current.RelativePath, name);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;

                if (ignore.IsIgnoredNormalized(relativePath, isDirectory))
                {
                    ignoredCount++;
                    continue;
                }

                var pathKey = PathKey.Fold(relativePath);
                if (seenNames.TryGetValue(pathKey, out var firstPath))
                {
                    collisions.Add(new CaseCollision(firstPath, relativePath, pathKey));
                    continue;
                }

                seenNames.Add(pathKey, relativePath);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skipped.Add(new ScanSkip(relativePath, ScanSkipReason.Symlink, "reparse point/symlink"));
                    continue;
                }

                if (isDirectory)
                {
                    var directory = child as DirectoryInfo ?? new DirectoryInfo(child.FullName);
                    DateTimeOffset mtime;
                    try
                    {
                        mtime = Utc(directory.LastWriteTimeUtc);
                    }
                    catch (Exception ex) when (IsFileSystemException(ex))
                    {
                        issues.Add(new ScanIssue(relativePath, ex.Message));
                        continue;
                    }

                    entries.Add(new LocalEntry(
                        relativePath,
                        directory.FullName,
                        pathKey,
                        LocalEntryKind.Directory,
                        0,
                        mtime,
                        null));

                    stack.Push(new PendingDirectory(directory, relativePath));
                    continue;
                }

                var file = child as FileInfo ?? new FileInfo(child.FullName);
                ScanFile(
                    file,
                    relativePath,
                    pathKey,
                    settleSeconds,
                    settleCutoff,
                    options,
                    entries,
                    skipped,
                    issues);
            }
        }

        entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        skipped.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        issues.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        collisions.Sort((a, b) => StringComparer.Ordinal.Compare(a.PathKey, b.PathKey));

        started.Stop();

        return new LocalScan(
            root,
            entries,
            skipped,
            issues,
            collisions,
            ignoredCount,
            DateTimeOffset.UtcNow,
            started.Elapsed);
    }

    private static void ScanFile(
        FileInfo file,
        string relativePath,
        string pathKey,
        int settleSeconds,
        DateTimeOffset settleCutoff,
        ScanOptions options,
        List<LocalEntry> entries,
        List<ScanSkip> skipped,
        List<ScanIssue> issues)
    {
        long size;
        DateTimeOffset mtime;

        try
        {
            file.Refresh();
            if (!file.Exists)
            {
                skipped.Add(new ScanSkip(relativePath, ScanSkipReason.ChangedDuringScan, "disappeared during scan"));
                return;
            }

            size = file.Length;
            mtime = Utc(file.LastWriteTimeUtc);
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            issues.Add(new ScanIssue(relativePath, ex.Message));
            return;
        }

        if (settleSeconds > 0 && mtime > settleCutoff)
        {
            skipped.Add(new ScanSkip(relativePath, ScanSkipReason.Unsettled, "modified within settle window"));
            return;
        }

        string? sha1 = null;
        if (options.ComputeHashes)
        {
            var mtimeTicks = mtime.UtcTicks;
            if (options.HashCache is not null &&
                options.HashCache.TryGet(file.FullName, size, mtimeTicks, out var cached))
            {
                sha1 = cached;
            }
            else
            {
                try
                {
                    sha1 = ContentHash.ComputeSha1(file.FullName);
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    issues.Add(new ScanIssue(relativePath, $"cannot hash file: {ex.Message}"));
                    return;
                }

                try
                {
                    var after = new FileInfo(file.FullName);
                    after.Refresh();
                    if (!after.Exists)
                    {
                        skipped.Add(new ScanSkip(relativePath, ScanSkipReason.ChangedDuringScan, "disappeared during hash"));
                        issues.Add(new ScanIssue(relativePath, "file disappeared while hashing"));
                        return;
                    }

                    if (after.Length != size || after.LastWriteTimeUtc != mtime.UtcDateTime)
                    {
                        skipped.Add(new ScanSkip(relativePath, ScanSkipReason.ChangedDuringScan, "changed while hashing"));
                        issues.Add(new ScanIssue(relativePath, "file changed while hashing"));
                        return;
                    }
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    issues.Add(new ScanIssue(relativePath, ex.Message));
                    return;
                }

                options.HashCache?.Set(file.FullName, size, mtimeTicks, sha1);
            }
        }

        entries.Add(new LocalEntry(
            relativePath,
            file.FullName,
            pathKey,
            LocalEntryKind.File,
            size,
            mtime,
            sha1));
    }

    private static string Relative(string parent, string name) =>
        parent.Length == 0 ? name : parent + "/" + name;

    private static string Display(string relativePath) =>
        relativePath.Length == 0 ? "." : relativePath;

    private static DateTimeOffset Utc(DateTime value) =>
        new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static bool IsFileSystemException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;

    private readonly record struct PendingDirectory(DirectoryInfo Info, string RelativePath);
}
