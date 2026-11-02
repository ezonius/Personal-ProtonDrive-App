using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FakeProtonCli;

public sealed class ParsedArgs
{
    public List<string> Paths { get; } = new();
    public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TransferCounters
{
    public int TransferredItems;
    public long TransferredBytes;
    public int SkippedItems;
    public int FailedItems;
    public List<object> Failures { get; } = new();
}

public sealed class Commands
{
    private readonly Drive _drive;
    private readonly StateStore _store;
    private readonly ScenarioOutcome _outcome;
    private readonly string[] _args;
    private readonly bool _json;

    public Commands(Drive drive, StateStore store, ScenarioOutcome outcome, string[] args)
    {
        _drive = drive;
        _store = store;
        _outcome = outcome;
        _args = args;
        _json = args.Contains("--json") || args.Contains("-j");
    }

    public int Execute()
    {
        if (_args.Length == 0)
            return 1;

        if (_args[0] == "auth")
            return Auth();

        if (_args[0] != "fs")
            return Error($"Unknown command: {_args[0]}");

        if (!_store.State.Auth)
            return Error("Not authenticated. Run: proton-drive auth login", 3);

        if (_args.Length < 2)
            return Error("Usage: fs <command> ...");

        var rest = _args.Skip(2).ToArray();
        return _args[1].ToLowerInvariant() switch
        {
            "list" or "ls" => List(rest),
            "info" => Info(rest),
            "create-folder" or "mkdir" => CreateFolder(rest),
            "upload" or "up" => Upload(rest),
            "download" or "down" => Download(rest),
            "move" or "mv" => Move(rest),
            "rename" or "ren" => Rename(rest),
            "trash" => Trash(rest),
            "restore" => Restore(rest),
            "delete" or "rm" => Delete(rest),
            "empty-trash" => EmptyTrash(),
            "size" => Size(rest),
            _ => Error($"Unknown filesystem command: {_args[1]}"),
        };
    }

    // ---------------------------------------------------------------- commands

    private int List(string[] rest)
    {
        var parsed = Parse(rest, isList: true);
        if (parsed.Paths.Count != 1)
            return Error("Usage: fs list [-t TYPE] path");

        var path = parsed.Paths[0];
        if (path == "/")
        {
            WriteJson(Drive.Sections());
            return 0;
        }

        if (path == "/trash")
        {
            var nodes = _drive.TrashedRoots();
            WriteJson(nodes.Select(_drive.NodeJson).ToArray());
            return 0;
        }

        var folder = _drive.ResolveActive(path);
        if (folder is null)
            return Error($"Node not found: {path}");
        if (folder.Type != "folder")
            return Error($"Not a folder: {path}");

        var typeFilter = parsed.Options.GetValueOrDefault("type");
        var children = _drive.Children(folder)
            .Where(n => typeFilter is null || n.Type == typeFilter)
            .Select(n => _drive.NodeJson(n))
            .ToArray();

        if (_json)
            WriteJson(children);
        else
            foreach (var child in children)
                Console.WriteLine($"{child["type"]}\t{child["name"]?["value"]}");

        return 0;
    }

    private int Info(string[] rest)
    {
        var parsed = Parse(rest, isList: false);
        if (parsed.Paths.Count != 1)
            return Error("Usage: fs info path");

        var node = _drive.ResolveAny(parsed.Paths[0]);
        if (node is null)
            return Error($"Node not found: {parsed.Paths[0]}");

        if (_json)
            WriteJson(_drive.NodeJson(node));
        else
            Console.WriteLine($"{node.Type}\t{node.Name}\t{node.Size} B\t{Drive.Iso(node.ModifiedUtc)}");
        return 0;
    }

    private int CreateFolder(string[] rest)
    {
        var parsed = Parse(rest, isList: false);
        if (parsed.Paths.Count != 2)
            return Error("Usage: fs create-folder parentPath name");

        var parent = _drive.ResolveActive(parsed.Paths[0]);
        if (parent is null || parent.Type != "folder")
            return Error($"Node not found: {parsed.Paths[0]}");

        var name = parsed.Paths[1];
        if (name.Contains('/') || name.Contains('\\') || name.Length == 0)
            return Error($"Invalid folder name: {name}");

        if (_drive.Children(parent).Any(n => n.Name == name))
            return Error("A file or folder with that name already exists");

        var node = _drive.CreateNode(parent, name, "folder");
        _store.Save();
        if (_json)
            WriteJson(_drive.NodeJson(node));
        else
            Console.WriteLine($"Created folder {name}");
        return 0;
    }

    private int Upload(string[] rest)
    {
        var parsed = Parse(rest, isList: false);
        if (parsed.Paths.Count < 2)
            return Error("Usage: fs upload [-f STRATEGY] [-d STRATEGY] [-t] localPath... parentPath");

        var parentPath = parsed.Paths[^1];
        var locals = parsed.Paths.Take(parsed.Paths.Count - 1).ToList();
        var parent = _drive.ResolveActive(parentPath);
        if (parent is null || parent.Type != "folder")
            return Error($"Node not found: {parentPath}");

        var fileStrategy = parsed.Options.GetValueOrDefault("fileStrategy", "create-new-revision");
        var folderStrategy = parsed.Options.GetValueOrDefault("folderStrategy", "merge");

        var missing = new List<string>();
        var resolved = new List<string>();
        foreach (var original in locals)
        {
            var path = UnescapeLocalPath(original);
            if (File.Exists(path) || Directory.Exists(path))
                resolved.Add(path);
            else
                missing.Add(original);
        }

        if (missing.Count > 0)
        {
            foreach (var item in missing)
                Console.Error.WriteLine($"No paths matched: {item}");
            return 1;
        }

        var counters = new TransferCounters();
        foreach (var local in resolved)
            UploadLocal(local, parent, fileStrategy, folderStrategy, counters);

        _store.Save();
        WriteTransfer(counters, "Uploaded");
        return counters.FailedItems > 0 ? 1 : 0;
    }

    private void UploadLocal(string local, FakeNode parent, string fileStrategy, string folderStrategy, TransferCounters counters)
    {
        if (Directory.Exists(local))
        {
            var name = Path.GetFileName(local.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var existing = _drive.Children(parent).FirstOrDefault(n => n.Name == name && n.Type == "folder");
            FakeNode folder;

            if (existing is not null)
            {
                switch (folderStrategy)
                {
                    case "skip":
                        counters.SkippedItems++;
                        return;
                    case "replace":
                        _drive.TrashSubtree(existing);
                        folder = _drive.CreateNode(parent, name, "folder");
                        break;
                    case "rename":
                        folder = _drive.CreateNode(parent, _drive.UniqueName(parent, name, isFolder: true), "folder");
                        break;
                    default: // merge
                        folder = existing;
                        break;
                }
            }
            else
            {
                folder = _drive.CreateNode(parent, name, "folder");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(local).OrderBy(x => x, StringComparer.Ordinal))
                UploadLocal(entry, folder, fileStrategy, folderStrategy, counters);
            return;
        }

        var fileName = Path.GetFileName(local);
        var bytes = File.ReadAllBytes(local);
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var current = _drive.Children(parent).FirstOrDefault(n => n.Name == fileName && n.Type == "file");

        if (current is not null && current.Sha1 == sha1 && current.Size == bytes.Length)
        {
            counters.SkippedItems++;
            return;
        }

        if (current is not null)
        {
            switch (fileStrategy)
            {
                case "skip":
                    counters.SkippedItems++;
                    return;
                case "rename":
                    fileName = _drive.UniqueName(parent, fileName, isFolder: false);
                    current = null;
                    break;
                case "replace":
                    _drive.TrashSubtree(current);
                    current = null;
                    break;
            }
        }

        if (current is null)
            current = _drive.CreateNode(parent, fileName, "file");

        current.Sha1 = sha1;
        current.HideDigest = _store.State.HideDigest;
        current.Size = bytes.Length;
        current.RevUid = _drive.NewRevisionUid();
        current.ClaimedMtimeUtc = File.GetLastWriteTimeUtc(local);
        current.ModifiedUtc = DateTime.UtcNow;
        _store.SaveBlob(sha1, bytes);
        counters.TransferredItems++;
        counters.TransferredBytes += bytes.Length;
    }

    private int Download(string[] rest)
    {
        var parsed = Parse(rest, isList: false);
        if (parsed.Paths.Count < 2)
            return Error("Usage: fs download [-f STRATEGY] [-d STRATEGY] path... localFolder");

        var localFolder = parsed.Paths[^1];
        var remotes = parsed.Paths.Take(parsed.Paths.Count - 1).ToList();
        var nodes = new List<FakeNode>();
        foreach (var remote in remotes)
        {
            var node = _drive.ResolveActive(remote);
            if (node is null)
                return Error($"Node not found: {remote}");
            nodes.Add(node);
        }

        Directory.CreateDirectory(localFolder);
        var fileStrategy = parsed.Options.GetValueOrDefault("fileStrategy", "remove");
        var folderStrategy = parsed.Options.GetValueOrDefault("folderStrategy", "merge");
        var counters = new TransferCounters();

        foreach (var node in nodes)
            DownloadNode(node, localFolder, fileStrategy, folderStrategy, counters);

        WriteTransfer(counters, "Downloaded");
        return counters.FailedItems > 0 ? 1 : 0;
    }

    private void DownloadNode(FakeNode node, string localFolder, string fileStrategy, string folderStrategy, TransferCounters counters)
    {
        if (node.Type == "folder")
        {
            var target = Path.Combine(localFolder, node.Name);
            if (File.Exists(target))
            {
                if (folderStrategy == "skip")
                {
                    counters.SkippedItems++;
                    return;
                }
                File.Delete(target);
            }

            if (Directory.Exists(target))
            {
                switch (folderStrategy)
                {
                    case "skip":
                        counters.SkippedItems++;
                        return;
                    case "rename":
                        target = UniqueLocalPath(target, isFolder: true);
                        break;
                    case "remove":
                    case "replace":
                        Directory.Delete(target, recursive: true);
                        break;
                }
            }

            Directory.CreateDirectory(target);
            foreach (var child in _drive.Children(node))
                DownloadNode(child, target, fileStrategy, folderStrategy, counters);
            return;
        }

        var destination = Path.Combine(localFolder, node.Name);
        if (File.Exists(destination))
        {
            switch (fileStrategy)
            {
                case "skip":
                    counters.SkippedItems++;
                    return;
                case "rename":
                    destination = UniqueLocalPath(destination, isFolder: false);
                    break;
                default:
                    File.Delete(destination);
                    break;
            }
        }

        var bytes = _store.ReadBlob(node.Sha1);
        if (bytes is null)
        {
            counters.FailedItems++;
            counters.Failures.Add(new { path = node.Name, error = "blob missing" });
            return;
        }

        if (_outcome.TruncateDownload)
        {
            File.WriteAllBytes(destination, bytes[..(bytes.Length / 2)]);
            counters.FailedItems++;
            counters.Failures.Add(new { path = node.Name, error = "truncated download" });
            return;
        }

        File.WriteAllBytes(destination, bytes);
        File.SetLastWriteTimeUtc(destination, node.ClaimedMtimeUtc ?? node.ModifiedUtc);
        counters.TransferredItems++;
        counters.TransferredBytes += bytes.Length;
    }

    private int Move(string[] rest)
    {
        var parsed = Parse(rest, isList: false);
        if (parsed.Paths.Count < 2)
            return Error("Usage: fs move sourcePath... targetParentPath");

        var targetParent = _drive.ResolveActive(parsed.Paths[^1]);
        if (targetParent is null || targetParent.Type != "folder")
            return Error($"Node not found: {parsed.Paths[^1]}");

        var sources = new List<FakeNode>();
        foreach (var path in parsed.Paths.Take(parsed.Paths.Count - 1))
        {
            var node = _drive.ResolveActive(path);
            if (node is null)
                return Error($"Node not found: {path}");
            sources.Add(node);
        }

        var moved = 0;
        foreach (var source in sources)
        {
            if (_drive.Children(targetParent).Any(n => n.Name == source.Name && n.Uid != source.Uid))
                return Error($"A file or folder with that name already exists: {source.Name}");
            source.ParentUid = targetParent.Uid;
            source.ModifiedUtc = DateTime.UtcNow;
            moved++;
        }

        _store.Save();
        WriteJson(new { movedItems = moved });
        return 0;
    }

    private int Rename(string[] rest)
    {
        var parsed = Parse(rest, isList: false);
        if (parsed.Paths.Count != 2)
            return Error("Usage: fs rename path newName");

        var node = _drive.ResolveActive(parsed.Paths[0]);
        if (node is null)
            return Error($"Node not found: {parsed.Paths[0]}");

        var newName = parsed.Paths[1];
        if (newName.Contains('/') || newName.Contains('\\') || newName.Length == 0)
            return Error($"Invalid name: {newName}");

        var parent = _drive.FindByUid(node.ParentUid);
        if (parent is null)
            return Error("Parent not found");
        if (_drive.Children(parent).Any(n => n.Name == newName && n.Uid != node.Uid))
            return Error("A file or folder with that name already exists");

        node.Name = newName;
        node.ModifiedUtc = DateTime.UtcNow;
        _store.Save();

        if (_json)
            WriteJson(_drive.NodeJson(node));
        else
            Console.WriteLine($"Renamed to {newName}");
        return 0;
    }

    private int Trash(string[] rest)
    {
        var parsed = Parse(rest, isList: false);
        if (parsed.Paths.Count == 0)
            return Error("Usage: fs trash path...");

        var count = 0;
        foreach (var path in parsed.Paths)
        {
            var node = _drive.ResolveActive(path);
            if (node is null)
                return Error($"Node not found: {path}");
            count += _drive.TrashSubtree(node);
        }

        _store.Save();
        WriteJson(new { trashedItems = count });
        return 0;
    }

    private int Restore(string[] rest)
    {
        var parsed = Parse(rest, isList: false);
        if (parsed.Paths.Count == 0)
            return Error("Usage: fs restore path...");

        var count = 0;
        foreach (var path in parsed.Paths)
        {
            var node = _drive.ResolveTrash(path);
            if (node is null)
                return Error($"Node not found or ambiguous: {path}");

            var parentUid = node.OriginalParentUid ?? node.ParentUid;
            var parent = _drive.FindByUid(parentUid);
            if (parent is null || parent.Trashed)
                return Error($"Original parent is not available for: {node.Name}");
            if (_drive.Children(parent).Any(n => n.Name == node.Name && n.Uid != node.Uid))
                return Error($"A file or folder with that name already exists: {node.Name}");

            _drive.RestoreSubtree(node);
            count++;
        }

        _store.Save();
        WriteJson(new { restoredItems = count });
        return 0;
    }

    private int Delete(string[] rest)
    {
        var parsed = Parse(rest, isList: false);
        if (parsed.Paths.Count == 0)
            return Error("Usage: fs delete path...");

        var count = 0;
        foreach (var path in parsed.Paths)
        {
            var node = _drive.ResolveAny(path);
            if (node is null)
                return Error($"Node not found: {path}");

            var uids = _drive.SelfAndDescendants(node).Select(n => n.Uid).ToHashSet(StringComparer.Ordinal);
            count += _store.State.Nodes.RemoveAll(n => uids.Contains(n.Uid));
        }

        _store.Save();
        WriteJson(new { deletedItems = count });
        return 0;
    }

    private int EmptyTrash()
    {
        var removed = _store.State.Nodes.RemoveAll(n => n.Trashed);
        _store.Save();
        WriteJson(new { removedItems = removed });
        return 0;
    }

    private int Size(string[] rest)
    {
        var parsed = Parse(rest, isList: false);
        if (parsed.Paths.Count != 1)
            return Error("Usage: fs size path");

        var node = _drive.ResolveAny(parsed.Paths[0]);
        if (node is null)
            return Error($"Node not found: {parsed.Paths[0]}");

        var total = _drive.SelfAndDescendants(node).Sum(n => n.Size);
        WriteJson(new { size = total });
        return 0;
    }

    private int Auth()
    {
        var sub = _args.Length > 1 ? _args[1] : "status";
        switch (sub)
        {
            case "login":
                _store.State.Auth = true;
                _store.Save();
                Console.WriteLine("Logged in");
                return 0;
            case "logout":
                _store.State.Auth = false;
                _store.Save();
                Console.WriteLine("Logged out");
                return 0;
            case "status":
                Console.WriteLine(_store.State.Auth ? "Authenticated" : "Not authenticated");
                return _store.State.Auth ? 0 : 3;
            default:
                return Error($"Unknown auth command: {sub}");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static ParsedArgs Parse(string[] args, bool isList)
    {
        var parsed = new ParsedArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--json":
                case "-j":
                    parsed.Options["json"] = "true";
                    break;
                case "-t":
                case "--skip-thumbnails" when !isList:
                    if (isList)
                    {
                        if (i + 1 < args.Length)
                            parsed.Options["type"] = args[++i];
                    }
                    else
                    {
                        parsed.Options["skipThumbnails"] = "true";
                    }
                    break;
                case "--type":
                    if (i + 1 < args.Length)
                        parsed.Options["type"] = args[++i];
                    break;
                case "-f":
                case "--file-conflict-strategy":
                    if (i + 1 < args.Length)
                        parsed.Options["fileStrategy"] = args[++i];
                    break;
                case "-d":
                case "--folder-conflict-strategy":
                    if (i + 1 < args.Length)
                        parsed.Options["folderStrategy"] = args[++i];
                    break;
                default:
                    if (arg.StartsWith("--file-conflict-strategy=", StringComparison.Ordinal))
                        parsed.Options["fileStrategy"] = arg["--file-conflict-strategy=".Length..];
                    else if (arg.StartsWith("--folder-conflict-strategy=", StringComparison.Ordinal))
                        parsed.Options["folderStrategy"] = arg["--folder-conflict-strategy=".Length..];
                    else if (arg.StartsWith('-') && arg.Length > 1)
                        parsed.Options[arg.TrimStart('-')] = "true";
                    else
                        parsed.Paths.Add(arg);
                    break;
            }
        }

        return parsed;
    }

    private static string UnescapeLocalPath(string path) => path
        .Replace("[[]", "[")
        .Replace("[]]", "]")
        .Replace("[*]", "*")
        .Replace("[?]", "?");

    private static string UniqueLocalPath(string path, bool isFolder)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileName(path);
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
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (isFolder ? !Directory.Exists(candidate) : !File.Exists(candidate))
                return candidate;
        }
    }

    private void WriteTransfer(TransferCounters counters, string verb)
    {
        if (_json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                transferredItems = counters.TransferredItems,
                transferredBytes = counters.TransferredBytes,
                skippedItems = counters.SkippedItems,
                failedItems = counters.FailedItems,
                failures = counters.Failures,
            }, FakeJson.Compact));
            return;
        }

        Console.WriteLine("Transfer summary:");
        Console.WriteLine($"  {verb}: {counters.TransferredItems} items ({counters.TransferredBytes} B)");
        if (counters.SkippedItems > 0)
            Console.WriteLine($"  Skipped: {counters.SkippedItems} items");
        if (counters.FailedItems > 0)
            Console.WriteLine($"  Failed: {counters.FailedItems} items");
    }

    private static void WriteJson(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, FakeJson.Pretty));

    private static int Error(string message, int code = 1)
    {
        Console.Error.WriteLine(message);
        return code;
    }
}
