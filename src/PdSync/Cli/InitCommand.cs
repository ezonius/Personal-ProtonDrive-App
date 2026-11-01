using System.Text.Json;
using System.Text.Json.Serialization;
using PdSync.Config;
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
        TextWriter error)
    {
        // Keep stdout machine-readable under --json: prompts and the overwrite
        // question are diagnostics, not output.
        var promptOutput = json ? error : output;
        var configPath = ResolveTargetPath(explicitConfigPath);
        if (File.Exists(configPath))
        {
            throw new ConfigException(
                $"init: config file already exists: {configPath}; refusing to overwrite. " +
                "Pass --config <other-path> or remove it first.");
        }

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
        var validated = ConfigValidator.Validate(ConfigLoader.Deserialize(yaml), configPath);
        var sync = validated.Syncs[0];

        var ignoreFile = sync.IgnoreFile;
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

        try
        {
            var configDirectory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(configDirectory))
                Directory.CreateDirectory(configDirectory);

            File.WriteAllText(configPath, yaml);
            File.WriteAllText(ignoreFile, DefaultIgnoreText + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
    private static string ResolveTargetPath(string? explicitConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitConfigPath))
            return Absolute(explicitConfigPath, "--config");

        var fromEnvironment = Environment.GetEnvironmentVariable(ConfigLoader.ConfigEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Absolute(fromEnvironment, ConfigLoader.ConfigEnvironmentVariable);

        return ConfigLoader.FindExisting()
               ?? Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "pdsync.yaml"));
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
            output.WriteLine();
            return fallback;
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
