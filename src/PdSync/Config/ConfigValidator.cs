using PdSync.Proton;

namespace PdSync.Config;

/// <summary>
/// Turns raw YAML into a fully-resolved, validated config. All invariants from
/// SPEC §3.2 are enforced here; nothing downstream re-checks config shape.
/// </summary>
public static class ConfigValidator
{
    private static readonly Dictionary<string, SyncDirection> DirectionNames = new(StringComparer.Ordinal)
    {
        ["two-way"] = SyncDirection.TwoWay,
        ["push"] = SyncDirection.Push,
        ["pull"] = SyncDirection.Pull,
    };

    private static readonly Dictionary<string, ConflictPolicy> ConflictNames = new(StringComparer.Ordinal)
    {
        ["newer-wins"] = ConflictPolicy.NewerWins,
        ["keep-both"] = ConflictPolicy.KeepBoth,
        ["local-wins"] = ConflictPolicy.LocalWins,
        ["remote-wins"] = ConflictPolicy.RemoteWins,
        ["ask"] = ConflictPolicy.Ask,
    };

    private static readonly Dictionary<string, LocalDeletePolicy> LocalDeleteNames = new(StringComparer.Ordinal)
    {
        ["remote-trash"] = LocalDeletePolicy.RemoteTrash,
        ["keep"] = LocalDeletePolicy.Keep,
    };

    private static readonly Dictionary<string, RemoteDeletePolicy> RemoteDeleteNames = new(StringComparer.Ordinal)
    {
        ["local-trash"] = RemoteDeletePolicy.LocalTrash,
        ["keep"] = RemoteDeletePolicy.Keep,
    };

    private static readonly Dictionary<string, InitialSyncPolicy> InitialSyncNames = new(StringComparer.Ordinal)
    {
        ["adopt"] = InitialSyncPolicy.Adopt,
        ["keep-both"] = InitialSyncPolicy.KeepBoth,
        ["local-wins"] = InitialSyncPolicy.LocalWins,
        ["remote-wins"] = InitialSyncPolicy.RemoteWins,
    };

    private static readonly Dictionary<string, IgnoreCasePolicy> IgnoreCaseNames = new(StringComparer.Ordinal)
    {
        ["sensitive"] = IgnoreCasePolicy.Sensitive,
        ["insensitive"] = IgnoreCasePolicy.Insensitive,
    };

    private static readonly Dictionary<string, VerifyMode> VerifyModeNames = new(StringComparer.Ordinal)
    {
        ["hash"] = VerifyMode.Hash,
        ["size"] = VerifyMode.Size,
        ["sample"] = VerifyMode.Sample,
        ["full"] = VerifyMode.Full,
    };

    public static PdsyncConfig Validate(RawConfig raw, string configPath)
    {
        var errors = new List<string>();
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Environment.CurrentDirectory;

        var version = raw.Version ?? 1;
        if (raw.Version is null)
            errors.Add("version: required (expected 1)");
        else if (raw.Version != 1)
            errors.Add($"version: unsupported value {raw.Version}; expected 1");

        var proton = ResolveProton(raw.Proton, configDirectory, errors);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultStateDir = Path.Combine(localAppData, "pdsync");

        var stateDir = ResolveDirectory(raw.StateDir, defaultStateDir, "stateDir", configDirectory, errors);
        var logDir = ResolveDirectory(raw.LogDir, Path.Combine(stateDir, "logs"), "logDir", configDirectory, errors);

        var defaults = ResolveDefaults(raw.Defaults, errors);
        var syncs = ResolveSyncs(raw.Syncs, defaults, raw.Defaults?.IgnoreFile, configDirectory, errors);

        if (raw.Syncs is null || raw.Syncs.Count == 0)
            errors.Add("syncs: at least one sync is required");

        ValidateOverlaps(syncs, stateDir, logDir, errors);

        if (errors.Count > 0)
            throw new ConfigException(errors.Distinct(StringComparer.Ordinal));

        return new PdsyncConfig(version, Path.GetFullPath(configPath), proton, stateDir, logDir, defaults, syncs);
    }

    private static ProtonOptions ResolveProton(RawProton? raw, string configDirectory, List<string> errors)
    {
        var cli = "proton-drive";
        if (!string.IsNullOrWhiteSpace(raw?.Cli))
        {
            try
            {
                var expanded = PathUtil.Expand(raw.Cli.Trim(), "proton.cli");
                cli = LooksLikePath(expanded)
                    ? PathUtil.ToAbsolute(expanded, configDirectory, "proton.cli")
                    : expanded;
            }
            catch (ConfigException ex)
            {
                errors.AddRange(ex.Errors);
                cli = raw.Cli.Trim();
            }
        }

        // The CLI version this project is built and live-tested against. The
        // value stays config-overridable, including raising it.
        var minVersion = "0.9.0";
        if (!string.IsNullOrWhiteSpace(raw?.MinVersion))
        {
            minVersion = raw.MinVersion.Trim();
            if (!CliVersion.TryParseStrict(minVersion, out _))
            {
                errors.Add(
                    $"proton.minVersion: invalid version '{minVersion}' (expected major.minor.patch, e.g. 0.9.0)");
            }
        }

        return new ProtonOptions(cli, minVersion);
    }

    private static ResolvedOptions ResolveDefaults(RawDefaults? raw, List<string> errors)
    {
        raw ??= new RawDefaults();

        var direction = ParseEnum(raw.Direction, "defaults.direction", SyncDirection.TwoWay, DirectionNames, errors);
        var conflict = ParseEnum(raw.Conflict, "defaults.conflict", ConflictPolicy.NewerWins, ConflictNames, errors);
        var onLocalDelete = ParseEnum(raw.OnLocalDelete, "defaults.onLocalDelete", LocalDeletePolicy.RemoteTrash, LocalDeleteNames, errors);
        var onRemoteDelete = ParseEnum(raw.OnRemoteDelete, "defaults.onRemoteDelete", RemoteDeletePolicy.LocalTrash, RemoteDeleteNames, errors);
        var initialSync = ParseEnum(raw.InitialSync, "defaults.initialSync", InitialSyncPolicy.Adopt, InitialSyncNames, errors);
        var ignoreCase = ParseEnum(raw.IgnoreCase, "defaults.ignoreCase", IgnoreCasePolicy.Sensitive, IgnoreCaseNames, errors);

        var settleSeconds = ParseInt(raw.SettleSeconds, "defaults.settleSeconds", 3, errors, min: 0);
        var mtimeToleranceMs = ParseInt(raw.MtimeToleranceMs, "defaults.mtimeToleranceMs", 2000, errors, min: 0);
        var fastFullEveryRuns = ParseInt(raw.FastFullEveryRuns, "defaults.fastFullEveryRuns", 20, errors, min: 1);

        var safety = new SafetyOptions(
            ParseInt(raw.Safety?.MaxDeletes, "defaults.safety.maxDeletes", 200, errors, min: 0),
            ParseInt(raw.Safety?.MaxDeletePercent, "defaults.safety.maxDeletePercent", 10, errors, min: 0, max: 100));

        var verify = ResolveVerify(raw.Verify, "defaults.verify", errors);
        var localTrash = ResolveLocalTrash(raw.LocalTrash, "defaults.localTrash", errors);

        return new ResolvedOptions(
            direction,
            conflict,
            onLocalDelete,
            onRemoteDelete,
            initialSync,
            settleSeconds,
            mtimeToleranceMs,
            raw.DetectRenames ?? true,
            ignoreCase,
            raw.PruneEmptyRemoteDirs ?? true,
            raw.Fast ?? false,
            fastFullEveryRuns,
            safety,
            verify,
            localTrash);
    }

    private static List<SyncConfig> ResolveSyncs(
        Dictionary<string, RawSync>? rawSyncs,
        ResolvedOptions defaults,
        string? defaultIgnoreFile,
        string configDirectory,
        List<string> errors)
    {
        var result = new List<SyncConfig>();
        if (rawSyncs is null)
            return result;

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawName, raw) in rawSyncs)
        {
            var name = rawName.Trim();
            var field = $"syncs.{name}";

            if (string.IsNullOrWhiteSpace(name))
                errors.Add("syncs: sync name is empty");
            else if (name is "." or ".." || name.Length > 100 ||
                     name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0 ||
                     name.Any(char.IsControl))
                errors.Add($"{field}: sync name is not a valid identifier");
            else if (!seenNames.Add(name))
                errors.Add($"{field}: duplicate sync name (names are case-insensitive)");

            var localRoot = ResolveLocalRoot(raw?.Local, field + ".local", errors);
            var remoteRoot = ResolveRemoteRoot(raw?.Remote, field + ".remote", errors);

            var ignoreFile = ResolveIgnoreFile(raw?.IgnoreFile ?? defaultIgnoreFile ?? ".pdignore",
                localRoot, configDirectory, field + ".ignoreFile", errors);

            var excludes = new List<string>();
            if (raw?.Exclude is not null)
            {
                foreach (var pattern in raw.Exclude)
                {
                    if (string.IsNullOrEmpty(pattern))
                        errors.Add($"{field}.exclude: empty pattern");
                    else
                        excludes.Add(pattern);
                }
            }

            var direction = ParseEnum(raw?.Direction, field + ".direction", defaults.Direction, DirectionNames, errors);
            var conflict = ParseEnum(raw?.Conflict, field + ".conflict", defaults.Conflict, ConflictNames, errors);

            // A per-sync verify block inherits unspecified fields from defaults.
            var verify = raw?.Verify is null
                ? defaults.Verify
                : new VerifyOptions(
                    ParseEnum(raw.Verify.Mode, field + ".verify.mode", defaults.Verify.Mode, VerifyModeNames, errors),
                    ParseInt(raw.Verify.SamplePercent, field + ".verify.samplePercent", defaults.Verify.SamplePercent, errors, 0, 100),
                    ParseInt(raw.Verify.MinSizeMb, field + ".verify.minSizeMb", defaults.Verify.MinSizeMb, errors, min: 0));

            var options = defaults with
            {
                Direction = direction,
                Conflict = conflict,
                Fast = raw?.Fast ?? defaults.Fast,
                Verify = verify,
            };

            if (localRoot is not null && remoteRoot is not null && !string.IsNullOrWhiteSpace(name))
            {
                result.Add(new SyncConfig(
                    name,
                    localRoot,
                    remoteRoot,
                    raw?.Enabled ?? true,
                    ignoreFile,
                    excludes,
                    options));
            }
        }

        return result;
    }

    private static VerifyOptions ResolveVerify(
        RawVerify? raw,
        string field,
        List<string> errors)
    {
        var mode = ParseEnum(raw?.Mode, field + ".mode", VerifyMode.Hash, VerifyModeNames, errors);
        var samplePercent = ParseInt(raw?.SamplePercent, field + ".samplePercent", 5, errors, 0, 100);
        var minSizeMb = ParseInt(raw?.MinSizeMb, field + ".minSizeMb", 10, errors, min: 0);
        return new VerifyOptions(mode, samplePercent, minSizeMb);
    }

    private static LocalTrashOptions ResolveLocalTrash(RawLocalTrash? raw, string field, List<string> errors)
    {
        if (raw?.Retention is null)
            return new LocalTrashOptions(null);

        var text = raw.Retention.ToString()?.Trim();
        if (string.Equals(text, "forever", StringComparison.Ordinal))
            return new LocalTrashOptions(null);

        if (int.TryParse(text, out var days) && days > 0)
            return new LocalTrashOptions(days);

        errors.Add($"{field}.retention: expected 'forever' or a positive number of days");
        return new LocalTrashOptions(null);
    }

    private static string? ResolveLocalRoot(string? value, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{field}: required");
            return null;
        }

        try
        {
            var expanded = PathUtil.Expand(value.Trim(), field);
            if (!Path.IsPathFullyQualified(expanded))
            {
                errors.Add($"{field}: must be an absolute path");
                return null;
            }

            var full = PathUtil.TrimEnd(Path.GetFullPath(expanded));
            if (PathUtil.IsDriveOrFilesystemRoot(full))
            {
                errors.Add($"{field}: must not be a filesystem root");
                return null;
            }

            if (!Directory.Exists(full))
            {
                errors.Add($"{field}: directory does not exist: {full}");
                return null;
            }

            return full;
        }
        catch (ConfigException ex)
        {
            errors.AddRange(ex.Errors);
            return null;
        }
        catch (Exception ex)
        {
            errors.Add($"{field}: {ex.Message}");
            return null;
        }
    }

    private static string? ResolveRemoteRoot(string? value, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{field}: required");
            return null;
        }

        try
        {
            var expanded = PathUtil.ExpandVariables(value.Trim(), field);
            var normalized = RemotePath.Normalize(expanded);
            if (RemotePath.IsSectionRoot(normalized))
            {
                errors.Add($"{field}: must be a folder below a section root, not the section itself ({normalized})");
                return null;
            }

            if (!RemotePath.IsSyncable(normalized))
            {
                errors.Add($"{field}: must be under /my-files, /devices, /shared-by-me, or /shared-with-me");
                return null;
            }

            return normalized;
        }
        catch (FormatException ex)
        {
            errors.Add($"{field}: {ex.Message}");
            return null;
        }
    }

    private static string ResolveIgnoreFile(
        string? value,
        string? localRoot,
        string configDirectory,
        string field,
        List<string> errors)
    {
        var text = string.IsNullOrWhiteSpace(value) ? ".pdignore" : value.Trim();
        try
        {
            var expanded = PathUtil.Expand(text, field);
            var baseDirectory = localRoot ?? configDirectory;
            var result = Path.IsPathFullyQualified(expanded)
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(Path.Combine(baseDirectory, expanded));

            return PathUtil.TrimEnd(result);
        }
        catch (ConfigException ex)
        {
            errors.AddRange(ex.Errors);
            return Path.Combine(localRoot ?? configDirectory, ".pdignore");
        }
        catch (Exception ex)
        {
            errors.Add($"{field}: {ex.Message}");
            return Path.Combine(localRoot ?? configDirectory, ".pdignore");
        }
    }

    private static void ValidateOverlaps(
        List<SyncConfig> syncs,
        string stateDir,
        string logDir,
        List<string> errors)
    {
        for (var i = 0; i < syncs.Count; i++)
        {
            var a = syncs[i];
            if (PathUtil.IsSameOrUnder(stateDir, a.LocalRoot))
                errors.Add($"stateDir: must not be inside local root of sync '{a.Name}' ({a.LocalRoot})");
            if (PathUtil.IsSameOrUnder(logDir, a.LocalRoot))
                errors.Add($"logDir: must not be inside local root of sync '{a.Name}' ({a.LocalRoot})");

            for (var j = i + 1; j < syncs.Count; j++)
            {
                var b = syncs[j];

                // Compare folded identities: pdsync never syncs case (D14), so two
                // roots that differ only by case would share state entries.
                var aLocalKey = PathUtil.IdentityKey(a.LocalRoot);
                var bLocalKey = PathUtil.IdentityKey(b.LocalRoot);
                if (PathUtil.IsSameOrUnder(aLocalKey, bLocalKey) || PathUtil.IsSameOrUnder(bLocalKey, aLocalKey))
                    errors.Add($"syncs.{a.Name}.local and syncs.{b.Name}.local overlap: {a.LocalRoot} / {b.LocalRoot}");

                if (RemotePath.IsSameOrUnder(a.RemoteRoot, b.RemoteRoot) || RemotePath.IsSameOrUnder(b.RemoteRoot, a.RemoteRoot))
                    errors.Add($"syncs.{a.Name}.remote and syncs.{b.Name}.remote overlap: {a.RemoteRoot} / {b.RemoteRoot}");
            }
        }
    }

    private static T ParseEnum<T>(
        string? value,
        string field,
        T fallback,
        IReadOnlyDictionary<string, T> names,
        List<string> errors)
        where T : struct
    {
        if (value is null)
            return fallback;

        if (names.TryGetValue(value.Trim(), out var parsed))
            return parsed;

        errors.Add($"{field}: unknown value '{value}'. Allowed: {string.Join(", ", names.Keys)}");
        return fallback;
    }

    private static int ParseInt(
        int? value,
        string field,
        int fallback,
        List<string> errors,
        int min = int.MinValue,
        int max = int.MaxValue)
    {
        if (value is null)
            return fallback;

        if (value < min || value > max)
        {
            errors.Add($"{field}: must be between {min} and {max} (got {value})");
            return fallback;
        }

        return value.Value;
    }

    private static string ResolveDirectory(
        string? value,
        string fallback,
        string field,
        string configDirectory,
        List<string> errors)
    {
        if (value is null)
            return fallback;

        try
        {
            return PathUtil.ToAbsolute(value, configDirectory, field);
        }
        catch (ConfigException ex)
        {
            errors.AddRange(ex.Errors);
            return fallback;
        }
    }

    private static bool LooksLikePath(string value) =>
        value.Contains('/') || value.Contains('\\') || Path.IsPathFullyQualified(value) ||
        value.StartsWith(".", StringComparison.Ordinal);
}
