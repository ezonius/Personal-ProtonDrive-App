using System.Text.Json;
using FluentAssertions;
using PdSync.Cli;
using PdSync.Config;

namespace PdSync.Tests;

/// <summary>
/// `pdsync init` creates a config accepted by the real loader/validator and a
/// default `.pdignore`, without touching state, logs, or Proton. Tests run the
/// CLI in-process with an injected stdin so no console is required.
/// </summary>
public sealed class InitCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Yes_CreatesValidConfigAndIgnoreFile()
    {
        var local = _temp.Combine("photos");
        Directory.CreateDirectory(local);
        var configPath = _temp.Combine("cfg", "pdsync.yaml");

        var result = InDirectory(local, () => Cli("init", "--yes", "--config", configPath));

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stdout.Should().Contain("created config:");
        result.Stdout.Should().Contain(configPath);
        File.Exists(configPath).Should().BeTrue();
        File.Exists(Path.Combine(local, ".pdignore")).Should().BeTrue();

        var config = ConfigLoader.Load(configPath);
        config.Version.Should().Be(1);
        var sync = config.Syncs.Should().ContainSingle().Subject;
        sync.Name.Should().Be("photos");
        sync.LocalRoot.Should().Be(local);
        sync.RemoteRoot.Should().Be("/my-files/photos");
        sync.IgnoreFile.Should().Be(Path.Combine(local, ".pdignore"));
        sync.Options.Direction.Should().Be(SyncDirection.TwoWay);
        sync.Options.Conflict.Should().Be(ConflictPolicy.NewerWins);
        sync.Options.OnLocalDelete.Should().Be(LocalDeletePolicy.RemoteTrash);
        sync.Options.OnRemoteDelete.Should().Be(RemoteDeletePolicy.LocalTrash);
        sync.Options.InitialSync.Should().Be(InitialSyncPolicy.Adopt);

        File.ReadAllText(sync.IgnoreFile).Should().Contain("*.tmp");
        File.ReadAllText(sync.IgnoreFile).Should().Contain("*.log");

        // Init must not create state, logs, fast counters, or anything else.
        Directory.Exists(Path.Combine(local, "state")).Should().BeFalse();
        Directory.Exists(Path.Combine(local, "logs")).Should().BeFalse();
    }

    [Fact]
    public void Interactive_UsesScriptedAnswers()
    {
        var local = _temp.Combine("work");
        Directory.CreateDirectory(local);
        var configPath = _temp.Combine("interactive", "pdsync.yaml");

        var result = CliInput(
            $"{local}\nDocs\n/my-files/Documents\n",
            "init", "--config", configPath, "--json");

        result.ExitCode.Should().Be(0, result.Stderr);
        result.Stderr.Should().Contain("local root [");
        result.Stderr.Should().Contain("sync name [");

        var config = ConfigLoader.Load(configPath);
        var sync = config.Syncs.Should().ContainSingle().Subject;
        sync.Name.Should().Be("Docs");
        sync.LocalRoot.Should().Be(local);
        sync.RemoteRoot.Should().Be("/my-files/Documents");

        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        root.GetProperty("command").GetString().Should().Be("init");
        root.GetProperty("config").GetString().Should().Be(configPath);
        root.GetProperty("sync").GetString().Should().Be("Docs");
        root.GetProperty("local").GetString().Should().Be(local);
        root.GetProperty("remote").GetString().Should().Be("/my-files/Documents");
        root.GetProperty("ignoreFileOverwritten").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Interactive_EmptyAnswers_AcceptTheDefaults()
    {
        var local = _temp.Combine("defaults");
        Directory.CreateDirectory(local);
        var configPath = _temp.Combine("defaults.yaml");

        var result = InDirectory(local, () => CliInput("\n\n\n", "init", "--config", configPath));

        result.ExitCode.Should().Be(0, result.Stderr);
        var sync = ConfigLoader.Load(configPath).Syncs.Should().ContainSingle().Subject;
        sync.Name.Should().Be("defaults");
        sync.LocalRoot.Should().Be(local);
        sync.RemoteRoot.Should().Be("/my-files/defaults");
    }

    [Fact]
    public void ExistingConfig_RefusesOverwriteWithConfigError()
    {
        var local = _temp.Combine("docs");
        Directory.CreateDirectory(local);
        var configPath = _temp.Write("pdsync.yaml", "version: 1\n# sentinel\n");

        var result = InDirectory(local, () => Cli("init", "--yes", "--config", configPath));

        result.ExitCode.Should().Be(4);
        result.Stderr.Should().Contain(configPath);
        result.Stderr.Should().Contain("refusing to overwrite");
        result.Stdout.Should().BeEmpty();
        File.ReadAllText(configPath).Should().Be("version: 1\n# sentinel\n");
        File.Exists(Path.Combine(local, ".pdignore")).Should().BeFalse();
    }

    [Fact]
    public void ExistingIgnoreFile_WithYes_RefusesWithoutWritingConfig()
    {
        var local = _temp.Combine("docs");
        Directory.CreateDirectory(local);
        var ignoreFile = _temp.Write("docs/.pdignore", "# sentinel\n");
        var configPath = _temp.Combine("config", "pdsync.yaml");

        var result = InDirectory(local, () => Cli("init", "--yes", "--config", configPath));

        result.ExitCode.Should().Be(4);
        result.Stderr.Should().Contain(ignoreFile);
        result.Stderr.Should().Contain("refusing to overwrite");
        File.Exists(configPath).Should().BeFalse();
        File.ReadAllText(ignoreFile).Should().Be("# sentinel\n");
    }

    [Fact]
    public void ExistingIgnoreFile_InteractiveDecline_Refuses()
    {
        var local = _temp.Combine("docs");
        Directory.CreateDirectory(local);
        var ignoreFile = _temp.Write("docs/.pdignore", "# sentinel\n");
        var configPath = _temp.Combine("config", "pdsync.yaml");

        var result = CliInput(
            $"{local}\ndocs\n/my-files/docs\nn\n",
            "init", "--config", configPath);

        result.ExitCode.Should().Be(4);
        result.Stdout.Should().Contain("Overwrite it?");
        result.Stderr.Should().Contain("refusing to overwrite");
        File.Exists(configPath).Should().BeFalse();
        File.ReadAllText(ignoreFile).Should().Be("# sentinel\n");
    }

    [Fact]
    public void ExistingIgnoreFile_InteractiveYes_OverwritesAndCreatesConfig()
    {
        var local = _temp.Combine("docs");
        Directory.CreateDirectory(local);
        var ignoreFile = _temp.Write("docs/.pdignore", "# sentinel\n");
        var configPath = _temp.Combine("config", "pdsync.yaml");

        var result = CliInput(
            $"{local}\ndocs\n/my-files/docs\nyes\n",
            "init", "--config", configPath);

        result.ExitCode.Should().Be(0, result.Stderr);
        File.Exists(configPath).Should().BeTrue();
        File.ReadAllText(ignoreFile).Should().Contain("*.tmp");
        File.ReadAllText(ignoreFile).Should().NotContain("# sentinel");
        result.Stdout.Should().Contain("updated ignore file:");
    }

    [Fact]
    public void InvalidSyncName_IsRejectedWithoutPartialFiles()
    {
        var local = _temp.Combine("docs");
        Directory.CreateDirectory(local);
        var configPath = _temp.Combine("config", "pdsync.yaml");

        var result = CliInput(
            $"{local}\nbad/name\n/my-files/docs\n",
            "init", "--config", configPath);

        result.ExitCode.Should().Be(4);
        result.Stderr.Should().Contain("sync name is not a valid identifier");
        File.Exists(configPath).Should().BeFalse();
        File.Exists(Path.Combine(local, ".pdignore")).Should().BeFalse();
    }

    [Fact]
    public void MissingLocalRoot_IsRejectedWithoutPartialFiles()
    {
        var missing = _temp.Combine("does-not-exist");
        var configPath = _temp.Combine("config", "pdsync.yaml");

        var result = CliInput(
            $"{missing}\ndocs\n/my-files/docs\n",
            "init", "--config", configPath);

        result.ExitCode.Should().Be(4);
        result.Stderr.Should().Contain("directory does not exist");
        File.Exists(configPath).Should().BeFalse();
    }

    [Fact]
    public void InvalidRemoteRoot_IsRejectedWithoutPartialFiles()
    {
        var local = _temp.Combine("docs");
        Directory.CreateDirectory(local);
        var configPath = _temp.Combine("config", "pdsync.yaml");

        var result = CliInput(
            $"{local}\ndocs\n/trash/docs\n",
            "init", "--config", configPath);

        result.ExitCode.Should().Be(4);
        result.Stderr.Should().Contain("must be under /my-files");
        File.Exists(configPath).Should().BeFalse();
    }

    [Fact]
    public void PdsyncConfigEnvironmentVariable_SelectsTheTargetPath()
    {
        var local = _temp.Combine("docs");
        Directory.CreateDirectory(local);
        var configPath = _temp.Combine("from-env", "pdsync.yaml");

        var result = WithEnvironment(
            ConfigLoader.ConfigEnvironmentVariable, configPath,
            () => InDirectory(local, () => Cli("init", "--yes")));

        result.ExitCode.Should().Be(0, result.Stderr);
        File.Exists(configPath).Should().BeTrue();
        File.Exists(Path.Combine(local, "pdsync.yaml")).Should().BeFalse();
        ConfigLoader.Load(configPath).Syncs.Should().ContainSingle();
    }

    [Fact]
    public void DiscoveredExistingConfig_IsNeverOverwritten()
    {
        var local = _temp.Combine("docs");
        Directory.CreateDirectory(local);
        var configPath = _temp.Write("pdsync.yaml", "version: 1\n# discovery sentinel\n");

        var result = WithEnvironment(
            ConfigLoader.ConfigEnvironmentVariable, null,
            () => InDirectory(_temp.Root, () => Cli("init", "--yes")));

        result.ExitCode.Should().Be(4);
        result.Stderr.Should().Contain(configPath);
        result.Stderr.Should().Contain("refusing to overwrite");
        File.ReadAllText(configPath).Should().Be("version: 1\n# discovery sentinel\n");
        File.Exists(Path.Combine(local, ".pdignore")).Should().BeFalse();
    }

    [Fact]
    public void NoDiscoveredConfig_CreatesPdsyncYamlInTheCurrentDirectory()
    {
        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "pdsync", "config.yaml");
        if (File.Exists(fallback))
            return; // A real user-level config would correctly be refused instead.

        var local = _temp.Combine("fresh");
        Directory.CreateDirectory(local);

        var result = WithEnvironment(
            ConfigLoader.ConfigEnvironmentVariable, null,
            () => InDirectory(local, () => Cli("init", "--yes")));

        result.ExitCode.Should().Be(0, result.Stderr);
        var configPath = Path.GetFullPath(Path.Combine(local, "pdsync.yaml"));
        File.Exists(configPath).Should().BeTrue();
        ConfigLoader.Load(configPath).Syncs.Should().ContainSingle();
        File.Exists(Path.Combine(local, ".pdignore")).Should().BeTrue();
    }

    private static CliResult Cli(params string[] args) => CliInput(null, args);

    private static CliResult CliInput(string? stdin, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliApp.Run(
            args, stdout, stderr, desktopMappings: null, input: new StringReader(stdin ?? string.Empty));
        return new CliResult(exit, stdout.ToString(), stderr.ToString());
    }

    private static T InDirectory<T>(string directory, Func<T> action)
    {
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = directory;
        try
        {
            return action();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    private static T WithEnvironment<T>(string name, string? value, Func<T> action)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
