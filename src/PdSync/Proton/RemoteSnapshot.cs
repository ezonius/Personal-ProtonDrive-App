using PdSync.Config;

namespace PdSync.Proton;

public enum RemoteEntryKind
{
    File,
    Folder,
}

/// <summary>
/// One remote node, keyed by its display path. Folders carry no content digest;
/// files carry the active revision's claimed size/digest/mtime.
/// </summary>
public sealed record RemoteEntry(
    string Path,
    string PathKey,
    RemoteEntryKind Kind,
    string Uid,
    string? RevisionUid,
    long Size,
    DateTimeOffset ModifiedAt,
    DateTimeOffset? ClaimedMtime,
    string? Sha1,
    bool Sha1Verified)
{
    public bool IsDirectory => Kind == RemoteEntryKind.Folder;
}

public sealed record RemoteSkip(string? Path, string Reason);

public sealed record RemoteCaseCollision(string FirstPath, string SecondPath, string PathKey);

/// <summary>
/// The R snapshot: a complete or partial tree beneath the sync root. When it is
/// incomplete (<see cref="IsComplete"/> false), the planner must not infer remote
/// deletions from missing paths.
/// </summary>
public sealed class RemoteSnapshot
{
    public RemoteSnapshot(
        string root,
        IReadOnlyList<RemoteEntry> entries,
        IReadOnlyList<RemoteSkip> skipped,
        IReadOnlyList<RemoteCaseCollision> collisions,
        DateTimeOffset scannedAt)
    {
        Root = root;
        Entries = entries;
        Skipped = skipped;
        Collisions = collisions;
        ScannedAt = scannedAt;

        var byKey = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
        var byUid = new Dictionary<string, RemoteEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            byKey[entry.PathKey] = entry;
            if (!string.IsNullOrEmpty(entry.Uid))
                byUid.TryAdd(entry.Uid, entry);
        }
        ByKey = byKey;
        ByUid = byUid;

        var skippedByKey = new Dictionary<string, RemoteSkip>(StringComparer.Ordinal);
        foreach (var skip in skipped)
        {
            if (!string.IsNullOrEmpty(skip.Path))
                skippedByKey[PathKey.Fold(skip.Path)] = skip;
        }
        SkippedByKey = skippedByKey;
    }

    public string Root { get; }
    public IReadOnlyList<RemoteEntry> Entries { get; }
    public IReadOnlyDictionary<string, RemoteEntry> ByKey { get; }
    public IReadOnlyDictionary<string, RemoteEntry> ByUid { get; }
    public IReadOnlyList<RemoteSkip> Skipped { get; }
    public IReadOnlyDictionary<string, RemoteSkip> SkippedByKey { get; }
    public IReadOnlyList<RemoteCaseCollision> Collisions { get; }
    public DateTimeOffset ScannedAt { get; }

    public bool IsComplete => Collisions.Count == 0 && Skipped.Count == 0;

    public RemoteEntry? Get(string pathKey) => ByKey.GetValueOrDefault(pathKey);

    public RemoteEntry? GetByUid(string? uid) =>
        string.IsNullOrEmpty(uid) ? null : ByUid.GetValueOrDefault(uid);
}
