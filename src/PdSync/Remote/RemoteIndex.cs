using PdSync.Config;
using PdSync.Proton;
using PdSync.State;

namespace PdSync.Remote;

/// <summary>
/// Builds the R snapshot by a BFS over <c>fs list --json</c> (SPEC §6.3). The
/// result is cached for one run; <c>--fast</c> subtree skipping is a later
/// optimization and is deliberately not implemented here.
/// </summary>
public static class RemoteIndex
{
    public static RemoteSnapshot Build(ProtonCli cli, string root) =>
        Build(cli, root, baseline: null, skipFolder: null);

    /// <summary>
    /// Builds R. When <paramref name="skipFolder"/> returns true for a relative
    /// folder, that folder is not listed; its entries are synthesized from the
    /// baseline instead. This is the fast-mode contract: the skipped subtree
    /// produces no actions in the current run.
    /// </summary>
    public static RemoteSnapshot Build(
        ProtonCli cli,
        string root,
        IReadOnlyDictionary<string, EntryRecord>? baseline,
        Func<string, bool>? skipFolder)
    {
        ArgumentNullException.ThrowIfNull(cli);
        var normalizedRoot = RemotePath.Normalize(root);

        var entries = new List<RemoteEntry>();
        var skipped = new List<RemoteSkip>();
        var collisions = new List<RemoteCaseCollision>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<(string RemotePath, string RelativePath)>();
        queue.Enqueue((normalizedRoot, string.Empty));

        while (queue.Count > 0)
        {
            var (folder, relativeFolder) = queue.Dequeue();

            if (relativeFolder.Length > 0 && baseline is not null && skipFolder is not null &&
                skipFolder(relativeFolder))
            {
                AddBaselineSubtree(baseline, relativeFolder, entries, seen);
                continue;
            }

            var children = cli.List(folder);

            foreach (var node in children.OrderBy(n => n.Name, StringComparer.Ordinal))
            {
                if (!node.NameOk)
                {
                    skipped.Add(new RemoteSkip(null, "undecryptable node name"));
                    continue;
                }

                var name = PathKey.Nfc(node.Name);
                var relativePath = relativeFolder.Length == 0
                    ? name
                    : relativeFolder + "/" + name;

                if (node.Name.Length == 0 || node.Name.Contains('/') ||
                    node.Name is "." or ".." || node.Name.Any(char.IsControl))
                {
                    skipped.Add(new RemoteSkip(relativePath, "name cannot be represented safely as a path"));
                    continue;
                }

                var pathKey = PathKey.Fold(relativePath);

                if (seen.TryGetValue(pathKey, out var firstPath))
                {
                    collisions.Add(new RemoteCaseCollision(firstPath, relativePath, pathKey));
                    continue;
                }

                seen.Add(pathKey, relativePath);

                RemoteEntryKind kind;
                if (node.Type == "folder")
                    kind = RemoteEntryKind.Folder;
                else if (node.Type == "file")
                    kind = RemoteEntryKind.File;
                else
                {
                    skipped.Add(new RemoteSkip(relativePath, $"unsupported node type '{node.Type}'"));
                    continue;
                }

                entries.Add(new RemoteEntry(
                    relativePath,
                    pathKey,
                    kind,
                    node.Uid,
                    node.RevisionUid,
                    node.Size,
                    node.ModifiedAt,
                    node.ClaimedMtime,
                    node.Sha1,
                    node.Sha1Verified));

                if (kind == RemoteEntryKind.Folder)
                {
                    var remotePath = folder.TrimEnd('/') + "/" + node.Name;
                    queue.Enqueue((remotePath, relativePath));
                }
            }
        }

        entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        skipped.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path ?? string.Empty, b.Path ?? string.Empty));
        collisions.Sort((a, b) => StringComparer.Ordinal.Compare(a.PathKey, b.PathKey));

        return new RemoteSnapshot(normalizedRoot, entries, skipped, collisions, DateTimeOffset.UtcNow);
    }

    private static void AddBaselineSubtree(
        IReadOnlyDictionary<string, EntryRecord> baseline,
        string relativeFolder,
        List<RemoteEntry> entries,
        Dictionary<string, string> seen)
    {
        var folderKey = PathKey.Fold(relativeFolder);
        var prefix = folderKey + "/";

        foreach (var entry in baseline.Values.OrderBy(e => e.PathKey, StringComparer.Ordinal))
        {
            if (!entry.PathKey.StartsWith(prefix, StringComparison.Ordinal) || !entry.RemotePresent)
                continue;
            if (entry.Frozen)
                continue;
            if (seen.ContainsKey(entry.PathKey))
                continue;

            seen[entry.PathKey] = entry.Path;
            entries.Add(new RemoteEntry(
                entry.Path,
                entry.PathKey,
                entry.Kind == EntryKind.Folder ? RemoteEntryKind.Folder : RemoteEntryKind.File,
                entry.RemoteUid ?? string.Empty,
                entry.RemoteRevUid,
                entry.RemoteSize ?? 0,
                FromMilliseconds(entry.RemoteMtimeMs),
                entry.RemoteMtimeMs is null ? null : FromMilliseconds(entry.RemoteMtimeMs),
                entry.RemoteSha1,
                Sha1Verified: false));
        }
    }

    private static DateTimeOffset FromMilliseconds(long? milliseconds) =>
        milliseconds is null ? DateTimeOffset.UnixEpoch : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value);
}
