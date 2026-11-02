using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

public enum ProtonFailureKind
{
    Auth,
    Transient,
    Permanent,
}

public class ProtonCliException : Exception
{
    public ProtonCliException(
        string message,
        IReadOnlyList<string> args,
        int exitCode,
        bool timedOut,
        string stdout,
        string stderr,
        ProtonFailureKind kind = ProtonFailureKind.Permanent,
        int attempts = 1)
        : base(message)
    {
        Args = args;
        ExitCode = exitCode;
        TimedOut = timedOut;
        Stdout = stdout;
        Stderr = stderr;
        Kind = kind;
        Attempts = attempts;
    }

    public IReadOnlyList<string> Args { get; }
    public int ExitCode { get; }
    public bool TimedOut { get; }
    public string Stdout { get; }
    public string Stderr { get; }
    public ProtonFailureKind Kind { get; }

    /// <summary>Number of CLI attempts made, including the final failed one.</summary>
    public int Attempts { get; }

    public bool IsTransient => Kind == ProtonFailureKind.Transient;

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
        string stderr,
        int attempts = 1)
        : base(message, args, exitCode, timedOut, stdout, stderr, ProtonFailureKind.Auth, attempts)
    {
    }
}

/// <summary>
/// Bounded exponential backoff for transient CLI failures (SPEC §6.4).
/// Only idempotent calls, and non-replayable calls rejected with 429 before
/// execution, are retried; see <see cref="ProtonCli"/>.
/// </summary>
public sealed class ProtonRetryOptions
{
    public static ProtonRetryOptions Default { get; } = new();

    /// <summary>Total attempts, including the first one. 3 means 2 retries.</summary>
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Fractional jitter applied to each delay, e.g. 0.2 = ±20%.</summary>
    public double Jitter { get; init; } = 0.2;

    internal void Validate()
    {
        if (MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "MaxAttempts must be at least 1");
        if (BaseDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(BaseDelay), "BaseDelay must be positive");
        if (MaxDelay < BaseDelay)
            throw new ArgumentOutOfRangeException(nameof(MaxDelay), "MaxDelay must be at least BaseDelay");
        if (Jitter is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(Jitter), "Jitter must be in [0, 1)");
    }

    /// <summary>Delay after the given number of failed attempts (1-based).</summary>
    internal TimeSpan DelayFor(int failedAttempts)
    {
        var milliseconds = BaseDelay.TotalMilliseconds * Math.Pow(2, failedAttempts - 1);
        milliseconds = Math.Min(milliseconds, MaxDelay.TotalMilliseconds);
        if (Jitter > 0)
            milliseconds *= 1 + ((Random.Shared.NextDouble() * 2 - 1) * Jitter);
        return TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
    }
}

public sealed record UploadResult(
    int TransferredItems,
    long TransferredBytes,
    int SkippedItems,
    int FailedItems,
    IReadOnlyList<string> Failures);

public sealed record DownloadResult(
    int TransferredItems,
    long TransferredBytes,
    int SkippedItems,
    int FailedItems,
    IReadOnlyList<string> Failures);

/// <summary>
/// The only class that spawns <c>proton-drive</c>. Calls are serialized because
/// the CLI shares SQLite caches and one account rate limit (D8).
/// </summary>
public sealed class ProtonCli
{
    private readonly string _executable;
    private readonly TimeSpan _timeout;
    private readonly ProtonRetryOptions _retry;
    private readonly object _gate = new();

    private enum ProtonCallKind
    {
        /// <summary>Replaying the call converges to the same result (list/info/download).</summary>
        Idempotent,

        /// <summary>Replaying after an ambiguous failure could duplicate or reorder work.</summary>
        NonReplayable,
    }

    public ProtonCli(string executable, TimeSpan? timeout = null, ProtonRetryOptions? retry = null)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("CLI executable is required", nameof(executable));

        _executable = executable;
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "timeout must be positive");

        _retry = retry ?? ProtonRetryOptions.Default;
        _retry.Validate();
    }

    public string Executable => _executable;

    public IReadOnlyList<ProtonNode> List(string remotePath) =>
        ListRemote(remotePath);

    public ProtonNode Info(string remotePath)
    {
        var result = Run(ProtonCallKind.Idempotent, "fs", "info", "--json", EscapePath(remotePath));
        using var document = ParseJson(result, remotePath);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw InvalidJson(result, "expected one node object");
        return ParseNode(document.RootElement);
    }

    public string GetVersion()
    {
        var result = Run(ProtonCallKind.Idempotent, "--version");
        if (result.TimedOut)
            throw new ProtonCliException("timed out reading proton-drive --version", result.Args,
                result.ExitCode, true, result.Stdout, result.Stderr, ProtonFailureKind.Transient, result.Attempts);
        if (result.ExitCode != 0)
            throw CommandFailure(result, "reading --version");

        var text = result.Stdout.Trim();
        return text.Length == 0 ? "unknown" : text.Split('\n', 2)[0].Trim();
    }

    public ProtonNode CreateFolder(string parentPath, string name)
    {
        var result = Run(ProtonCallKind.NonReplayable, "fs", "create-folder", "--json", EscapePath(parentPath), name);
        using var document = ParseJson(result, parentPath);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw InvalidJson(result, "expected the created folder object");
        return ParseNode(document.RootElement);
    }

    public UploadResult Upload(string localPath, string parentPath)
    {
        var result = Run(
            ProtonCallKind.NonReplayable,
            "fs", "upload", "--json",
            "--file-conflict-strategy", "create-new-revision",
            LocalPathForCli(localPath), EscapePath(parentPath));
        return ParseTransfer<UploadResult>(result, parentPath, "upload", static (a, b, c, d, e) => new UploadResult(a, b, c, d, e));
    }

    public DownloadResult Download(string remotePath, string stagingDirectory)
    {
        var result = Run(
            ProtonCallKind.Idempotent,
            "fs", "download", "--json",
            "--file-conflict-strategy", "remove",
            EscapePath(remotePath), stagingDirectory);
        return ParseTransfer<DownloadResult>(result, remotePath, "download", static (a, b, c, d, e) => new DownloadResult(a, b, c, d, e));
    }

    public void Move(string sourcePath, string targetParentPath)
    {
        var result = Run(ProtonCallKind.NonReplayable, "fs", "move", "--json", EscapePath(sourcePath), EscapePath(targetParentPath));
        using var document = ParseJson(result, sourcePath);
        _ = document.RootElement;
    }

    public void Rename(string remotePath, string newName)
    {
        var result = Run(ProtonCallKind.NonReplayable, "fs", "rename", "--json", EscapePath(remotePath), newName);
        using var document = ParseJson(result, remotePath);
        _ = document.RootElement;
    }

    public void Trash(params string[] remotePaths)
    {
        if (remotePaths.Length == 0)
            return;

        var args = new List<string> { "fs", "trash", "--json" };
        args.AddRange(remotePaths.Select(EscapePath));
        var result = Run(ProtonCallKind.NonReplayable, args.ToArray());
        using var document = ParseJson(result, remotePaths[0]);
        _ = document.RootElement;
    }

    /// <summary>
    /// Restores one trashed node by its <c>/trash/&lt;name&gt;</c> path. The CLI
    /// (0.9.0) has no UID-addressed restore (S15), so callers must prove the
    /// name is unique before calling. Non-replayable: a timed-out restore is
    /// reconciled from the next <c>/trash</c> listing, never replayed blindly.
    /// </summary>
    public void Restore(string trashPath)
    {
        var result = Run(ProtonCallKind.NonReplayable, "fs", "restore", EscapePath(trashPath));
        EnsureSuccess(result, trashPath);
    }

    private IReadOnlyList<ProtonNode> ListRemote(string remotePath)
    {
        var result = Run(ProtonCallKind.Idempotent, "fs", "list", "--json", EscapePath(remotePath));
        using var document = ParseJson(result, remotePath);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw InvalidJson(result, "expected a JSON array");

        var nodes = new List<ProtonNode>();
        foreach (var item in document.RootElement.EnumerateArray())
            nodes.Add(ParseNode(item));
        return nodes;
    }

    private CliResult Run(params string[] args) =>
        Run(ProtonCallKind.Idempotent, args);

    /// <summary>
    /// Runs one CLI command, retrying transient failures with exponential
    /// backoff. Idempotent calls are retried on timeout, 429, 5xx, network
    /// resets, and spawn failures. Non-replayable calls are only retried when
    /// the CLI demonstrably did not execute the command (spawn failure or a
    /// 429 rate-limit rejection), so a timed-out upload/trash is never replayed
    /// blindly; the next run reconciles it from state.
    /// </summary>
    private CliResult Run(ProtonCallKind kind, params string[] args)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = ExecuteOnce(args);
            var classification = Classify(result);
            if (classification is null)
                return result with { Attempts = attempt };

            var retryable = classification == ProtonFailureKind.Transient &&
                            (kind == ProtonCallKind.Idempotent ||
                             result.StartError is not null ||
                             IsRateLimited(result));

            if (!retryable || attempt >= _retry.MaxAttempts)
                return result with { Attempts = attempt };

            Thread.Sleep(_retry.DelayFor(attempt));
        }
    }

    private CliResult ExecuteOnce(string[] args)
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
                started.Stop();
                return new CliResult(args, -1, false, string.Empty, string.Empty)
                {
                    Elapsed = started.Elapsed,
                    StartError = ex.Message,
                };
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

    private static ProtonFailureKind? Classify(CliResult result)
    {
        if (result.TimedOut)
            return ProtonFailureKind.Transient;
        if (result.ExitCode == 0)
            return null;
        if (result.ExitCode == 3)
            return ProtonFailureKind.Auth;
        if (result.StartError is not null)
            return ProtonFailureKind.Transient;
        if (IsRateLimited(result))
            return ProtonFailureKind.Transient;

        var text = result.Stderr + "\n" + result.Stdout;
        return TransientPatterns.Any(pattern => pattern.IsMatch(text))
            ? ProtonFailureKind.Transient
            : ProtonFailureKind.Permanent;
    }

    private static bool IsRateLimited(CliResult result)
    {
        if (result.ExitCode == 429)
            return true;
        var text = result.Stderr + "\n" + result.Stdout;
        return RateLimitPattern.IsMatch(text);
    }

    private static readonly Regex RateLimitPattern = new(
        @"\b429\b|rate[\s_-]?limit|too many requests",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex[] TransientPatterns =
    [
        new(@"\bHTTP[ /]?5\d\d\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bstatus(?:[ =:]+| code )5\d\d\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b50[234]\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"service unavailable|internal server error|bad gateway|gateway time-?out|temporarily unavailable",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"ECONNRESET|ECONNREFUSED|ETIMEDOUT|EAI_AGAIN|ENOTFOUND|socket hang up|connection reset|connection refused|network (?:error|is unreachable)|fetch failed",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\btimed out\b|\btimeout\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static ProtonCliException SpawnFailure(CliResult result) => new(
        $"could not start the proton-drive CLI: {result.StartError}",
        result.Args,
        result.ExitCode,
        result.TimedOut,
        result.Stdout,
        result.Stderr,
        ProtonFailureKind.Transient,
        result.Attempts);

    /// <summary>
    /// Turns a non-success CLI result into the matching exception before any
    /// JSON is required. Commands that do not emit JSON (for example
    /// <c>fs restore</c>) still must not treat a failed process as success.
    /// </summary>
    private static void EnsureSuccess(CliResult result, string target)
    {
        if (result.ExitCode == 3)
        {
            throw new ProtonAuthException(
                "Proton Drive CLI is not authenticated (run: proton-drive auth login)",
                result.Args,
                result.ExitCode,
                result.TimedOut,
                result.Stdout,
                result.Stderr,
                result.Attempts);
        }

        if (result.StartError is not null)
            throw SpawnFailure(result);

        if (result.TimedOut)
        {
            throw new ProtonCliException(
                $"timed out after {result.Elapsed.TotalSeconds:0.#}s operating on {target} " +
                $"(after {result.Attempts} attempt(s))",
                result.Args,
                result.ExitCode,
                result.TimedOut,
                result.Stdout,
                result.Stderr,
                ProtonFailureKind.Transient,
                result.Attempts);
        }

        if (result.ExitCode != 0)
            throw CommandFailure(result, target);
    }

    private static JsonDocument ParseJson(CliResult result, string remotePath)
    {
        EnsureSuccess(result, remotePath);

        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(result.Stdout) ? "null" : result.Stdout);
        }
        catch (JsonException ex)
        {
            throw InvalidJson(result, $"invalid JSON: {ex.Message}");
        }
    }

    private static ProtonCliException CommandFailure(CliResult result, string target)
    {
        var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        var kind = Classify(result) ?? ProtonFailureKind.Permanent;
        var message = $"proton-drive exited {result.ExitCode} operating on {target}: {detail.Trim()}";
        if (kind == ProtonFailureKind.Auth)
        {
            message += "; run 'proton-drive auth login' and retry";
            return new ProtonAuthException(
                message,
                result.Args,
                result.ExitCode,
                result.TimedOut,
                result.Stdout,
                result.Stderr,
                result.Attempts);
        }

        return new ProtonCliException(
            message,
            result.Args,
            result.ExitCode,
            result.TimedOut,
            result.Stdout,
            result.Stderr,
            kind,
            result.Attempts);
    }

    private static T ParseTransfer<T>(
        CliResult result,
        string target,
        string operation,
        Func<int, long, int, int, IReadOnlyList<string>, T> factory)
    {
        using var document = ParseJson(result, target);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw InvalidJson(result, "expected a transfer summary object");

        var root = document.RootElement;
        var transferred = GetInt32(root, "transferredItems") ?? 0;
        var bytes = GetInt64(root, "transferredBytes") ?? 0;
        var skipped = GetInt32(root, "skippedItems") ?? 0;
        var failed = GetInt32(root, "failedItems") ?? 0;
        var failures = new List<string>();
        if (root.TryGetProperty("failures", out var failureArray) && failureArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var failure in failureArray.EnumerateArray())
                failures.Add(failure.ValueKind == JsonValueKind.String
                    ? failure.GetString() ?? string.Empty
                    : failure.ToString());
        }

        if (failed > 0)
        {
            var detail = failures.Count == 0 ? "no failure detail" : string.Join("; ", failures);
            throw new ProtonCliException(
                $"proton-drive {operation} failed {failed} item(s) for {target}: {detail}",
                result.Args,
                result.ExitCode,
                result.TimedOut,
                result.Stdout,
                result.Stderr,
                ProtonFailureKind.Permanent,
                result.Attempts);
        }

        return factory(transferred, bytes, skipped, failed, failures);
    }

    private static int? GetInt32(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static string LocalPathForCli(string path)
    {
        // Staging paths are plain; escaping keeps ordinary bracketed names safe.
        return PdSync.Scan.LocalPathGlob.Escape(path);
    }

    private static ProtonCliException InvalidJson(CliResult result, string detail) => new(
        $"unexpected proton-drive output: {detail}",
        result.Args,
        result.ExitCode,
        result.TimedOut,
        result.Stdout,
        result.Stderr,
        ProtonFailureKind.Permanent,
        result.Attempts);

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
        public string? StartError { get; init; }
        public int Attempts { get; init; } = 1;
    }
}
