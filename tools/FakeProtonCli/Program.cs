using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FakeProtonCli;

public sealed class SeedManifest
{
    public bool Auth { get; set; } = true;
    public List<SeedNode> Nodes { get; set; } = new();
}

public sealed class SeedNode
{
    public string Path { get; set; } = "";
    public string Type { get; set; } = "file";
    public string? Content { get; set; }
    public string? ContentBase64 { get; set; }
    public string? SourceFile { get; set; }
    public DateTime? Mtime { get; set; }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        var stateDir = Environment.GetEnvironmentVariable("PDSYNC_FAKE_STATE")
            ?? Path.Combine(Environment.CurrentDirectory, ".pdsync-fake-state");

        try
        {
            Directory.CreateDirectory(stateDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot create fake state dir: {ex.Message}");
            return 1;
        }

        if (args.Length > 0 && args[0] == "__fake")
            return Control(args.Skip(1).ToArray(), stateDir);

        using var store = StateStore.Open(stateDir);
        store.LogInvocation(args);

        var scenario = Scenario.Load(Environment.GetEnvironmentVariable("PDSYNC_FAKE_SCENARIO"), stateDir);
        var outcome = ScenarioEngine.Evaluate(scenario, args, store.State);
        store.Save();

        if (outcome.LatencyMs > 0)
            Thread.Sleep(outcome.LatencyMs);

        if (outcome.AuthExpired)
        {
            Console.Error.WriteLine("Session expired. Run: proton-drive auth login");
            return 3;
        }

        if (outcome.ExitCode != 0)
        {
            if (!string.IsNullOrEmpty(outcome.Stderr))
                Console.Error.WriteLine(outcome.Stderr);
            return outcome.ExitCode;
        }

        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        if (args[0] is "--version" or "-v")
        {
            Console.WriteLine("Fake Proton Drive CLI 0.9.0 (pdsync fake)");
            return 0;
        }

        if (args[0] is "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        var drive = new Drive(store.State);
        return new Commands(drive, store, outcome, args).Execute();
    }

    private static int Control(string[] args, string stateDir)
    {
        using var store = StateStore.Open(stateDir);
        var sub = args.Length > 0 ? args[0] : "path";

        switch (sub)
        {
            case "reset":
                store.Replace(StateStore.NewState());
                store.Save();
                Console.WriteLine(stateDir);
                return 0;

            case "seed":
                if (args.Length < 2)
                    return Fail("Usage: __fake seed <manifest.json>");
                try
                {
                    var seeded = Seed(args[1], store);
                    store.Replace(seeded);
                    store.Save();
                    Console.WriteLine($"Seeded {seeded.Nodes.Count} nodes into {stateDir}");
                    return 0;
                }
                catch (Exception ex)
                {
                    return Fail($"Seed failed: {ex.Message}");
                }

            case "dump":
                Console.WriteLine(JsonSerializer.Serialize(store.State, FakeJson.Pretty));
                return 0;

            case "log":
                if (File.Exists(store.InvocationFile))
                    Console.Write(File.ReadAllText(store.InvocationFile));
                return 0;

            case "path":
                Console.WriteLine(stateDir);
                return 0;

            case "set-auth":
                if (args.Length < 2 || args[1] is not ("on" or "off"))
                    return Fail("Usage: __fake set-auth on|off");
                store.State.Auth = args[1] == "on";
                store.Save();
                return 0;

            case "scenario":
                if (args.Length < 2)
                    return Fail("Usage: __fake scenario <scenario.json>");
                File.Copy(args[1], Path.Combine(stateDir, "scenario.json"), overwrite: true);
                Console.WriteLine(Path.Combine(stateDir, "scenario.json"));
                return 0;

            case "clear-scenario":
                File.Delete(Path.Combine(stateDir, "scenario.json"));
                return 0;

            default:
                return Fail($"Unknown fake control command: {sub}");
        }
    }

    private static FakeState Seed(string manifestPath, StateStore store)
    {
        var manifest = JsonSerializer.Deserialize<SeedManifest>(File.ReadAllText(manifestPath), FakeJson.Pretty)
            ?? throw new InvalidOperationException("Empty manifest");

        var state = StateStore.NewState();
        state.Auth = manifest.Auth;
        var drive = new Drive(state);

        foreach (var seed in manifest.Nodes)
        {
            var segments = Drive.SplitPath(seed.Path)
                ?? throw new InvalidOperationException($"Invalid path: {seed.Path}");
            if (segments.Count == 0 || segments[0] != "my-files")
                throw new InvalidOperationException($"Path must be under /my-files: {seed.Path}");

            var parent = drive.MyFiles;
            for (var i = 1; i < segments.Count - 1; i++)
            {
                var name = segments[i];
                var existing = state.Nodes.FirstOrDefault(n =>
                    n.ParentUid == parent.Uid && !n.Trashed && n.Name == name && n.Type == "folder");
                parent = existing ?? drive.CreateNode(parent, name, "folder");
            }

            var leaf = segments[^1];
            var type = string.Equals(seed.Type, "folder", StringComparison.OrdinalIgnoreCase) ? "folder" : "file";
            var duplicate = state.Nodes.FirstOrDefault(n =>
                n.ParentUid == parent.Uid && !n.Trashed && n.Name == leaf);
            if (duplicate is not null)
                state.Nodes.Remove(duplicate);

            var node = drive.CreateNode(parent, leaf, type);
            if (type == "folder")
            {
                if (seed.Mtime is not null)
                    node.ModifiedUtc = seed.Mtime.Value.ToUniversalTime();
                continue;
            }

            var bytes = ReadSeedContent(seed);
            var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
            node.Sha1 = sha1;
            node.Size = bytes.Length;
            node.RevUid = drive.NewRevisionUid();
            node.ClaimedMtimeUtc = seed.Mtime?.ToUniversalTime() ?? DateTime.UtcNow;
            node.ModifiedUtc = node.ClaimedMtimeUtc.Value;
            store.SaveBlob(sha1, bytes);
        }

        return state;
    }

    private static byte[] ReadSeedContent(SeedNode seed)
    {
        if (seed.Content is not null)
            return Encoding.UTF8.GetBytes(seed.Content);
        if (seed.ContentBase64 is not null)
            return Convert.FromBase64String(seed.ContentBase64);
        if (seed.SourceFile is not null)
            return File.ReadAllBytes(seed.SourceFile);
        return Array.Empty<byte>();
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Fake Proton Drive CLI (pdsync test harness)

            Environment:
              PDSYNC_FAKE_STATE      state directory (required for isolation)
              PDSYNC_FAKE_SCENARIO   scenario JSON path (or <state>/scenario.json)

            Commands mirror the real CLI:
              auth login|logout|status
              fs list|info|create-folder|upload|download|move|rename|trash|restore|delete|empty-trash|size
              --version, --help

            Control commands:
              __fake reset
              __fake seed <manifest.json>
              __fake dump
              __fake log
              __fake path
              __fake set-auth on|off
              __fake scenario <scenario.json>
              __fake clear-scenario
            """);
    }
}
