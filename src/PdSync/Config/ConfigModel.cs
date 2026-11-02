namespace PdSync.Config;

// ---------------------------------------------------------------------------
// Raw YAML DTOs. Every value is nullable so the validator can distinguish
// "not specified" from an explicit zero/false, and so defaults live in one
// place. YamlDotNet is configured to reject unknown properties.
// ---------------------------------------------------------------------------

public sealed class RawConfig
{
    public int? Version { get; set; }
    public RawProton? Proton { get; set; }
    public string? StateDir { get; set; }
    public string? LogDir { get; set; }
    public RawDefaults? Defaults { get; set; }
    public Dictionary<string, RawSync>? Syncs { get; set; }
}

public sealed class RawProton
{
    public string? Cli { get; set; }
    public string? MinVersion { get; set; }
}

public sealed class RawDefaults
{
    public string? Direction { get; set; }
    public string? Conflict { get; set; }
    public string? OnLocalDelete { get; set; }
    public string? OnRemoteDelete { get; set; }
    public string? InitialSync { get; set; }
    public string? IgnoreFile { get; set; }
    public int? SettleSeconds { get; set; }
    public int? MtimeToleranceMs { get; set; }
    public bool? DetectRenames { get; set; }
    public string? IgnoreCase { get; set; }
    public bool? PruneEmptyRemoteDirs { get; set; }
    public bool? Fast { get; set; }
    public int? FastFullEveryRuns { get; set; }
    public RawSafety? Safety { get; set; }
    public RawVerify? Verify { get; set; }
    public RawLocalTrash? LocalTrash { get; set; }
}

public sealed class RawSync
{
    public string? Local { get; set; }
    public string? Remote { get; set; }
    public bool? Enabled { get; set; }
    public string? Direction { get; set; }
    public string? Conflict { get; set; }
    public bool? Fast { get; set; }
    public List<string>? Exclude { get; set; }
    public string? IgnoreFile { get; set; }
    public RawVerify? Verify { get; set; }
}

public sealed class RawSafety
{
    public int? MaxDeletes { get; set; }
    public int? MaxDeletePercent { get; set; }
}

public sealed class RawVerify
{
    public string? Mode { get; set; }
    public int? SamplePercent { get; set; }
    public int? MinSizeMb { get; set; }
}

public sealed class RawLocalTrash
{
    public object? Retention { get; set; }
}

// ---------------------------------------------------------------------------
// Normalized model. Constructed only by ConfigValidator.
// ---------------------------------------------------------------------------

public enum SyncDirection
{
    TwoWay,
    Push,
    Pull,
}

public enum ConflictPolicy
{
    NewerWins,
    KeepBoth,
    LocalWins,
    RemoteWins,
    Ask,
}

public enum LocalDeletePolicy
{
    RemoteTrash,
    Keep,
}

public enum RemoteDeletePolicy
{
    LocalTrash,
    Keep,
}

public enum InitialSyncPolicy
{
    Adopt,
    KeepBoth,
    LocalWins,
    RemoteWins,
}

public enum IgnoreCasePolicy
{
    Sensitive,
    Insensitive,
}

public enum VerifyMode
{
    Hash,
    Size,
    Sample,
    Full,
}

public sealed record ProtonOptions(string Cli, string MinVersion);

public sealed record SafetyOptions(int MaxDeletes, int MaxDeletePercent)
{
    public bool IsUnlimited => MaxDeletes == 0 && MaxDeletePercent == 100;
}

public sealed record VerifyOptions(VerifyMode Mode, int SamplePercent, int MinSizeMb);

public sealed record LocalTrashOptions(int? RetentionDays)
{
    public bool IsForever => RetentionDays is null;
}

public sealed record ResolvedOptions(
    SyncDirection Direction,
    ConflictPolicy Conflict,
    LocalDeletePolicy OnLocalDelete,
    RemoteDeletePolicy OnRemoteDelete,
    InitialSyncPolicy InitialSync,
    int SettleSeconds,
    int MtimeToleranceMs,
    bool DetectRenames,
    IgnoreCasePolicy IgnoreCase,
    bool PruneEmptyRemoteDirs,
    bool Fast,
    int FastFullEveryRuns,
    SafetyOptions Safety,
    VerifyOptions Verify,
    LocalTrashOptions LocalTrash);

public sealed record SyncConfig(
    string Name,
    string LocalRoot,
    string RemoteRoot,
    bool Enabled,
    string IgnoreFile,
    IReadOnlyList<string> Exclude,
    ResolvedOptions Options);

public sealed record PdsyncConfig(
    int Version,
    string ConfigPath,
    ProtonOptions Proton,
    string StateDir,
    string LogDir,
    ResolvedOptions Defaults,
    IReadOnlyList<SyncConfig> Syncs);

public sealed class ConfigException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public ConfigException(string error)
        : this(new[] { error })
    {
    }

    public ConfigException(IEnumerable<string> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors.ToList();
    }
}
