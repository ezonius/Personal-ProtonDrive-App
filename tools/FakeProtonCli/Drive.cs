using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace FakeProtonCli;

public sealed class Drive
{
    public FakeState State { get; }

    public Drive(FakeState state) => State = state;

    public FakeNode MyFiles => State.Nodes.First(n => n.Uid == "root-my-files");

    public FakeNode? FindByUid(string? uid) =>
        uid is null ? null : State.Nodes.FirstOrDefault(n => n.Uid == uid);

    public IReadOnlyList<FakeNode> Children(FakeNode parent) =>
        State.Nodes
            .Where(n => n.ParentUid == parent.Uid && !n.Trashed)
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .ToList();

    public IEnumerable<FakeNode> SelfAndDescendants(FakeNode root)
    {
        var stack = new Stack<FakeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in State.Nodes.Where(n => n.ParentUid == node.Uid))
                stack.Push(child);
        }
    }

    public FakeNode CreateNode(FakeNode parent, string name, string type)
    {
        var now = DateTime.UtcNow;
        var node = new FakeNode
        {
            Uid = $"n{State.NextId++}",
            ParentUid = parent.Uid,
            Name = name,
            Type = type,
            CreatedUtc = now,
            ModifiedUtc = now,
            RevUid = type == "file" ? NewRevisionUid() : null,
        };
        State.Nodes.Add(node);
        return node;
    }

    public string NewRevisionUid() => "rev-" + Guid.NewGuid().ToString("N")[..12];

    public string UniqueName(FakeNode parent, string name, bool isFolder)
    {
        var stem = name;
        var ext = "";
        if (!isFolder)
        {
            var dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                stem = name[..dot];
                ext = name[dot..];
            }
        }

        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (State.Nodes.All(n => !(n.ParentUid == parent.Uid && !n.Trashed && n.Name == candidate)))
                return candidate;
        }
    }

    public int TrashSubtree(FakeNode root)
    {
        var now = DateTime.UtcNow;
        var count = 0;
        foreach (var node in SelfAndDescendants(root))
        {
            if (node.Trashed)
                continue;
            node.Trashed = true;
            node.TrashTime = now;
            node.OriginalParentUid = node.ParentUid;
            count++;
        }
        return count;
    }

    public void RestoreSubtree(FakeNode root)
    {
        foreach (var node in SelfAndDescendants(root))
        {
            node.Trashed = false;
            node.TrashTime = null;
        }

        if (!string.IsNullOrEmpty(root.OriginalParentUid))
            root.ParentUid = root.OriginalParentUid;
        root.OriginalParentUid = null;
    }

    public IReadOnlyList<FakeNode> TrashedRoots()
    {
        var trashedUids = State.Nodes.Where(n => n.Trashed).Select(n => n.Uid).ToHashSet(StringComparer.Ordinal);
        return State.Nodes
            .Where(n => n.Trashed && !trashedUids.Contains(n.ParentUid))
            .OrderBy(n => n.TrashTime)
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .ToList();
    }

    public FakeNode? ResolveActive(string path)
    {
        var segments = SplitPath(path);
        if (segments is null || segments.Count == 0)
            return null;
        if (!string.Equals(segments[0], "my-files", StringComparison.Ordinal))
            return null;

        var node = MyFiles;
        for (var i = 1; i < segments.Count; i++)
        {
            var name = segments[i];
            node = State.Nodes.FirstOrDefault(n =>
                n.ParentUid == node.Uid && !n.Trashed && n.Name == name);
            if (node is null)
                return null;
        }

        return node;
    }

    public FakeNode? ResolveTrash(string path)
    {
        var segments = SplitPath(path);
        if (segments is null || segments.Count < 2 || segments[0] != "trash")
            return null;

        var key = segments[1];
        var byUid = State.Nodes.FirstOrDefault(n => n.Trashed && n.Uid == key);
        if (byUid is not null)
            return byUid;

        var matches = TrashedRoots().Where(n => n.Name == key).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public FakeNode? ResolveAny(string path) =>
        path.StartsWith("/trash", StringComparison.Ordinal) ? ResolveTrash(path) : ResolveActive(path);

    public static List<string>? SplitPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
            return null;

        var segments = new List<string>();
        var buffer = new StringBuilder();
        for (var i = 1; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '\\' && i + 1 < path.Length)
            {
                buffer.Append(path[++i]);
                continue;
            }

            if (c == '/')
            {
                if (buffer.Length > 0)
                {
                    segments.Add(buffer.ToString());
                    buffer.Clear();
                }
            }
            else
            {
                buffer.Append(c);
            }
        }

        if (buffer.Length > 0)
            segments.Add(buffer.ToString());
        return segments;
    }

    public static JsonNode Sections() => new JsonArray(
        new JsonObject { ["path"] = "/my-files" },
        new JsonObject { ["path"] = "/devices" },
        new JsonObject { ["path"] = "/shared-by-me" },
        new JsonObject { ["path"] = "/shared-with-me" },
        new JsonObject { ["path"] = "/trash" },
        new JsonObject { ["path"] = "/albums" },
        new JsonObject { ["path"] = "/photos" },
        new JsonObject { ["path"] = "/photos-shared-by-me" },
        new JsonObject { ["path"] = "/photos-shared-with-me" },
        new JsonObject { ["path"] = "/photos-trash" });

    public JsonObject NodeJson(FakeNode node)
    {
        var obj = new JsonObject
        {
            ["uid"] = node.Uid,
            ["parentUid"] = node.ParentUid,
            ["name"] = new JsonObject { ["ok"] = true, ["value"] = node.Name },
            ["keyAuthor"] = Author(),
            ["nameAuthor"] = Author(),
            ["directRole"] = "admin",
            ["ownedBy"] = new JsonObject { ["email"] = "fake@example.com" },
            ["type"] = node.Type,
            ["isShared"] = false,
            ["isSharedByUrl"] = false,
            ["creationTime"] = Iso(node.CreatedUtc),
            ["modificationTime"] = Iso(node.ModifiedUtc),
            ["treeEventScopeId"] = "fake-scope",
        };

        if (node.Trashed)
            obj["trashTime"] = Iso(node.TrashTime ?? node.ModifiedUtc);

        if (node.Type == "folder")
        {
            obj["folder"] = new JsonObject { ["isImported"] = false };
        }
        else
        {
            var digest = new JsonObject { ["sha1Verified"] = false };
            if (!string.IsNullOrEmpty(node.Sha1))
                digest["sha1"] = node.Sha1;

            obj["mediaType"] = node.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                ? "text/plain;charset=utf-8"
                : "application/octet-stream";
            obj["totalStorageSize"] = node.Size + 78;
            obj["activeRevision"] = new JsonObject
            {
                ["uid"] = node.RevUid ?? "",
                ["state"] = "active",
                ["creationTime"] = Iso(node.ModifiedUtc),
                ["contentAuthor"] = Author(),
                ["storageSize"] = node.Size + 78,
                ["isImported"] = false,
                ["claimedSize"] = node.Size,
                ["claimedModificationTime"] = Iso(node.ClaimedMtimeUtc ?? node.ModifiedUtc),
                ["claimedDigests"] = digest,
                ["claimedAdditionalMetadata"] = new JsonObject { ["Media"] = new JsonObject() },
            };
        }

        return obj;
    }

    private static JsonObject Author() => new() { ["ok"] = true, ["value"] = "fake@example.com" };

    public static string Iso(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
