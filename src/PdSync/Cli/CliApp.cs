using System.Text.Json;
using System.Text.Json.Serialization;
using PdSync.Config;
using PdSync.Diagnostics;
using PdSync.Exec;
using PdSync.Plan;
using PdSync.Proton;
using PdSync.Remote;
using PdSync.Scan;
using PdSync.State;

namespace PdSync.Cli;

/// <summary>
/// Small, dependency-free command surface. Keeping parsing here means the engine
/// and executor can be tested in-process without a console.
/// </summary>
public static class CliApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static int Run(
        string[] args,
        TextWriter output,
        TextWriter error,
        IDesktopMappingDetector? desktopMappings = null)
    {
        CliArguments parsed;
        try
        {
            parsed = CliArguments.Parse(args);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine("pdsync: " + ex.Message);
            return 1;
        }

        if (parsed.ShowHelp || parsed.Command is null)
        {
            PrintUsage(output);
            return parsed.ShowHelp ? 0 : 1;
        }

        if (parsed.Command is not ("plan" or "run" or "doctor" or "status"))
            return Unknown(parsed.Command, error);

        try
        {
            var config = ConfigLoader.Load(parsed.ConfigPath);
            return parsed.Command switch
            {
                "plan" => Plan(config, parsed, output, error),
                "run" => RunSync(config, parsed, output, error),
                "doctor" => Doctor(config, parsed, output, error, desktopMappings ?? new DesktopMappingDetector()),
                "status" => Status(config, parsed, output, error),
                _ => Unknown(parsed.Command, error),
            };
        }
        catch (ConfigException ex)
        {
            foreach (var item in ex.Errors)
                error.WriteLine("config: " + item);
            return 4;
        }
        catch (ProtonAuthException ex)
        {
            error.WriteLine("auth: " + ex.Message);
            return 3;
        }
        catch (SafetyException ex)
        {
            error.WriteLine("safety: " + ex.Message);
            return 6;
        }
        catch (LockException ex)
        {
            error.WriteLine("lock: " + ex.Message);
            return 5;
        }
        catch (ProtonCliException ex)
        {
            error.WriteLine("proton-drive: " + ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is SyncException or ScanException or StateException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine("pdsync: " + ex.Message);
            return 1;
        }
    }

    private static int Plan(PdsyncConfig config, CliArguments args, TextWriter output, TextWriter error)
    {
        var syncs = SelectSyncs(config, args.SyncNames);
        if (syncs.Count == 0)
        {
            WriteNothing(output, args.Json);
            return 0;
        }

        var results = new List<object>();
        var exit = 0;
        foreach (var sync in syncs)
        {
            try
            {
                using var prepared = SyncService.PrepareForPlan(
                    config, sync, args.Fast || sync.Options.Fast, args.Full, args.Interactive);
                var plan = prepared.Plan;

                if (!args.Json)
                    WritePlan(output, sync, plan, prepared.UsedFastListing, prepared.RunsSinceFull);

                results.Add(new
                {
                    sync = sync.Name,
                    fast = prepared.UsedFastListing,
                    runsSinceFull = prepared.RunsSinceFull,
                    considered = plan.Considered,
                    actions = plan.Actions.Select(ActionJson),
                    conflicts = plan.Conflicts,
                    skipped = plan.Skipped,
                    adoptions = plan.Adoptions,
                    stats = plan.Stats,
                });

                if (plan.HasBlockingConflicts && exit == 0)
                    exit = 2;
            }
            catch (ProtonAuthException)
            {
                throw;
            }
            catch (SafetyException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SyncException or ScanException or ProtonCliException or StateException)
            {
                error.WriteLine($"sync '{sync.Name}': {ex.Message}");
                if (exit == 0)
                    exit = 1;
            }
        }

        if (args.Json)
            WriteJson(output, new { command = "plan", syncs = results, exitCode = exit });
        return exit;
    }

    private static int RunSync(PdsyncConfig config, CliArguments args, TextWriter output, TextWriter error)
    {
        var syncs = SelectSyncs(config, args.SyncNames);
        if (syncs.Count == 0)
        {
            WriteNothing(output, args.Json);
            return 0;
        }

        var results = new List<object>();
        var exit = 0;
        foreach (var sync in syncs)
        {
            using var runLock = RunLock.Acquire(config, sync);
            try
            {
                var attempt = SyncService.Run(
                    config, sync, args.Fast || sync.Options.Fast, args.Full,
                    args.Force, args.Interactive, args.Verbose,
                    args.Json ? TextWriter.Null : output, error);

                if (!args.Json)
                    WriteRun(output, sync, attempt);

                results.Add(new
                {
                    sync = sync.Name,
                    fast = attempt.UsedFastListing,
                    runsSinceFull = attempt.RunsSinceFull,
                    warning = attempt.Warning,
                    conflicts = attempt.Plan.Conflicts,
                    stats = attempt.Plan.Stats,
                    succeeded = attempt.Report?.Succeeded ?? 0,
                    failed = attempt.Report?.Failed ?? 0,
                    skipped = attempt.Report?.Skipped ?? 0,
                    durationMs = attempt.DurationMs,
                    prunedRemoteDirectories = attempt.Report?.PrunedRemoteDirectories ?? Array.Empty<string>(),
                });

                if (attempt.Plan.HasBlockingConflicts)
                    exit = Math.Max(exit, 2);
                else if (attempt.Report?.Failed > 0)
                    exit = Math.Max(exit, 1);
            }
            catch (ProtonAuthException)
            {
                throw;
            }
            catch (SafetyException)
            {
                throw;
            }
            catch (LockException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SyncException or ScanException or ProtonCliException or StateException)
            {
                error.WriteLine($"sync '{sync.Name}': {ex.Message}");
                exit = Math.Max(exit, 1);
            }
        }

        if (args.Json)
            WriteJson(output, new { command = "run", syncs = results, exitCode = exit });
        return exit;
    }

    private static int Doctor(
        PdsyncConfig config,
        CliArguments args,
        TextWriter output,
        TextWriter error,
        IDesktopMappingDetector desktopMappings)
    {
        var checks = new List<DoctorCheck>();
        var failed = false;

        var mappingScan = desktopMappings.Detect();
        var mappingDetail = mappingScan.Diagnostic is not null
            ? mappingScan.Diagnostic
            : mappingScan.Mappings.Count == 0
                ? "no desktop-app mappings detected"
                : $"{mappingScan.Mappings.Count} mapping(s): {string.Join(", ", mappingScan.Mappings.Select(mapping => mapping.LocalRoot))}";
        checks.Add(new DoctorCheck("desktop-app mappings", mappingScan.Diagnostic is null, mappingDetail,
            Warning: mappingScan.Diagnostic is not null));

        checks.Add(new DoctorCheck("config", true, config.ConfigPath));
        checks.Add(new DoctorCheck("stateDir", Directory.Exists(config.StateDir),
            Directory.Exists(config.StateDir) ? config.StateDir : "missing (created by the first run)"));

        string version;
        try
        {
            version = new ProtonCli(config.Proton.Cli).GetVersion();
            checks.Add(new DoctorCheck("proton-drive", true, version));
        }
        catch (Exception ex)
        {
            version = "unavailable";
            failed = true;
            checks.Add(new DoctorCheck("proton-drive", false, ex.Message));
        }

        var cli = new ProtonCli(config.Proton.Cli);
        try
        {
            _ = cli.List("/");
            checks.Add(new DoctorCheck("auth", true, "authenticated"));
        }
        catch (ProtonAuthException ex)
        {
            failed = true;
            checks.Add(new DoctorCheck("auth", false, ex.Message));
        }
        catch (Exception ex)
        {
            failed = true;
            checks.Add(new DoctorCheck("auth", false, ex.Message));
        }

        foreach (var sync in config.Syncs)
        {
            var localOk = Directory.Exists(sync.LocalRoot);
            checks.Add(new DoctorCheck($"sync {sync.Name} local", localOk,
                localOk ? sync.LocalRoot : "missing local root"));
            if (!localOk)
                failed = true;

            try
            {
                var children = cli.List(sync.RemoteRoot);
                checks.Add(new DoctorCheck($"sync {sync.Name} remote", true,
                    $"{sync.RemoteRoot} ({children.Count} child{(children.Count == 1 ? "" : "en")})"));
            }
            catch (ProtonAuthException ex)
            {
                failed = true;
                checks.Add(new DoctorCheck($"sync {sync.Name} remote", false, ex.Message));
            }
            catch (Exception ex)
            {
                failed = true;
                checks.Add(new DoctorCheck($"sync {sync.Name} remote", false, ex.Message));
            }

            var overlap = DesktopMappingOverlapDetector.Find(sync.LocalRoot, mappingScan.Mappings);
            if (overlap is not null)
            {
                failed = true;
                checks.Add(new DoctorCheck($"sync {sync.Name} desktop-app overlap", false,
                    $"{DescribeOverlap(overlap.Kind)}: {overlap.Mapping.LocalRoot} (mapping from {overlap.Mapping.SourceFile})"));
            }

            try
            {
                var dbPath = Path.Combine(config.StateDir, "state",
                    StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot) + ".sqlite");
                using var state = File.Exists(dbPath)
                    ? StateStore.Open(config, sync)
                    : StateStore.OpenReadOnly(config, sync);
                var count = state.CountEntries(includeFrozen: true);
                var runs = FastScanTracker.ReadRunsSinceFull(config, sync);
                checks.Add(new DoctorCheck($"sync {sync.Name} state", true,
                    state.IsInMemory
                        ? "no state DB yet (initial adoption)"
                        : $"{count} entries, {runs} fast run(s) since full listing"));
            }
            catch (Exception ex)
            {
                failed = true;
                checks.Add(new DoctorCheck($"sync {sync.Name} state", false, ex.Message));
            }
        }

        if (args.Json)
        {
            WriteJson(output, new
            {
                command = "doctor",
                ok = !failed,
                version,
                desktopMappings = mappingScan.Mappings.Select(mapping => mapping.LocalRoot).ToArray(),
                checks,
            });
        }
        else
        {
            foreach (var check in checks)
            {
                var prefix = check.Warning ? "warn" : check.Ok ? "ok  " : "FAIL";
                output.WriteLine($"{prefix} {check.Name}: {check.Detail}");
            }

            output.WriteLine(failed ? "doctor: problems found" : "doctor: ok");
        }

        return failed ? 1 : 0;
    }

    private static int Status(PdsyncConfig config, CliArguments args, TextWriter output, TextWriter error)
    {
        var syncs = SelectSyncs(config, args.SyncNames);
        var rows = new List<object>();
        foreach (var sync in syncs)
        {
            using var state = StateStore.OpenReadOnly(config, sync);
            var runs = state.LoadRuns(5);
            var pending = state.LoadPending();
            rows.Add(new
            {
                sync = sync.Name,
                state = state.IsInMemory ? "absent" : state.DatabasePath,
                pending = pending.Count,
                runs,
            });

            if (!args.Json)
            {
                output.WriteLine($"{sync.Name}: {(state.IsInMemory ? "no state DB" : state.DatabasePath)}");
                foreach (var run in runs)
                    output.WriteLine($"  run {run.Id}: {run.Status} {run.StartedAt:u} -> {run.FinishedAt:u}");
                if (pending.Count > 0)
                    output.WriteLine($"  pending: {pending.Count}");
            }
        }

        if (args.Json)
            WriteJson(output, new { command = "status", syncs = rows });
        return 0;
    }

    private static List<SyncConfig> SelectSyncs(PdsyncConfig config, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return config.Syncs.Where(s => s.Enabled).ToList();

        var result = new List<SyncConfig>();
        foreach (var name in names)
        {
            var match = config.Syncs.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new ConfigException($"unknown sync '{name}'");
            if (!match.Enabled)
                throw new ConfigException($"sync '{name}' is disabled");
            result.Add(match);
        }
        return result;
    }

    private static object ActionJson(PlanAction action) => new
    {
        kind = action.Kind.ToString(),
        path = action.Path,
        pathKey = action.PathKey,
        secondaryPath = action.SecondaryPath,
        reason = action.Reason,
    };

    private static void WritePlan(
        TextWriter output,
        SyncConfig sync,
        PlanResult plan,
        bool fast,
        int runsSinceFull)
    {
        output.WriteLine($"plan {sync.Name}{(fast ? " (fast)" : string.Empty)}: " +
                         $"{plan.Stats.Actions} action(s), {plan.Stats.Conflicts} conflict(s), " +
                         $"{plan.Stats.Skipped} skipped; fast runs since full: {runsSinceFull}");
        foreach (var action in plan.Actions)
            output.WriteLine($"  {Describe(action)}");
        foreach (var conflict in plan.Conflicts)
            output.WriteLine($"  conflict [{conflict.Resolution}] {conflict.Path}: {conflict.Message}");
        foreach (var skip in plan.Skipped)
            output.WriteLine($"  skip [{skip.Reason}] {skip.Path}: {skip.Message}");
        foreach (var adoption in plan.Adoptions)
            output.WriteLine($"  adopt {adoption.Path} ({adoption.Reason})");
    }

    private static void WriteRun(TextWriter output, SyncConfig sync, SyncAttempt attempt)
    {
        output.WriteLine(
            $"run {sync.Name}: planned {attempt.Plan.Stats.Actions}, " +
            $"succeeded {attempt.Report?.Succeeded ?? 0}, failed {attempt.Report?.Failed ?? 0}, " +
            $"skipped {attempt.Report?.Skipped ?? 0} in {attempt.DurationMs} ms");
        if (attempt.Report is null)
        {
            foreach (var conflict in attempt.Plan.Conflicts.Where(c => c.Resolution == PlanResolution.Abort))
                output.WriteLine($"  aborted [{conflict.Kind}] {conflict.Path}: {conflict.Message}");
            return;
        }

        foreach (var outcome in attempt.Report.Outcomes.Where(o => !o.Success))
            output.WriteLine($"  failed {outcome.Action.Kind} {outcome.Action.Path}: {outcome.Error}");
        foreach (var directory in attempt.Report.PrunedRemoteDirectories)
            output.WriteLine($"  pruned remote dir {directory}");
    }

    private static string Describe(PlanAction action) =>
        action.Kind switch
        {
            PlanActionKind.CreateRemoteDirectory => $"mkdir remote {action.Path}",
            PlanActionKind.CreateLocalDirectory => $"mkdir local {action.Path}",
            PlanActionKind.DownloadCreate => $"download new {action.Path}",
            PlanActionKind.DownloadReplace => $"download replace {action.Path}",
            PlanActionKind.DownloadRestore => $"download restore {action.Path}",
            PlanActionKind.LocalConflictRename => $"rename local {action.Path} -> {action.SecondaryPath}",
            PlanActionKind.UploadCreate => $"upload new {action.Path}",
            PlanActionKind.UploadRevision => $"upload revision {action.Path}",
            PlanActionKind.KeepBothUpload => $"upload conflict copy {action.Path}",
            PlanActionKind.RemoteMove => $"move remote {action.Path} -> {action.SecondaryPath}",
            PlanActionKind.LocalMove => $"move local {action.Path} -> {action.SecondaryPath}",
            PlanActionKind.LocalTrash => $"trash local {action.Path}",
            PlanActionKind.RemoteTrash => $"trash remote {action.Path}",
            PlanActionKind.DropBase => $"drop state {action.Path}",
            _ => action.Kind + " " + action.Path,
        };

    private sealed record DoctorCheck(string Name, bool Ok, string Detail, bool Warning = false);

    private static string DescribeOverlap(DesktopMappingOverlapKind kind) => kind switch
    {
        DesktopMappingOverlapKind.Exact => "exact overlap with desktop-app mapping",
        DesktopMappingOverlapKind.LocalRootInsideMapping => "local root is inside desktop-app mapping",
        DesktopMappingOverlapKind.MappingInsideLocalRoot => "desktop-app mapping is inside local root",
        _ => "overlap with desktop-app mapping",
    };

    private static void WriteNothing(TextWriter output, bool json)
    {
        if (json)
            WriteJson(output, new { command = "noop", syncs = Array.Empty<object>(), exitCode = 0 });
        else
            output.WriteLine("pdsync: no enabled syncs selected");
    }

    private static int Unknown(string command, TextWriter error)
    {
        error.WriteLine($"pdsync: unknown command '{command}'");
        return 1;
    }

    private static void WriteJson(TextWriter output, object value) =>
        output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("""
            pdsync <command> [options]

            Commands:
              plan [sync...]      show actions; never writes
              run  [sync...]      apply actions and commit state
              doctor              check CLI, auth, roots, state
              status [sync...]    recent runs and pending rows

            Options:
              --config <path>     explicit config file
              --fast              incremental remote listing
              --full              force a complete remote listing
              --force             override the deletion budget
              --json              machine-readable output
              --verbose           per-action output
              --yes               non-interactive (default)
            """);
    }

    private sealed class CliArguments
    {
        public string? Command { get; private set; }
        public string? ConfigPath { get; private set; }
        public bool Fast { get; private set; }
        public bool Full { get; private set; }
        public bool Force { get; private set; }
        public bool Json { get; private set; }
        public bool Verbose { get; private set; }
        public bool Interactive { get; private set; }
        public bool ShowHelp { get; private set; }
        public List<string> SyncNames { get; } = new();

        public static CliArguments Parse(string[] args)
        {
            var result = new CliArguments();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--config":
                        result.ConfigPath = Next(args, ref i, "--config");
                        break;
                    case "--json":
                        result.Json = true;
                        break;
                    case "--fast":
                        result.Fast = true;
                        break;
                    case "--full":
                        result.Full = true;
                        break;
                    case "--force":
                        result.Force = true;
                        break;
                    case "--verbose":
                    case "-v":
                        result.Verbose = true;
                        break;
                    case "--yes":
                        break;
                    case "--interactive":
                        result.Interactive = true;
                        break;
                    case "--no-color":
                        break;
                    case "--help":
                    case "-h":
                        result.ShowHelp = true;
                        break;
                    default:
                        if (arg.StartsWith("--config=", StringComparison.Ordinal))
                            result.ConfigPath = arg["--config=".Length..];
                        else if (arg.StartsWith('-'))
                            throw new ArgumentException($"unknown option '{arg}'");
                        else if (result.Command is null)
                            result.Command = arg;
                        else
                            result.SyncNames.Add(arg);
                        break;
                }
            }

            if (result.Fast && result.Full)
                throw new ArgumentException("--fast and --full are mutually exclusive");

            return result;
        }

        private static string Next(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"{option} requires a value");
            return args[++index];
        }
    }
}
