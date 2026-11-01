using System.Text.Json;
using System.Text.Json.Serialization;
using PdSync.Config;
using PdSync.Scan;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PdSync.Cli;

/// <summary>
/// `pdsync init` creates a config and a default `.pdignore`. Every value is fed
/// through <see cref="ConfigValidator"/> before anything is written, so init can
/// never leave behind a config the rest of the tool would reject. It never
/// opens the state DB, takes the run lock, or touches Proton.
/// </summary>
public static class InitCommand
{
    public const string DefaultIgnoreText = """
        # pdsync ignore rules (same syntax as .gitignore).
        # Ignored paths are never uploaded or downloaded.
        .DS_Store
        Thumbs.db
        *.tmp
        *.log
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    /// Runs init and returns an exit code. Config problems are thrown as
    /// <see cref="ConfigException"/> so the CLI maps them to exit 4.
    /// </summary>
    public static int Execute(
        string? explicitConfigPath,
        bool yes,
        bool json,
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<string?>? findExistingConfig = null)
    {
        // Keep stdout machine-readable under --json: prompts and the overwrite
        // question are diagnostics, not output.
        var promptOutput = json ? error : output;
        Func<string?> discovery = findExistingConfig ?? (() => ConfigLoader.FindExisting());
        var configPath = ResolveTargetPath(explicitConfigPath, discovery);
        if (File.Exists(configPath))
        {
            throw new ConfigException(
                $"init: config file already exists: {configPath}; refusing to overwrite. " +
                "Pass --config <other-path> or remove it first.");
        }

        if (Directory.Exists(configPath))
            throw new ConfigException($"init: config path is a directory: {configPath}");

        var defaultLocalRoot = PathUtil.TrimEnd(Path.GetFullPath(Environment.CurrentDirectory));
        string localRootInput;
        string syncName;
        string remoteRoot;

        if (yes)
        {
            localRootInput = defaultLocalRoot;
            syncName = DefaultSyncName(localRootInput);
            remoteRoot = $"/my-files/{syncName}";
        }
        else
        {
            localRootInput = Prompt(input, promptOutput, "local root", defaultLocalRoot);
            syncName = Prompt(input, promptOutput, "sync name", DefaultSyncName(localRootInput));
            remoteRoot = Prompt(input, promptOutput, "remote root", $"/my-files/{syncName}");
        }

        var localRoot = NormalizeLocalRoot(localRootInput);
        var raw = new RawConfig
        {
            Version = 1,
            Defaults = new RawDefaults
            {
                Direction = "two-way",
                Conflict = "newer-wins",
                OnLocalDelete = "remote-trash",
                OnRemoteDelete = "local-trash",
                InitialSync = "adopt",
                IgnoreFile = ".pdignore",
            },
            Syncs = new Dictionary<string, RawSync>(StringComparer.Ordinal)
            {
                [syncName] = new RawSync { Local = localRoot, Remote = remoteRoot },
            },
        };

        // Serialize and validate the exact text that will land on disk. This
        // catches both invalid inputs and a serializer that emits something the
        // loader cannot read back.
        var yaml = Serialize(raw);
        RawConfig reparsed;
        try
        {
            reparsed = ConfigLoader.Deserialize(yaml);
        }
        catch (YamlException ex)
        {
            throw new ConfigException($"init: generated config is not valid YAML: {ex.Message}");
        }

        var validated = ConfigValidator.Validate(reparsed, configPath);
        var sync = validated.Syncs[0];

        var ignoreFile = sync.IgnoreFile;
        if (PathUtil.IdentityKey(configPath) == PathUtil.IdentityKey(ignoreFile))
        {
            throw new ConfigException(
                $"init: config path and ignore path are the same file: {configPath}; " +
                "pass a different --config path.");
        }

        if (Directory.Exists(ignoreFile))
        {
            throw new ConfigException(
                $"init: ignore path is a directory: {ignoreFile}; remove it or choose another local root.");
        }

        // A config created inside the local root would otherwise be synced as
        // an ordinary file. Ignore it by default; a config outside the root (or
        // the ignore file itself, already rejected above) needs no entry.
        var configRelativePath = InRootRelativePath(configPath, sync.LocalRoot);
        var ignoreText = BuildIgnoreText(configRelativePath);

        var ignoreExists = File.Exists(ignoreFile);
        var overwroteIgnore = false;
        if (ignoreExists)
        {
            if (yes || !Confirm(input, promptOutput,
                    $"ignore file already exists: {ignoreFile}. Overwrite it?"))
            {
                throw new ConfigException(
                    $"init: ignore file already exists: {ignoreFile}; refusing to overwrite. " +
                    (yes
                        ? "Rerun without --yes to confirm interactively."
                        : "No files were written."));
            }

            overwroteIgnore = true;
        }

        var configDirectory = Path.GetDirectoryName(configPath);
        string? ignoreTemp = null;
        var configCreated = false;
        try
        {
            if (!string.IsNullOrEmpty(configDirectory))
                Directory.CreateDirectory(configDirectory);

            // FileMode.CreateNew makes the no-overwrite contract race-safe: a
            // config created after the probe above is rejected instead of
            // silently overwritten.
            WriteNewConfig(configPath, yaml);
            configCreated = true;

            // Stage the ignore text next to its destination, then move it into
            // place so a failed write never leaves a truncated ignore file.
            ignoreTemp = ignoreFile + ".pdsync-init-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(ignoreTemp, ignoreText);
            ValidateIgnoreText(ignoreTemp, configRelativePath, configPath);
            File.Move(ignoreTemp, ignoreFile, overwrite: true);
            ignoreTemp = null;
        }
        catch (ConfigException)
        {
            // Validation failures and other config errors must leave no staged
            // ignore file and no config created by this invocation.
            TryDelete(ignoreTemp);
            if (configCreated)
                TryDelete(configPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(ignoreTemp);
            if (configCreated)
                TryDelete(configPath);
            throw new ConfigException($"init: cannot write files: {ex.Message}");
        }

        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                command = "init",
                config = configPath,
                ignoreFile,
                sync = sync.Name,
                local = sync.LocalRoot,
                remote = sync.RemoteRoot,
                ignoreFileOverwritten = overwroteIgnore,
            }, JsonOptions));
        }
        else
        {
            output.WriteLine($"created config: {configPath}");
            output.WriteLine($"{(overwroteIgnore ? "updated" : "created")} ignore file: {ignoreFile}");
            output.WriteLine($"sync {sync.Name}: {sync.LocalRoot} <-> {sync.RemoteRoot}");
        }

        return 0;
    }

    /// <summary>
    /// `--config` and PDSYNC_CONFIG select the target even when the file does
    /// not exist yet. Otherwise an existing config in discovery order is the
    /// target (and is then refused as an overwrite); with no existing config we
    /// create ./pdsync.yaml.
    /// </summary>
    private static string ResolveTargetPath(string? explicitConfigPath, Func<string?> findExistingConfig)
    {
        if (!string.IsNullOrWhiteSpace(explicitConfigPath))
            return Absolute(explicitConfigPath, "--config");

        var fromEnvironment = Environment.GetEnvironmentVariable(ConfigLoader.ConfigEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Absolute(fromEnvironment, ConfigLoader.ConfigEnvironmentVariable);

        return findExistingConfig()
               ?? Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "pdsync.yaml"));
    }

    /// <summary>
    /// Claims the config path exclusively. A concurrent creation after the
    /// initial probe surfaces as the same exit-4 "already exists" error as a
    /// config that existed up front.
    /// </summary>
    internal static void WriteNewConfig(string path, string yaml)
    {
        var created = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            using var writer = new StreamWriter(stream);
            writer.Write(yaml);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (created)
                TryDelete(path);

            if (!created && File.Exists(path))
            {
                throw new ConfigException(
                    $"init: config file already exists: {path}; refusing to overwrite. " +
                    "Pass --config <other-path> or remove it first.");
            }

            throw new ConfigException($"init: cannot write config '{path}': {ex.Message}");
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort rollback; the original write error is more useful.
        }
    }

    /// <summary>
    /// Relative POSIX path of the generated config when it is inside the local
    /// root, or null when it is outside. Only the platform directory separator
    /// is converted to '/': a literal backslash is a filename character on Unix
    /// and is left intact for validation to accept or reject safely.
    /// </summary>
    internal static string? InRootRelativePath(string configPath, string localRoot)
    {
        if (!PathUtil.IsSameOrUnder(configPath, localRoot))
            return null;

        var relative = Path.GetRelativePath(localRoot, configPath);
        if (relative is "." or "")
            return null;

        // Windows accepts a path whose directory components differ from the
        // on-disk casing. The scanner reports the on-disk names and ignore
        // matching is case-sensitive, so derive the rule from the real casing
        // or the generated config could still be uploaded.
        if (OperatingSystem.IsWindows())
            relative = ResolveOnDiskCasing(localRoot, relative);

        if (Path.DirectorySeparatorChar != '/')
            relative = relative.Replace(Path.DirectorySeparatorChar, '/');

        return relative;
    }

    /// <summary>
    /// Rewrites each existing component of <paramref name="relative"/> to the
    /// name reported by the filesystem. Components that do not exist yet (the
    /// config file itself) keep the caller's spelling; an exact-case match is
    /// preferred so a case-sensitive Windows directory is not rewritten.
    /// </summary>
    private static string ResolveOnDiskCasing(string localRoot, string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = localRoot;

        for (var i = 0; i < parts.Length; i++)
        {
            var name = parts[i];
            if (name.Length == 0 || name is "." or "..")
            {
                current = Path.Combine(current, name);
                continue;
            }

            var candidate = Path.Combine(current, name);
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                try
                {
                    var names = new DirectoryInfo(current)
                        .EnumerateFileSystemInfos()
                        .Select(entry => entry.Name)
                        .ToList();

                    name = names.FirstOrDefault(n => string.Equals(n, name, StringComparison.Ordinal))
                           ?? names.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                           ?? name;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Cannot enumerate this directory; keep the caller's casing.
                    // Validation still runs against the resulting rule.
                }
            }

            parts[i] = name;
            current = Path.Combine(current, name);
        }

        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>
    /// Builds the ignore-file content written by init. An in-root config gets a
    /// root-anchored rule so a nested file with the same name stays included.
    /// </summary>
    internal static string BuildIgnoreText(string? configRelativePath)
    {
        var text = DefaultIgnoreText + Environment.NewLine;
        if (configRelativePath is null)
            return text;

        return text + EscapeIgnorePattern(configRelativePath) + Environment.NewLine;
    }

    /// <summary>
    /// Escapes a relative POSIX path for use as an anchored gitignore pattern.
    /// MAB treats [, *, and ? as glob syntax, so each literal occurrence is
    /// written as a bracket expression. The leading slash anchors the pattern
    /// to the ignore file's directory.
    /// </summary>
    internal static string EscapeIgnorePattern(string configRelativePath) =>
        "/" + configRelativePath
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("*", "[*]", StringComparison.Ordinal)
            .Replace("?", "[?]", StringComparison.Ordinal);

    /// <summary>
    /// Loads the staged ignore file and requires the generated rule to ignore
    /// the literal config path. MAB cannot represent every legal path; an
    /// unrepresentable path becomes exit 4 before the staged file is moved into
    /// place or any file is committed.
    /// </summary>
    private static void ValidateIgnoreText(string stagedIgnorePath, string? configRelativePath, string configPath)
    {
        if (configRelativePath is null)
            return;

        IgnoreRules rules;
        try
        {
            rules = IgnoreRules.Load(stagedIgnorePath);
            if (rules.Decide(configRelativePath, isDirectory: false).IsIgnored)
                return;
        }
        catch (IgnoreRulesException ex)
        {
            throw new ConfigException(
                $"init: cannot represent config path '{configPath}' in the generated ignore file: {ex.Message} " +
                "Move the config outside the local root or choose a different --config path.");
        }

        throw new ConfigException(
            $"init: generated ignore rule does not ignore config path '{configPath}'. " +
            "Move the config outside the local root or choose a different --config path.");
    }

    private static string Absolute(string value, string field) =>
        PathUtil.TrimEnd(Path.GetFullPath(PathUtil.Expand(value, field)));

    private static string NormalizeLocalRoot(string value)
    {
        try
        {
            var expanded = PathUtil.Expand(value.Trim(), "local root");
            return PathUtil.TrimEnd(Path.GetFullPath(expanded));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ConfigException($"init: invalid local root '{value}': {ex.Message}");
        }
    }

    /// <summary>A safe default derived from the directory name; the validator has the final say.</summary>
    internal static string DefaultSyncName(string localRoot)
    {
        string name;
        try
        {
            name = Path.GetFileName(PathUtil.TrimEnd(Path.GetFullPath(localRoot)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            name = string.Empty;
        }

        var sanitized = new string(name
            .Trim()
            .Select(character =>
                character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(character)
                    ? '-'
                    : character)
            .ToArray());

        if (string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or "..")
            return "sync";
        return sanitized.Length > 100 ? sanitized[..100] : sanitized;
    }

    private static string Serialize(RawConfig raw) =>
        YamlSerializer.Serialize(raw).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Prompt(TextReader input, TextWriter output, string label, string fallback)
    {
        output.Write($"{label} [{fallback}]: ");
        var line = input.ReadLine();
        if (line is null)
        {
            throw new ConfigException(
                "init: input closed before all prompts were answered; " +
                "rerun interactively or pass --yes for defaults.");
        }

        line = line.Trim();
        return line.Length == 0 ? fallback : line;
    }

    private static bool Confirm(TextReader input, TextWriter output, string question)
    {
        output.Write($"{question} [y/N]: ");
        var answer = input.ReadLine();
        if (answer is null)
        {
            output.WriteLine();
            return false;
        }

        answer = answer.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
