using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PdSync.Config;

/// <summary>
/// Config discovery and YAML parsing. Semantic checks live in
/// <see cref="ConfigValidator"/>; this class only finds, reads, and deserializes.
/// </summary>
public static class ConfigLoader
{
    public const string ConfigEnvironmentVariable = "PDSYNC_CONFIG";

    public static string Discover(string? explicitPath = null) =>
        FindExisting(explicitPath) ?? throw new ConfigException(
            $"No config file found. Looked for ./pdsync.yaml, ./pdsync.yml, and {FallbackPath}; " +
            $"or pass --config / set {ConfigEnvironmentVariable}.");

    /// <summary>
    /// Applies the documented discovery order and returns the first existing
    /// config, or null when none exists. `init` uses the null case to decide
    /// where a new config belongs without creating one.
    /// </summary>
    public static string? FindExisting(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return RequireFile(PathUtil.Expand(explicitPath, "--config"));

        var fromEnv = Environment.GetEnvironmentVariable(ConfigEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return RequireFile(PathUtil.Expand(fromEnv, ConfigEnvironmentVariable));

        var cwdYaml = Path.Combine(Environment.CurrentDirectory, "pdsync.yaml");
        if (File.Exists(cwdYaml))
            return Path.GetFullPath(cwdYaml);

        var cwdYml = Path.Combine(Environment.CurrentDirectory, "pdsync.yml");
        if (File.Exists(cwdYml))
            return Path.GetFullPath(cwdYml);

        if (File.Exists(FallbackPath))
            return Path.GetFullPath(FallbackPath);

        return null;
    }

    private static string FallbackPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pdsync", "config.yaml");

    public static PdsyncConfig Load(string? explicitPath = null)
    {
        var path = Discover(explicitPath);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"Cannot read config '{path}': {ex.Message}");
        }

        RawConfig raw;
        try
        {
            raw = Deserialize(text);
        }
        catch (YamlException ex)
        {
            throw new ConfigException(
                $"Invalid YAML in '{path}' at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}");
        }

        return ConfigValidator.Validate(raw, path);
    }

    public static RawConfig Deserialize(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        return deserializer.Deserialize<RawConfig>(yaml) ?? new RawConfig();
    }

    private static string RequireFile(string path)
    {
        var full = PathUtil.TrimEnd(Path.GetFullPath(path));
        if (!File.Exists(full))
            throw new ConfigException($"Config file not found: {full}");
        return full;
    }
}
