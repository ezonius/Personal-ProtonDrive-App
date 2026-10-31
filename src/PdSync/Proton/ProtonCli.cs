using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PdSync.Proton;

/// <summary>One node as returned by <c>fs list --json</c> / <c>fs info --json</c>.</summary>
public sealed record ProtonNode(
    string Uid,
    string Name,
    bool NameOk,
    string Type,
    string? RevisionUid,
    long Size,
    DateTimeOffset ModifiedAt,
    DateTimeOffset? ClaimedMtime,
    string? Sha1,
    bool Sha1Verified);

public class ProtonCliException : Exception
{
    public ProtonCliException(
        string message,
        IReadOnlyList<string> args,
        int exitCode,
        bool timedOut,
        string stdout,
        string stderr)
        : base(message)
    {
        Args = args;
        ExitCode = exitCode;
        TimedOut = timedOut;
        Stdout = stdout;
        Stderr = stderr;
    }

    public IReadOnlyList<string> Args { get; }
    public int ExitCode { get; }
    public bool TimedOut { get; }
    public string Stdout { get; }
    public string Stderr { get; }

    public string Command => "proton-drive " + string.Join(" ", Args.Select(Quote));
    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;
}

public sealed class ProtonAuthException : ProtonCliException
{
    public ProtonAuthException(
        string message,
        IReadOnlyList<string> args,
        int exitCode,
        bool timedOut,
        string stdout,
        string stderr)
        : base(message, args, exitCode, timedOut, stdout, stderr)
    {
    }
}

/// <summary>
/// The only class that spawns <c>proton-drive</c>. Calls are serialized because
/// the CLI shares SQLite caches and one account rate limit (D8).
/// </summary>
public sealed class ProtonCli
{
    private readonly string _executable;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();

    public ProtonCli(string executable, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("CLI executable is required", nameof(executable));

        _executable = executable;
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "timeout must be positive");
    }

    public string Executable => _executable;

    public IReadOnlyList<ProtonNode> List(string remotePath) =>
        ListRemote(remotePath);

    public ProtonNode Info(string remotePath)
    {
        var result = Run("fs", "info", "--json", EscapePath(remotePath));
        using var document = ParseJson(result, remotePath);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw InvalidJson(result, "expected one node object");
        return ParseNode(document.RootElement);
    }

    private IReadOnlyList<ProtonNode> ListRemote(string remotePath)
    {
        var result = Run("fs", "list", "--json", EscapePath(remotePath));
        using var document = ParseJson(result, remotePath);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw InvalidJson(result, "expected a JSON array");

        var nodes = new List<ProtonNode>();
        foreach (var item in document.RootElement.EnumerateArray())
            nodes.Add(ParseNode(item));
        return nodes;
    }

    private CliResult Run(params string[] args)
    {
        lock (_gate)
        {
            var started = Stopwatch.StartNew();
            var psi = new ProcessStartInfo(_executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new ProtonCliException(
                    $"could not start {_executable}: {ex.Message}",
                    args,
                    exitCode: -1,
                    timedOut: false,
                    stdout: string.Empty,
                    stderr: string.Empty);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var timedOut = false;
            if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }

            process.WaitForExit();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            var exitCode = process.HasExited ? process.ExitCode : -1;
            started.Stop();

            return new CliResult(args, exitCode, timedOut, stdout, stderr) { Elapsed = started.Elapsed };
        }
    }

    private static JsonDocument ParseJson(CliResult result, string remotePath)
    {
        if (result.ExitCode == 3)
        {
            throw new ProtonAuthException(
                "Proton Drive CLI is not authenticated (run: proton-drive auth login)",
                result.Args,
                result.ExitCode,
                result.TimedOut,
                result.Stdout,
                result.Stderr);
        }

        if (result.TimedOut)
        {
            throw new ProtonCliException(
                $"timed out after {result.Elapsed.TotalSeconds:0.#}s listing {remotePath}",
                result.Args,
                result.ExitCode,
                result.TimedOut,
                result.Stdout,
                result.Stderr);
        }

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
            throw new ProtonCliException(
                $"proton-drive exited {result.ExitCode} listing {remotePath}: {detail.Trim()}",
                result.Args,
                result.ExitCode,
                result.TimedOut,
                result.Stdout,
                result.Stderr);
        }

        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(result.Stdout) ? "null" : result.Stdout);
        }
        catch (JsonException ex)
        {
            throw InvalidJson(result, $"invalid JSON: {ex.Message}");
        }
    }

    private static ProtonCliException InvalidJson(CliResult result, string detail) => new(
        $"unexpected proton-drive output: {detail}",
        result.Args,
        result.ExitCode,
        result.TimedOut,
        result.Stdout,
        result.Stderr);

    private static ProtonNode ParseNode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("node is not an object");

        var uid = GetString(element, "uid") ?? string.Empty;
        var nameOk = false;
        var name = string.Empty;
        if (element.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.Object)
        {
            nameOk = nameElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
            name = GetString(nameElement, "value") ?? string.Empty;
        }

        var type = GetString(element, "type") ?? "other";
        var modified = ParseTime(GetString(element, "modificationTime")) ?? DateTimeOffset.UnixEpoch;

        string? revisionUid = null;
        long size = 0;
        DateTimeOffset? claimedMtime = null;
        string? sha1 = null;
        var sha1Verified = false;
        if (element.TryGetProperty("activeRevision", out var revision) && revision.ValueKind == JsonValueKind.Object)
        {
            revisionUid = GetString(revision, "uid");
            size = GetInt64(revision, "claimedSize") ?? 0;
            claimedMtime = ParseTime(GetString(revision, "claimedModificationTime"));
            if (revision.TryGetProperty("claimedDigests", out var digests) && digests.ValueKind == JsonValueKind.Object)
            {
                sha1 = GetString(digests, "sha1");
                sha1Verified = digests.TryGetProperty("sha1Verified", out var verified) &&
                               verified.ValueKind == JsonValueKind.True;
            }
        }

        return new ProtonNode(uid, name, nameOk, type, revisionUid, size, modified, claimedMtime, sha1, sha1Verified);
    }

    private static string? GetString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetInt64(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Escapes each path segment for the CLI. The display path uses '/' as a
    /// separator, so literal '/' in a name is not representable here (it is
    /// rejected by the remote index instead).
    /// </summary>
    public static string EscapePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var leading = path.StartsWith('/');
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var escaped = string.Join("/", segments.Select(segment =>
            segment.Replace("\\", "\\\\", StringComparison.Ordinal)
                   .Replace("/", "\\/", StringComparison.Ordinal)));
        return leading ? "/" + escaped : escaped;
    }

    private sealed record CliResult(
        string[] Args,
        int ExitCode,
        bool TimedOut,
        string Stdout,
        string Stderr)
    {
        public TimeSpan Elapsed { get; init; } = TimeSpan.Zero;
    }
}
