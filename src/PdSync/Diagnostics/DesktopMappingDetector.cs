using System.Text.Json;
using PdSync.Config;

namespace PdSync.Diagnostics;

/// <summary>A local folder mapped by the Proton Drive desktop app.</summary>
public sealed record DesktopMapping(string LocalRoot, string SourceFile);

/// <summary>
/// Result of scanning for desktop-app mappings. A malformed or unreadable file
/// yields no mappings plus a diagnostic; detection never throws.
/// </summary>
public sealed record DesktopMappingScan(IReadOnlyList<DesktopMapping> Mappings, string? Diagnostic)
{
    public static DesktopMappingScan Empty { get; } = new(Array.Empty<DesktopMapping>(), null);
}

/// <summary>
/// Seam for the desktop-app mapping lookup so <c>doctor</c> can be tested
/// without a Proton Drive desktop install.
/// </summary>
public interface IDesktopMappingDetector
{
    DesktopMappingScan Detect();
}

/// <summary>
/// Reads the Proton Drive desktop app's <c>Mappings.json</c>. The file is
/// user-local state; pdsync only ever reads it and treats every failure as a
/// non-fatal diagnostic. On non-Windows platforms the default detector is a
/// no-op because the desktop app only publishes this file on Windows.
/// </summary>
public sealed class DesktopMappingDetector : IDesktopMappingDetector
{
    private readonly IReadOnlyList<string>? _candidatePaths;

    /// <param name="candidatePaths">
    /// Optional explicit mapping-file locations. When omitted, the known
    /// per-user and machine-wide Proton Drive locations are probed.
    /// </param>
    public DesktopMappingDetector(IReadOnlyList<string>? candidatePaths = null) =>
        _candidatePaths = candidatePaths;

    /// <summary>Known locations of the desktop app's mapping file, newest first.</summary>
    public static IReadOnlyList<string> DefaultCandidatePaths()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<string>();

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var paths = new List<string>();
        Add(paths, local, "Proton", "Proton Drive", "Mappings.json");
        Add(paths, roaming, "Proton", "Proton Drive", "Mappings.json");
        Add(paths, programData, "Proton", "Proton Drive", "Mappings.json");
        // Older layouts seen in the wild.
        Add(paths, local, "ProtonDrive", "Mappings.json");
        Add(paths, local, "Proton", "ProtonDrive", "Mappings.json");
        return paths;
    }

    public DesktopMappingScan Detect()
    {
        var paths = _candidatePaths ?? DefaultCandidatePaths();
        if (paths.Count == 0)
            return DesktopMappingScan.Empty;

        string? diagnostic = null;
        var found = false;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;

            found = true;
            if (TryRead(path, out var mappings, out var error))
                return new DesktopMappingScan(mappings, null);

            diagnostic = error;
        }

        return found
            ? new DesktopMappingScan(Array.Empty<DesktopMapping>(), diagnostic)
            : DesktopMappingScan.Empty;
    }

    private static bool TryRead(
        string path,
        out List<DesktopMapping> mappings,
        out string? diagnostic)
    {
        mappings = new List<DesktopMapping>();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Mappings", out var list) ||
                list.ValueKind != JsonValueKind.Array)
            {
                diagnostic = $"unrecognized desktop-app mappings file (missing Mappings array): {path}";
                return false;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("Local", out var local) ||
                    local.ValueKind != JsonValueKind.Object ||
                    !local.TryGetProperty("RootFolderPath", out var root) ||
                    root.ValueKind != JsonValueKind.String)
                    continue;

                var value = root.GetString();
                if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                    continue;

                mappings.Add(new DesktopMapping(value, path));
            }

            diagnostic = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            mappings.Clear();
            diagnostic = $"cannot read desktop-app mappings {path}: {ex.Message}";
            return false;
        }
    }

    private static void Add(List<string> paths, params string[] parts)
    {
        if (parts.Any(string.IsNullOrEmpty))
            return;
        paths.Add(Path.Combine(parts));
    }
}

public enum DesktopMappingOverlapKind
{
    None,
    Exact,
    LocalRootInsideMapping,
    MappingInsideLocalRoot,
}

public sealed record DesktopMappingOverlap(DesktopMapping Mapping, DesktopMappingOverlapKind Kind);

/// <summary>
/// Compares configured local roots with desktop-app mappings using pdsync's
/// path identity rules (NFC + case-folded, segment-aware).
/// </summary>
public static class DesktopMappingOverlapDetector
{
    public static DesktopMappingOverlap? Find(string localRoot, IEnumerable<DesktopMapping> mappings)
    {
        foreach (var mapping in mappings)
        {
            var kind = Classify(localRoot, mapping.LocalRoot);
            if (kind != DesktopMappingOverlapKind.None)
                return new DesktopMappingOverlap(mapping, kind);
        }

        return null;
    }

    public static DesktopMappingOverlapKind Classify(string localRoot, string mappingRoot)
    {
        var local = TryIdentityKey(localRoot);
        var mapping = TryIdentityKey(mappingRoot);
        if (local is null || mapping is null)
            return DesktopMappingOverlapKind.None;

        if (local == mapping)
            return DesktopMappingOverlapKind.Exact;
        if (IsUnder(local, mapping))
            return DesktopMappingOverlapKind.LocalRootInsideMapping;
        if (IsUnder(mapping, local))
            return DesktopMappingOverlapKind.MappingInsideLocalRoot;
        return DesktopMappingOverlapKind.None;
    }

    private static string? TryIdentityKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var full = PathUtil.TrimEnd(Path.GetFullPath(path)).Replace('\\', '/');
            return PathKey.Fold(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    private static bool IsUnder(string child, string parent) =>
        child.Length > parent.Length &&
        child.StartsWith(parent, StringComparison.Ordinal) &&
        (parent.EndsWith('/') || child[parent.Length] == '/');
}
