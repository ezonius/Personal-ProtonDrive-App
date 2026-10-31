using System.Text.Json;
using PdSync.Config;
using PdSync.State;

namespace PdSync.Exec;

/// <summary>
/// Moves local files/directories into <c>&lt;stateDir&gt;/trash/&lt;syncId&gt;/&lt;runId&gt;</c>
/// and writes a manifest per run. Nothing is ever unlinked directly (D3).
/// </summary>
public sealed class LocalTrashStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly List<TrashEntry> _entries = new();
    private readonly string _root;
    private readonly string _manifestPath;

    public LocalTrashStore(PdsyncConfig config, SyncConfig sync, long runId)
    {
        SyncId = StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot);
        _root = Path.Combine(config.StateDir, "trash", SyncId, runId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _manifestPath = Path.Combine(_root, "manifest.json");
    }

    public string SyncId { get; }

    public string Root => _root;

    public IReadOnlyList<TrashEntry> Entries => _entries;

    public string Move(string fullPath, string relativePath, string reason)
    {
        var isDirectory = Directory.Exists(fullPath);
        if (!isDirectory && !File.Exists(fullPath))
            return string.Empty;

        var destination = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        destination = MakeUnique(destination, isDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (isDirectory)
            FileSystemUtil.MoveDirectory(fullPath, destination);
        else
            FileSystemUtil.MoveFile(fullPath, destination, overwrite: false);

        _entries.Add(new TrashEntry(relativePath, fullPath, destination, isDirectory, reason, DateTimeOffset.UtcNow));
        SaveManifest();
        return destination;
    }

    public void SaveManifest()
    {
        if (_entries.Count == 0)
            return;

        Directory.CreateDirectory(_root);
        var temp = _manifestPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
        File.Move(temp, _manifestPath, overwrite: true);
    }

    private static string MakeUnique(string path, bool isDirectory)
    {
        if (isDirectory ? !Directory.Exists(path) : !File.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileName(path);
        var stem = name;
        var extension = string.Empty;
        if (!isDirectory)
        {
            var dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                stem = name[..dot];
                extension = name[dot..];
            }
        }

        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (isDirectory ? !Directory.Exists(candidate) : !File.Exists(candidate))
                return candidate;
        }
    }

    public sealed record TrashEntry(
        string RelativePath,
        string OriginalPath,
        string TrashPath,
        bool IsDirectory,
        string Reason,
        DateTimeOffset TrashedAt);
}
