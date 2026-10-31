using PdSync.Config;

namespace PdSync.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Root { get; }

    public TempDirectory()
    {
        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pdsync-test-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Combine(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = Root;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return System.IO.Path.Combine(all);
    }

    public string Write(string relativePath, string content)
    {
        var path = Combine(relativePath.Split('/'));
        var directory = System.IO.Path.GetDirectoryName(path);
        if (directory is not null)
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Path with forward slashes, safe to embed in a double-quoted YAML scalar.</summary>
    public string YamlPath(params string[] parts) => Combine(parts).Replace('\\', '/');

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best effort: SQLite may still hold a handle on a failed test.
        }
    }
}

public static class ConfigTestHelper
{
    public static string MinimalYaml(
        TempDirectory temp,
        string syncName = "docs",
        string? local = null,
        string remote = "/my-files/docs",
        string? extraRoot = null,
        string? extraDefaults = null,
        string? extraSync = null)
    {
        var localPath = local ?? temp.Combine("local");
        if (local is null && !Directory.Exists(localPath))
            Directory.CreateDirectory(localPath);

        var defaults = extraDefaults is null ? "" : extraDefaults;
        var root = extraRoot is null ? "" : extraRoot;
        var extra = extraSync is null ? "" : extraSync;

        return $"""
            version: 1
            stateDir: "{temp.YamlPath("state")}"
            {defaults}
            {root}
            syncs:
              {syncName}:
                local: "{localPath.Replace('\\', '/')}"
                remote: "{remote}"
            {extra}
            """;
    }

    public static PdsyncConfig Load(TempDirectory temp, string yaml, string fileName = "pdsync.yaml")
    {
        var path = temp.Write(fileName, yaml);
        return ConfigLoader.Load(path);
    }
}
