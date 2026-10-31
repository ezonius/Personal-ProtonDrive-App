using PdSync.Config;
using PdSync.Proton;

namespace PdSync.Remote;

/// <summary>
/// Builds the R snapshot by a BFS over <c>fs list --json</c> (SPEC §6.3). The
/// result is cached for one run; <c>--fast</c> subtree skipping is a later
/// optimization and is deliberately not implemented here.
/// </summary>
public static class RemoteIndex
{
    public static RemoteSnapshot Build(ProtonCli cli, string root)
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
}
