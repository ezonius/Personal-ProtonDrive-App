using FluentAssertions;
using PdSync.Config;
using PdSync.Proton;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PdSync.Tests;

public sealed class ConfigTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private PdsyncConfig Load(string yaml) => ConfigTestHelper.Load(_temp, yaml);

    [Fact]
    public void MinimalConfig_AppliesDefaults()
    {
        var config = Load(ConfigTestHelper.MinimalYaml(_temp));

        config.Version.Should().Be(1);
        config.Syncs.Should().ContainSingle();
        var sync = config.Syncs[0];
        sync.Name.Should().Be("docs");
        sync.RemoteRoot.Should().Be("/my-files/docs");
        sync.Enabled.Should().BeTrue();
        sync.Exclude.Should().BeEmpty();
        sync.IgnoreFile.Should().Be(_temp.Combine("local", ".pdignore"));

        sync.Options.Direction.Should().Be(SyncDirection.TwoWay);
        sync.Options.Conflict.Should().Be(ConflictPolicy.NewerWins);
        sync.Options.OnLocalDelete.Should().Be(LocalDeletePolicy.RemoteTrash);
        sync.Options.OnRemoteDelete.Should().Be(RemoteDeletePolicy.LocalTrash);
        sync.Options.InitialSync.Should().Be(InitialSyncPolicy.Adopt);
        sync.Options.SettleSeconds.Should().Be(3);
        sync.Options.MtimeToleranceMs.Should().Be(2000);
        sync.Options.DetectRenames.Should().BeTrue();
        sync.Options.IgnoreCase.Should().Be(IgnoreCasePolicy.Sensitive);
        sync.Options.PruneEmptyRemoteDirs.Should().BeTrue();
        sync.Options.Fast.Should().BeFalse();
        sync.Options.FastFullEveryRuns.Should().Be(20);
        sync.Options.Safety.MaxDeletes.Should().Be(200);
        sync.Options.Safety.MaxDeletePercent.Should().Be(10);
        sync.Options.Verify.Mode.Should().Be(VerifyMode.Hash);
        sync.Options.Verify.SamplePercent.Should().Be(5);
        sync.Options.Verify.MinSizeMb.Should().Be(10);
        sync.Options.LocalTrash.IsForever.Should().BeTrue();

        config.Proton.Cli.Should().Be("proton-drive");
        config.Proton.MinVersion.Should().BeNull();
        config.LogDir.Should().Be(_temp.Combine("state", "logs"));
    }

    [Fact]
    public void MissingVersion_IsAnError()
    {
        var yaml = $"""
            stateDir: "{_temp.YamlPath("state")}"
            syncs:
              docs:
                local: "{_temp.YamlPath("local")}"
                remote: "/my-files/docs"
            """;

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("version");
    }

    [Fact]
    public void UnknownTopLevelKey_IsAnError()
    {
        var yaml = ConfigTestHelper.MinimalYaml(_temp, extraRoot: "nope: 1");

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("nope");
    }

    [Fact]
    public void UnknownSyncKey_IsAnError()
    {
        var yaml = ConfigTestHelper.MinimalYaml(_temp, extraSync: "    surprise: true");

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("surprise");
    }

    [Fact]
    public void UnknownEnumValue_IsAnErrorWithAllowedValues()
    {
        var yaml = ConfigTestHelper.MinimalYaml(_temp, extraDefaults: "defaults:\n  direction: sideways");

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("defaults.direction").And.Contain("sideways").And.Contain("two-way");
    }

    [Fact]
    public void DuplicateKey_IsAnError()
    {
        var yaml = $"""
            version: 1
            version: 2
            stateDir: "{_temp.YamlPath("state")}"
            syncs:
              docs:
                local: "{_temp.YamlPath("local")}"
                remote: "/my-files/docs"
            """;

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().ContainEquivalentOf("duplicate");
    }

    [Fact]
    public void EnvironmentVariables_AreExpanded()
    {
        var local = _temp.Combine("local");
        var state = _temp.Combine("state");
        Directory.CreateDirectory(local);
        Environment.SetEnvironmentVariable("PDSYNC_TEST_ROOT", local.Replace('\\', '/'));
        Environment.SetEnvironmentVariable("PDSYNC_TEST_STATE", state.Replace('\\', '/'));
        try
        {
            var yaml =
                "version: 1\n" +
                "stateDir: \"${PDSYNC_TEST_STATE}\"\n" +
                "syncs:\n" +
                "  docs:\n" +
                "    local: \"%PDSYNC_TEST_ROOT%\"\n" +
                "    remote: \"/my-files/docs\"\n";

            var config = Load(yaml);
            config.StateDir.Should().Be(state);
            config.Syncs[0].LocalRoot.Should().Be(local);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDSYNC_TEST_ROOT", null);
            Environment.SetEnvironmentVariable("PDSYNC_TEST_STATE", null);
        }
    }

    [Fact]
    public void UnsetEnvironmentVariable_IsAnError()
    {
        var yaml = ConfigTestHelper.MinimalYaml(
            _temp,
            local: "%PDSYNC_TEST_UNSET%");

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("PDSYNC_TEST_UNSET");
    }

    [Fact]
    public void RelativeLocalRoot_IsAnError()
    {
        var yaml = ConfigTestHelper.MinimalYaml(_temp, local: "relative/folder");

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("absolute");
    }

    [Fact]
    public void MissingLocalRoot_IsAnError()
    {
        var yaml = ConfigTestHelper.MinimalYaml(_temp, local: _temp.Combine("missing"));

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("does not exist");
    }

    [Fact]
    public void FilesystemRootAsLocal_IsAnError()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var yaml = ConfigTestHelper.MinimalYaml(_temp, local: "C:\\");

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("filesystem root");
    }

    [Fact]
    public void SectionRootRemote_IsAnError()
    {
        var yaml = ConfigTestHelper.MinimalYaml(_temp, remote: "/my-files");

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("section");
    }

    [Fact]
    public void NonSyncableRemote_IsAnError()
    {
        var yaml = ConfigTestHelper.MinimalYaml(_temp, remote: "/trash/docs");

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("/my-files");
    }

    [Fact]
    public void RemoteEnvironmentVariables_AreExpanded()
    {
        Environment.SetEnvironmentVariable("PDSYNC_TEST_REMOTE", "docs");
        try
        {
            var yaml = ConfigTestHelper.MinimalYaml(_temp, remote: "/my-files/${PDSYNC_TEST_REMOTE}");
            var config = Load(yaml);
            config.Syncs[0].RemoteRoot.Should().Be("/my-files/docs");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDSYNC_TEST_REMOTE", null);
        }
    }

    [Fact]
    public void TrailingRemoteSlash_IsNormalized()
    {
        var config = Load(ConfigTestHelper.MinimalYaml(_temp, remote: "/my-files/docs/"));

        config.Syncs[0].RemoteRoot.Should().Be("/my-files/docs");
    }

    [Fact]
    public void RemoteDotDotSegment_IsAnError()
    {
        var yaml = ConfigTestHelper.MinimalYaml(_temp, remote: "/my-files/docs/../other");

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("'..'");
    }

    [Fact]
    public void OverlappingLocalRoots_AreAnError()
    {
        var nested = _temp.Combine("local", "nested");
        Directory.CreateDirectory(nested);

        var yaml = $"""
            version: 1
            stateDir: "{_temp.YamlPath("state")}"
            syncs:
              a:
                local: "{_temp.YamlPath("local")}"
                remote: "/my-files/a"
              b:
                local: "{nested.Replace('\\', '/')}"
                remote: "/my-files/b"
            """;

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("overlap");
    }

    [Fact]
    public void OverlappingRemoteRoots_AreAnError()
    {
        var yaml = $"""
            version: 1
            stateDir: "{_temp.YamlPath("state")}"
            syncs:
              a:
                local: "{_temp.YamlPath("local")}"
                remote: "/my-files/docs"
              b:
                local: "{_temp.YamlPath("local2")}"
                remote: "/my-files/docs/sub"
            """;
        Directory.CreateDirectory(_temp.Combine("local"));
        Directory.CreateDirectory(_temp.Combine("local2"));

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("overlap");
    }

    [Fact]
    public void LocalRootsDifferingOnlyByCase_AreAnError()
    {
        Directory.CreateDirectory(_temp.Combine("local"));
        Directory.CreateDirectory(_temp.Combine("LOCAL"));

        var yaml = $"""
            version: 1
            stateDir: "{_temp.YamlPath("state")}"
            syncs:
              a:
                local: "{_temp.YamlPath("local")}"
                remote: "/my-files/a"
              b:
                local: "{_temp.YamlPath("LOCAL")}"
                remote: "/my-files/b"
            """;

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("overlap");
    }

    [Fact]
    public void RemoteOverlapIsCaseFolded()
    {
        var yaml = $"""
            version: 1
            stateDir: "{_temp.YamlPath("state")}"
            syncs:
              a:
                local: "{_temp.YamlPath("local")}"
                remote: "/my-files/Docs"
              b:
                local: "{_temp.YamlPath("local2")}"
                remote: "/my-files/docs"
            """;
        Directory.CreateDirectory(_temp.Combine("local"));
        Directory.CreateDirectory(_temp.Combine("local2"));

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("overlap");
    }

    [Fact]
    public void StateDirInsideLocalRoot_IsAnError()
    {
        Directory.CreateDirectory(_temp.Combine("local"));

        var yaml = $"""
            version: 1
            stateDir: "{_temp.YamlPath("local", "state")}"
            syncs:
              docs:
                local: "{_temp.YamlPath("local")}"
                remote: "/my-files/docs"
            """;

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("stateDir");
    }

    [Fact]
    public void PerSyncOverrides_InheritUnspecifiedValues()
    {
        var yaml = ConfigTestHelper.MinimalYaml(
            _temp,
            extraDefaults: """
                defaults:
                  verify:
                    mode: size
                    samplePercent: 25
                    minSizeMb: 42
                """,
            extraSync: """
                    direction: push
                    fast: true
                    verify:
                      mode: sample
                      samplePercent: 10
                """);

        var config = Load(yaml);
        var options = config.Syncs[0].Options;
        options.Direction.Should().Be(SyncDirection.Push);
        options.Fast.Should().BeTrue();
        options.Verify.Mode.Should().Be(VerifyMode.Sample);
        options.Verify.SamplePercent.Should().Be(10);
        options.Verify.MinSizeMb.Should().Be(42);
    }

    [Fact]
    public void IgnoreFile_IsRelativeToLocalRoot()
    {
        var yaml = ConfigTestHelper.MinimalYaml(
            _temp,
            extraSync: "    ignoreFile: custom.ignore");

        var config = Load(yaml);
        config.Syncs[0].IgnoreFile.Should().Be(_temp.Combine("local", "custom.ignore"));
    }

    [Fact]
    public void ExcludePatterns_ArePreserved()
    {
        var yaml = ConfigTestHelper.MinimalYaml(
            _temp,
            extraSync: """
                    exclude:
                      - "*.tmp"
                      - "**/.cache/"
                """);

        var config = Load(yaml);
        config.Syncs[0].Exclude.Should().Equal("*.tmp", "**/.cache/");
    }

    [Fact]
    public void DisabledSync_IsLoadedButNotEnabled()
    {
        var yaml = ConfigTestHelper.MinimalYaml(
            _temp,
            extraSync: "    enabled: false");

        var config = Load(yaml);
        config.Syncs[0].Enabled.Should().BeFalse();
    }

    [Fact]
    public void SyncNameIsTrimmed()
    {
        var yaml = ConfigTestHelper.MinimalYaml(_temp, syncName: "\" docs \"");

        var config = Load(yaml);
        config.Syncs[0].Name.Should().Be("docs");
    }

    [Fact]
    public void RetentionDays_AreParsed()
    {
        var yaml = ConfigTestHelper.MinimalYaml(
            _temp,
            extraDefaults: """
                defaults:
                  localTrash:
                    retention: 30
                """);

        var config = Load(yaml);
        config.Defaults.LocalTrash.IsForever.Should().BeFalse();
        config.Defaults.LocalTrash.RetentionDays.Should().Be(30);
        config.Syncs[0].Options.LocalTrash.RetentionDays.Should().Be(30);
    }

    [Fact]
    public void InvalidRetention_IsAnError()
    {
        var yaml = ConfigTestHelper.MinimalYaml(
            _temp,
            extraDefaults: """
                defaults:
                  localTrash:
                    retention: soon
                """);

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("retention");
    }

    [Fact]
    public void OutOfRangeNumbers_AreErrors()
    {
        var yaml = ConfigTestHelper.MinimalYaml(
            _temp,
            extraDefaults: """
                defaults:
                  settleSeconds: -1
                  fastFullEveryRuns: 0
                  safety:
                    maxDeletePercent: 101
                """);

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("settleSeconds")
            .And.Contain("fastFullEveryRuns")
            .And.Contain("maxDeletePercent");
    }

    [Fact]
    public void NoSyncs_IsAnError()
    {
        var yaml = $"version: 1\nstateDir: \"{_temp.YamlPath("state")}\"\nsyncs: {{}}\n";

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("at least one sync");
    }

    [Fact]
    public void PathKey_FoldsCaseAndUnicode()
    {
        var nfc = "Caf\u00E9.TXT";
        var nfd = "Cafe\u0301.txt";

        PathKey.Fold(nfd).Should().Be(PathKey.Fold(nfc));
        PathKey.Fold(nfc).Should().Be("caf\u00E9.txt");
    }

    [Fact]
    public void Discover_UsesExplicitPath()
    {
        var path = _temp.Write("custom.yaml", "version: 1\nsyncs: {}\n");
        ConfigLoader.Discover(path).Should().Be(path);
    }

    [Fact]
    public void Discover_UsesEnvironmentVariable()
    {
        var path = _temp.Write("env.yaml", "version: 1\nsyncs: {}\n");
        var previous = Environment.GetEnvironmentVariable(ConfigLoader.ConfigEnvironmentVariable);
        Environment.SetEnvironmentVariable(ConfigLoader.ConfigEnvironmentVariable, path);
        try
        {
            ConfigLoader.Discover().Should().Be(path);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigLoader.ConfigEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Discover_UsesCurrentDirectory()
    {
        var previousDirectory = Environment.CurrentDirectory;
        var previousConfig = Environment.GetEnvironmentVariable(ConfigLoader.ConfigEnvironmentVariable);
        Environment.SetEnvironmentVariable(ConfigLoader.ConfigEnvironmentVariable, null);
        try
        {
            Directory.SetCurrentDirectory(_temp.Root);
            var path = _temp.Write("pdsync.yaml", "version: 1\nsyncs: {}\n");
            ConfigLoader.Discover().Should().Be(path);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            Environment.SetEnvironmentVariable(ConfigLoader.ConfigEnvironmentVariable, previousConfig);
        }
    }

    [Fact]
    public void Discover_MissingFile_IsAnError()
    {
        var act = () => ConfigLoader.Discover(_temp.Combine("nope.yaml"));
        act.Should().Throw<ConfigException>().WithMessage("*not found*");
    }

    [Fact]
    public void UnknownDefaultsKey_IsAnError()
    {
        var yaml = ConfigTestHelper.MinimalYaml(
            _temp,
            extraDefaults: "defaults:\n  nope: 1");

        var ex = Assert.Throws<ConfigException>(() => Load(yaml));
        ex.Message.Should().Contain("nope");
    }

    [Fact]
    public void PathUtil_IsSameOrUnder_RespectsSegmentBoundaries()
    {
        var root = _temp.Combine("a");
        PathUtil.IsSameOrUnder(_temp.Combine("a", "b"), root).Should().BeTrue();
        PathUtil.IsSameOrUnder(root, root).Should().BeTrue();
        PathUtil.IsSameOrUnder(_temp.Combine("ab"), root).Should().BeFalse();
        PathUtil.IsSameOrUnder(root, _temp.Combine("a", "b")).Should().BeFalse();
    }

    [Fact]
    public void RemotePath_NormalizesAndValidates()
    {
        RemotePath.Normalize("/my-files/docs/").Should().Be("/my-files/docs");
        RemotePath.Normalize("//my-files//docs").Should().Be("/my-files/docs");
        RemotePath.IsSyncable("/my-files/docs").Should().BeTrue();
        RemotePath.IsSyncable("/photos/x").Should().BeFalse();

        Assert.Throws<FormatException>(() => RemotePath.Normalize("my-files/docs"));
        Assert.Throws<FormatException>(() => RemotePath.Normalize("/my-files/../etc"));
        Assert.Throws<FormatException>(() => RemotePath.Normalize("/my-files/a\\b"));
    }
}
