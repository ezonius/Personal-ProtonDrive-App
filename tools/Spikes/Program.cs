using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Spikes;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly List<Spike> Spikes = new();
    private static ProtonCli _cli = null!;
    private static string _remoteRoot = "";
    private static string _localRoot = "";

    private static async Task<int> Main(string[] args)
    {
        var keep = args.Contains("--keep");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _remoteRoot = $"/my-files/pdsync-test/spike-{stamp}";
        _localRoot = Path.Combine(Path.GetTempPath(), $"pdsync-spike-{stamp}");

        var exe = FindCli();
        _cli = new ProtonCli(exe);
        Directory.CreateDirectory(_localRoot);

        Console.WriteLine($"CLI:    {exe}");
        Console.WriteLine($"remote: {_remoteRoot}");
        Console.WriteLine($"local:  {_localRoot}");
        Console.WriteLine();

        var version = await _cli.RunAsync(30_000, "--version");
        var versionLine = version.Stdout.Split('\n').FirstOrDefault() ?? "";
        Console.WriteLine(versionLine);
        Console.WriteLine();

        try
        {
            if (!await SetupAsync())
            {
                Console.Error.WriteLine("Setup failed; aborting.");
                return 2;
            }

            await RunAsync("S1", "Upload upsert + JSON output", S1Async);
            await RunAsync("S2", "mtime/size preservation", S2Async);
            await RunAsync("S3", "same-content skip semantics", S3Async);
            await RunAsync("S4", "folder modificationTime propagation", S4Async);
            await RunAsync("S5", "CLI call latency", S5Async);
            await RunAsync("S6", "download conflict semantics", S6Async);
            await RunAsync("S7", "name encoding round-trip", S7Async);
            await RunAsync("S8", "auth expiry", S8Async);
            await RunAsync("S9", "trash and restore", S9Async);
            await RunAsync("S10", "folder creation semantics", S10Async);
            await RunAsync("S11", "remote case sensitivity", S11Async);
            await RunAsync("S12", "claimedModificationTime accuracy", S12Async);
            await RunAsync("S13", "case-collision download + hardlink upload", S13Async);
            await RunAsync("S14", "local path globbing + upload JSON", S14Async);
        }
        finally
        {
            if (!keep)
            {
                Console.WriteLine();
                Console.WriteLine("Cleaning up: trashing remote scratch folder...");
                var trash = await _cli.RunAsync(300_000, "fs", "trash", _remoteRoot);
                Console.WriteLine($"  trash exit={trash.ExitCode} {Truncate(trash.Stderr, 300)}");
                TryDeleteDir(_localRoot);
            }
            else
            {
                Console.WriteLine($"Keeping remote {_remoteRoot} and local {_localRoot}");
            }

            WriteReports(versionLine);
        }

        return Spikes.Any(s => s.Status == "error") ? 1 : 0;
    }

    // ---------------------------------------------------------------- setup

    private static async Task<bool> SetupAsync()
    {
        // Parent may already exist; ignore the error.
        await _cli.RunAsync(60_000, "fs", "create-folder", "/my-files", "pdsync-test");

        var mk = await _cli.RunAsync(60_000, "fs", "create-folder", "/my-files/pdsync-test", Path.GetFileName(_remoteRoot));
        if (mk.ExitCode != 0)
        {
            Console.Error.WriteLine(mk.Stderr);
            return false;
        }

        var list = await _cli.RunAsync(60_000, "fs", "list", "--json", _remoteRoot);
        Console.WriteLine($"[setup] create-folder exit={mk.ExitCode}; list exit={list.ExitCode}");
        return list.ExitCode == 0;
    }

    private static async Task RunAsync(string id, string title, Func<Spike, Task> body)
    {
        var spike = new Spike { Id = id, Title = title };
        Console.WriteLine($"[{id}] {title}");
        try
        {
            await body(spike);
        }
        catch (Exception ex)
        {
            spike.Status = "error";
            spike.Findings.Add("EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
        }

        Spikes.Add(spike);
        Console.WriteLine($"  -> {spike.Status}");
        foreach (var f in spike.Findings)
            Console.WriteLine("     " + f);
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- spikes

    private static async Task S1Async(Spike s)
    {
        var f = L("alpha.txt");
        WriteFile(f, "alpha v1\n");

        var up1 = await UploadAsync(f);
        Log(s, up1);
        s.Findings.Add($"first upload exit={up1.ExitCode}");
        s.Findings.Add($"upload stdout JSON shape = {Shape(TryJson(up1.Stdout))}");
        s.Findings.Add($"upload stdout raw = {Truncate(up1.Stdout, 700)}");

        var info1 = await InfoAsync(R("alpha.txt"));
        var rev1 = Rev(info1);
        s.Findings.Add($"info after create: name={Name(info1)} claimedSize={Size(info1)} rev={Short(rev1)}");

        WriteFile(f, "alpha v2 is longer\n");
        var up2 = await UploadAsync(f);
        Log(s, up2);
        var info2 = await InfoAsync(R("alpha.txt"));
        s.Findings.Add($"second upload exit={up2.ExitCode}; rev changed={rev1 != Rev(info2)}; claimedSize={Size(info2)}");
    }

    private static async Task S2Async(Spike s)
    {
        var f = L("mtime.txt");
        WriteFile(f, "mtime-body\n");
        var localMtime = new DateTime(2023, 4, 5, 6, 7, 8, 123, DateTimeKind.Utc);
        SetMtime(f, localMtime);

        var up = await UploadAsync(f);
        Log(s, up);
        var info = await InfoAsync(R("mtime.txt"));
        var claimed = Str(info?["activeRevision"]?["claimedModificationTime"]);
        var nodeMtime = Str(info?["modificationTime"]);

        s.Findings.Add($"local: size={new FileInfo(f).Length}, mtime(UTC)={localMtime:O}");
        s.Findings.Add($"remote: claimedSize={Size(info)}, storageSize={Str(info?["activeRevision"]?["storageSize"])}, claimedMtime={claimed}, nodeMtime={nodeMtime}");
        s.Findings.Add($"delta(local, claimed) = {DeltaMs(claimed, localMtime)} ms");
    }

    private static async Task S3Async(Spike s)
    {
        var f = L("alpha.txt");
        var before = await InfoAsync(R("alpha.txt"));
        var up = await UploadAsync(f);
        Log(s, up);
        var after = await InfoAsync(R("alpha.txt"));
        s.Findings.Add($"re-upload unchanged: exit={up.ExitCode}, rev changed={Rev(before) != Rev(after)}, stdout={Truncate(up.Stdout, 400)}");

        var copy = L("alpha-copy.txt");
        File.WriteAllText(copy, File.ReadAllText(f));
        var upCopy = await UploadAsync(copy);
        Log(s, upCopy);
        var copyInfo = await InfoAsync(R("alpha-copy.txt"));
        s.Findings.Add($"same content, different name: exit={upCopy.ExitCode}, created={copyInfo is not null}, stdout={Truncate(upCopy.Stdout, 400)}");
    }

    private static async Task S4Async(Spike s)
    {
        var before = await InfoAsync(_remoteRoot);
        WriteFile(L("child.txt"), "child\n");
        await UploadAsync(L("child.txt"));
        var after = await InfoAsync(_remoteRoot);
        s.Findings.Add($"root folder mtime: before={Str(before?["modificationTime"])}, after={Str(after?["modificationTime"])}, changed={Str(before?["modificationTime"]) != Str(after?["modificationTime"])}");

        await _cli.RunAsync(60_000, "fs", "create-folder", _remoteRoot, "sub4");
        var subBefore = await InfoAsync(R("sub4"));
        WriteFile(L("inner4.txt"), "inner\n");
        await UploadAsync(L("inner4.txt"), R("sub4"));
        var subAfter = await InfoAsync(R("sub4"));
        s.Findings.Add($"subfolder mtime: before={Str(subBefore?["modificationTime"])}, after={Str(subAfter?["modificationTime"])}, changed={Str(subBefore?["modificationTime"]) != Str(subAfter?["modificationTime"])}");
    }

    private static async Task S5Async(Spike s)
    {
        for (var i = 0; i < 2; i++)
        {
            var list = await _cli.RunAsync(60_000, "fs", "list", "--json", "/my-files");
            Log(s, list);
            s.Findings.Add($"list /my-files #{i + 1}: {list.ElapsedMs} ms, exit={list.ExitCode}");
        }

        var info = await _cli.RunAsync(60_000, "fs", "info", "--json", _remoteRoot);
        s.Findings.Add($"info root: {info.ElapsedMs} ms, exit={info.ExitCode}");

        var f = L("latency.txt");
        WriteFile(f, "latency\n");
        var up = await UploadAsync(f);
        Log(s, up);
        s.Findings.Add($"upload small file: {up.ElapsedMs} ms, exit={up.ExitCode}");

        var dlDir = L("latency-dl");
        Directory.CreateDirectory(dlDir);
        var dl = await _cli.RunAsync(120_000, "fs", "download", R("latency.txt"), dlDir);
        Log(s, dl);
        s.Findings.Add($"download small file: {dl.ElapsedMs} ms, exit={dl.ExitCode}");
    }

    private static async Task S6Async(Spike s)
    {
        var src = L("dl-src.txt");
        WriteFile(src, "remote-content\n");
        await UploadAsync(src);

        var dir = L("dl");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "dl-src.txt");

        WriteFile(target, "local-conflict\n");
        var r1 = await _cli.RunAsync(120_000, "fs", "download", "--file-conflict-strategy", "remove", R("dl-src.txt"), dir);
        Log(s, r1);
        s.Findings.Add($"remove strategy: exit={r1.ExitCode}, local content={Quote(File.ReadAllText(target))}");

        WriteFile(target, "local-conflict-2\n");
        var r2 = await _cli.RunAsync(120_000, "fs", "download", "--file-conflict-strategy", "skip", R("dl-src.txt"), dir);
        Log(s, r2);
        s.Findings.Add($"skip strategy: exit={r2.ExitCode}, local content={Quote(File.ReadAllText(target))}");

        var empty = L("dl-empty");
        Directory.CreateDirectory(empty);
        var r3 = await _cli.RunAsync(120_000, "fs", "download", R("dl-src.txt"), empty);
        Log(s, r3);
        var created = Path.Combine(empty, "dl-src.txt");
        s.Findings.Add($"into empty dir: exit={r3.ExitCode}, created={File.Exists(created)}, content={(File.Exists(created) ? Quote(File.ReadAllText(created)) : "n/a")}");
    }

    private static async Task S7Async(Spike s)
    {
        var names = new[]
        {
            "space name.txt",
            "unicode-æøå-你好.txt",
            "hash#name.txt",
            "plus+name.txt",
            "percent%name.txt",
            "amp&name.txt",
            "brackets[1].txt",
            "dollar$(x).txt",
        };

        foreach (var n in names)
            WriteFile(L(n), "content of " + n + "\n");

        foreach (var n in names)
        {
            var ok = await UploadOneAsync(s, L(n), n);
            s.Findings.Add($"upload {(ok ? "OK  " : "FAIL")} {n}");
        }

        var list = await _cli.RunAsync(60_000, "fs", "list", "--json", _remoteRoot);
        Log(s, list);
        var actual = (TryJson(list.Stdout) as JsonArray ?? new JsonArray())
            .Select(n => Name(n))
            .Where(n => n is not null)
            .Select(n => n!)
            .Where(n => n.EndsWith(".txt", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        foreach (var expected in names)
        {
            var found = actual.Contains(expected, StringComparer.Ordinal);
            s.Findings.Add($"{(found ? "OK  " : "DIFF")} listed {expected}");
        }

        s.Findings.Add("note: '/' and '\\\\' are illegal in Windows file names, so remote escaping cannot be exercised from this host");
    }

    private static Task S8Async(Spike s)
    {
        s.Status = "skipped";
        s.Findings.Add("Not exercised: deliberately expiring the session would disturb the CLI login used by the rest of the suite.");
        s.Findings.Add("Design assumption remains: auth failures are detected from stderr/exit code and mapped to exit 3.");
        return Task.CompletedTask;
    }

    private static async Task S9Async(Spike s)
    {
        WriteFile(L("trash-a.txt"), "a\n");
        WriteFile(L("trash-b.txt"), "b\n");
        await UploadAsync(L("trash-a.txt"));
        await UploadAsync(L("trash-b.txt"));

        var t = await _cli.RunAsync(120_000, "fs", "trash", R("trash-a.txt"), R("trash-b.txt"));
        Log(s, t);
        s.Findings.Add($"trash two paths: exit={t.ExitCode}, stderr={Truncate(t.Stderr, 300)}");

        var trashList = await _cli.RunAsync(60_000, "fs", "list", "--json", "/trash");
        Log(s, trashList);
        var names = (TryJson(trashList.Stdout) as JsonArray ?? new JsonArray())
            .Select(n => Name(n)).Where(n => n is not null).Select(n => n!).ToArray();
        s.Findings.Add($"trash contains: {string.Join(", ", names.Where(n => n.StartsWith("trash-", StringComparison.Ordinal)))}");

        var restore = await _cli.RunAsync(120_000, "fs", "restore", "/trash/trash-a.txt");
        Log(s, restore);
        s.Findings.Add($"restore /trash/trash-a.txt: exit={restore.ExitCode}, stderr={Truncate(restore.Stderr, 300)}");

        var rootList = await _cli.RunAsync(60_000, "fs", "list", "--json", _remoteRoot);
        var rootNames = (TryJson(rootList.Stdout) as JsonArray ?? new JsonArray())
            .Select(n => Name(n)).Where(n => n is not null).Select(n => n!).ToArray();
        s.Findings.Add($"root after restore contains trash-a: {rootNames.Contains("trash-a.txt")}");
    }

    private static async Task S10Async(Spike s)
    {
        var first = await _cli.RunAsync(60_000, "fs", "create-folder", _remoteRoot, "existing");
        var second = await _cli.RunAsync(60_000, "fs", "create-folder", _remoteRoot, "existing");
        Log(s, second);
        s.Findings.Add($"create-folder twice: first={first.ExitCode}, second={second.ExitCode}, stderr={Truncate(second.Stderr, 300)}");

        var missing = await _cli.RunAsync(60_000, "fs", "create-folder", R("missing-parent"), "child");
        Log(s, missing);
        s.Findings.Add($"create-folder with missing parent: exit={missing.ExitCode}, stderr={Truncate(missing.Stderr, 300)}");

        var folder = L("folder-up");
        WriteFile(Path.Combine(folder, "inner.txt"), "inner\n");
        var upload1 = await _cli.RunAsync(180_000, "fs", "upload", "--folder-conflict-strategy", "merge", folder, _remoteRoot);
        Log(s, upload1);
        var upload2 = await _cli.RunAsync(180_000, "fs", "upload", "--folder-conflict-strategy", "merge", folder, _remoteRoot);
        Log(s, upload2);
        s.Findings.Add($"upload folder twice with merge: first={upload1.ExitCode}, second={upload2.ExitCode}");
    }

    private static async Task S11Async(Spike s)
    {
        var lower = L("name.txt");
        WriteFile(lower, "lower-v1\n");
        var up1 = await UploadAsync(lower);
        Log(s, up1);

        // Windows case-only rename via a temporary name.
        var tmp = L("name.case-tmp");
        File.Move(lower, tmp);
        File.Move(tmp, L("Name.txt"));

        var up2 = await UploadAsync(L("Name.txt"));
        Log(s, up2);

        var list = await _cli.RunAsync(60_000, "fs", "list", "--json", _remoteRoot);
        Log(s, list);
        var names = (TryJson(list.Stdout) as JsonArray ?? new JsonArray())
            .Select(n => Name(n)).Where(n => n is not null).Select(n => n!).ToArray();

        var variants = names.Where(n => string.Equals(n, "name.txt", StringComparison.OrdinalIgnoreCase)).ToArray();
        s.Findings.Add($"remote variants of name.txt: count={variants.Length} [{string.Join(", ", variants)}]");
        s.Findings.Add(variants.Length > 1
            ? "CONFIRMED: backend is case-sensitive and a second upload creates a case variant."
            : "Backend appears to treat the names as the same node (or reused it).");
    }

    private static async Task S12Async(Spike s)
    {
        var f = L("clock.txt");
        WriteFile(f, "clock\n");
        var localMtime = new DateTime(2024, 12, 31, 23, 59, 59, 987, DateTimeKind.Utc);
        SetMtime(f, localMtime);

        var up = await UploadAsync(f);
        Log(s, up);
        var info = await InfoAsync(R("clock.txt"));
        var claimed = Str(info?["activeRevision"]?["claimedModificationTime"]);
        var nodeMtime = Str(info?["modificationTime"]);
        s.Findings.Add($"local mtime(UTC)={localMtime:O}");
        s.Findings.Add($"claimedMtime={claimed}, nodeMtime={nodeMtime}");
        s.Findings.Add($"delta(local, claimed) = {DeltaMs(claimed, localMtime)} ms");
    }

    private static async Task S13Async(Spike s)
    {
        // Part A: download a differently-cased remote name over an existing local file.
        WriteFile(L("Case.txt"), "UPPER\n");
        await UploadAsync(L("Case.txt"));

        var dir = L("case-dl");
        Directory.CreateDirectory(dir);
        WriteFile(Path.Combine(dir, "case.txt"), "lower-local\n");
        var dl = await _cli.RunAsync(120_000, "fs", "download", "--file-conflict-strategy", "remove", R("Case.txt"), dir);
        Log(s, dl);
        var entries = Directory.GetFiles(dir).Select(p => Path.GetFileName(p)).ToArray();
        s.Findings.Add($"case download: exit={dl.ExitCode}, entries=[{string.Join(", ", entries)}]");
        foreach (var e in entries)
            s.Findings.Add($"  {e}: {Quote(File.ReadAllText(Path.Combine(dir, e!)))}");

        // Part B: hardlink staging upload.
        var target = L("hardlink-target.txt");
        WriteFile(target, "hardlink-body\n");
        var stageDir = L("stage");
        Directory.CreateDirectory(stageDir);
        var stage = Path.Combine(stageDir, "canonical-name.txt");
        var linked = CreateHardLink(stage, target, IntPtr.Zero);
        var err = Marshal.GetLastWin32Error();
        s.Findings.Add($"CreateHardLink: ok={linked}, win32err={err}");
        if (!linked)
            File.Copy(target, stage);

        var up = await UploadAsync(stage);
        Log(s, up);
        var info = await InfoAsync(R("canonical-name.txt"));
        s.Findings.Add($"staged upload: exit={up.ExitCode}, claimedSize={Size(info)}, targetUnchanged={File.ReadAllText(target) == "hardlink-body\n"}");
        File.Delete(stage);
    }

    // -------------------------------------------------------------- helpers

    private static async Task S14Async(Spike s)
    {
        // 1. Unescaped '[' is treated as a glob and does not match the literal name.
        var bracket = L("glob[1].txt");
        WriteFile(bracket, "bracket\n");
        var raw = await _cli.RunAsync(120_000, "fs", "upload", "--json", "--skip-thumbnails", bracket, _remoteRoot);
        Log(s, raw);
        s.Findings.Add($"unescaped bracket: exit={raw.ExitCode}, stderr={Truncate(raw.Stderr, 200)}");

        var escaped = GlobEscape(bracket);
        var esc = await _cli.RunAsync(120_000, "fs", "upload", "--json", "--skip-thumbnails", escaped, _remoteRoot);
        Log(s, esc);
        s.Findings.Add($"escaped bracket ({escaped}): exit={esc.ExitCode}");

        // 2. Brace expansion and the temp-name + rename fallback.
        var brace = L("{x,y}.txt");
        WriteFile(brace, "brace\n");
        var rawBrace = await _cli.RunAsync(120_000, "fs", "upload", "--json", "--skip-thumbnails", brace, _remoteRoot);
        Log(s, rawBrace);
        s.Findings.Add($"unescaped brace: exit={rawBrace.ExitCode}, stderr={Truncate(rawBrace.Stderr, 200)}");

        var staged = await UploadOneAsync(s, brace, "{x,y}.txt");
        s.Findings.Add($"temp-name + fs rename for brace: ok={staged}");

        // 3. Upload JSON shape on a normal file.
        var normal = L("json-shape.txt");
        WriteFile(normal, "json\n");
        var jsonUp = await _cli.RunAsync(120_000, "fs", "upload", "--json", "--skip-thumbnails", normal, _remoteRoot);
        Log(s, jsonUp);
        s.Findings.Add($"upload --json shape = {Shape(TryJson(jsonUp.Stdout))}; raw={Truncate(jsonUp.Stdout, 300)}");
    }

    private static async Task<bool> UploadOneAsync(Spike s, string localPath, string canonicalName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(canonicalName, @"\{[^{}]*,[^{}]*\}"))
        {
            var up = await _cli.RunAsync(180_000, "fs", "upload", "--json", "--skip-thumbnails", GlobEscape(localPath), _remoteRoot);
            Log(s, up);
            return up.ExitCode == 0;
        }

        var stage = Path.Combine(Path.GetDirectoryName(localPath)!, "pdsync-stage-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            if (!CreateHardLink(stage, localPath, IntPtr.Zero))
                File.Copy(localPath, stage);

            var up = await _cli.RunAsync(180_000, "fs", "upload", "--json", "--skip-thumbnails", stage, _remoteRoot);
            Log(s, up);
            if (up.ExitCode != 0)
                return false;

            var rename = await _cli.RunAsync(120_000, "fs", "rename", "--json", R(Path.GetFileName(stage)), canonicalName);
            Log(s, rename);
            return rename.ExitCode == 0;
        }
        finally
        {
            try { File.Delete(stage); } catch { /* ignore */ }
        }
    }

    private static string GlobEscape(string path) => path
        .Replace("[", "[[]")
        .Replace("*", "[*]")
        .Replace("?", "[?]");

    private static Task<CliResult> UploadAsync(string localPath, string? parent = null)
        => _cli.RunAsync(180_000, "fs", "upload", "--file-conflict-strategy", "create-new-revision", "--skip-thumbnails", localPath, parent ?? _remoteRoot);

    private static async Task<JsonNode?> InfoAsync(string remotePath)
    {
        var r = await _cli.RunAsync(60_000, "fs", "info", "--json", remotePath);
        return TryJson(r.Stdout);
    }

    private static string L(params string[] parts) => Path.Combine(new[] { _localRoot }.Concat(parts).ToArray());

    private static string R(string rel) => _remoteRoot + "/" + rel;

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void SetMtime(string path, DateTime utc) => File.SetLastWriteTimeUtc(path, utc);

    private static void Log(Spike s, CliResult r) => s.Calls.Add(new CallLog
    {
        Command = r.Command,
        Exit = r.ExitCode,
        Ms = r.ElapsedMs,
        Stdout = Truncate(r.Stdout, 1500),
        Stderr = Truncate(r.Stderr, 1000),
    });

    private static JsonNode? TryJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try { return JsonNode.Parse(text); }
        catch { return null; }
    }

    private static string Shape(JsonNode? node) => node switch
    {
        null => "<none>",
        JsonArray a => $"array[{a.Count}] itemKeys={(a.Count > 0 && a[0] is JsonObject o ? string.Join(",", o.Select(kv => kv.Key)) : "n/a")}",
        JsonObject o => "objectKeys=" + string.Join(",", o.Select(kv => kv.Key)),
        _ => node.GetValueKind().ToString(),
    };

    private static string? Name(JsonNode? node)
    {
        try { return node?["name"]?["value"]?.GetValue<string>(); }
        catch { return null; }
    }

    private static string? Rev(JsonNode? node)
    {
        try { return node?["activeRevision"]?["uid"]?.GetValue<string>(); }
        catch { return null; }
    }

    private static string? Size(JsonNode? node)
    {
        try { return node?["activeRevision"]?["claimedSize"]?.ToString(); }
        catch { return null; }
    }

    private static string Str(JsonNode? node)
    {
        try { return node?.ToString() ?? "<null>"; }
        catch { return "<error>"; }
    }

    private static string Short(string? s) => s is null ? "<null>" : (s.Length <= 24 ? s : s[..24] + "…");

    private static double? DeltaMs(string? iso, DateTime localUtc)
    {
        if (iso is null) return null;
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return null;
        return (dto.UtcDateTime - localUtc).TotalMilliseconds;
    }

    private static string Quote(string s) => "\"" + s.Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string FindCli()
    {
        var env = Environment.GetEnvironmentVariable("PDSYNC_CLI");
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "proton-drive.exe");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return "proton-drive";
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "pdsync.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static void TryDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) { Console.WriteLine($"  local cleanup skipped: {ex.Message}"); }
    }

    private static void WriteReports(string versionLine)
    {
        var docs = Path.Combine(FindRepoRoot(), "docs");
        Directory.CreateDirectory(docs);

        var payload = new SpikeReport
        {
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            CliVersion = versionLine,
            RemoteRoot = _remoteRoot,
            LocalRoot = _localRoot,
            Spikes = Spikes,
        };
        File.WriteAllText(Path.Combine(docs, "spikes-results.json"), JsonSerializer.Serialize(payload, JsonOpts));

        var sb = new StringBuilder();
        sb.AppendLine("# Spike results (raw)").AppendLine();
        sb.AppendLine($"- Generated: {payload.GeneratedAt}");
        sb.AppendLine($"- CLI: `{versionLine}`");
        sb.AppendLine($"- Remote: `{_remoteRoot}`");
        sb.AppendLine();
        foreach (var spike in Spikes)
        {
            sb.AppendLine($"## {spike.Id} — {spike.Title} ({spike.Status})").AppendLine();
            foreach (var finding in spike.Findings)
                sb.AppendLine($"- {finding}");
            sb.AppendLine();
            sb.AppendLine("| ms | exit | command |").AppendLine("|---:|---:|---|");
            foreach (var call in spike.Calls)
                sb.AppendLine($"| {call.Ms} | {call.Exit} | `{call.Command.Replace("|", "\\|")}` |");
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(docs, "spikes-results.md"), sb.ToString());
        Console.WriteLine($"Wrote {Path.Combine(docs, "spikes-results.json")} and .md");
    }

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}

internal sealed class ProtonCli
{
    private readonly string _exe;

    public ProtonCli(string exe) => _exe = exe;

    public async Task<CliResult> RunAsync(int timeoutMs, params string[] args)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var timedOut = false;
        using (var cts = new CancellationTokenSource(timeoutMs))
        {
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
        }

        try { await p.WaitForExitAsync(); } catch { /* ignore */ }
        sw.Stop();

        return new CliResult
        {
            Args = args,
            ExitCode = timedOut ? -2 : (p.HasExited ? p.ExitCode : -1),
            TimedOut = timedOut,
            Stdout = stdout.ToString().TrimEnd(),
            Stderr = stderr.ToString().TrimEnd(),
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }
}

internal sealed class CliResult
{
    public string[] Args { get; init; } = Array.Empty<string>();
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
    public long ElapsedMs { get; init; }

    public string Command => "proton-drive " + string.Join(" ", Args.Select(a =>
        a.Any(char.IsWhiteSpace) || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
}

internal sealed class Spike
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "ok";
    public List<string> Findings { get; set; } = new();
    public List<CallLog> Calls { get; set; } = new();
}

internal sealed class CallLog
{
    public string Command { get; set; } = "";
    public int Exit { get; set; }
    public long Ms { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
}

internal sealed class SpikeReport
{
    public string GeneratedAt { get; set; } = "";
    public string CliVersion { get; set; } = "";
    public string RemoteRoot { get; set; } = "";
    public string LocalRoot { get; set; } = "";
    public List<Spike> Spikes { get; set; } = new();
}
