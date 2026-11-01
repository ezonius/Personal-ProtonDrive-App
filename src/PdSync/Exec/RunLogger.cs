using System.Globalization;
using System.Text;
using System.Text.Json;
using PdSync.Config;
using PdSync.Plan;

namespace PdSync.Exec;

/// <summary>
/// Writes the machine-readable run log required by NFR-10. One JSON object is
/// emitted per line; the file is created only for <c>run</c>, never for
/// <c>plan</c>. Logging is best-effort: a broken log path must not abort a sync.
/// </summary>
public sealed class RunLogger : IDisposable
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly StreamWriter? _writer;
    private readonly string _syncName;
    private readonly string _syncId;
    private readonly long _runId;
    private readonly TextWriter _output;
    private readonly object _gate = new();
    private bool _warningWritten;
    private bool _disposed;

    private RunLogger(
        StreamWriter? writer,
        string? path,
        string syncName,
        string syncId,
        long runId,
        TextWriter output)
    {
        _writer = writer;
        Path = path;
        _syncName = syncName;
        _syncId = syncId;
        _runId = runId;
        _output = output;
    }

    /// <summary>Absolute log path, or <c>null</c> when logging could not be enabled.</summary>
    public string? Path { get; }

    public static RunLogger Create(
        PdsyncConfig config,
        SyncConfig sync,
        long runId,
        PlanStats stats,
        bool fast,
        int runsSinceFull,
        TextWriter output)
    {
        StreamWriter? writer = null;
        string? path = null;
        try
        {
            var directory = System.IO.Path.Combine(config.LogDir, SanitizeName(sync.Name));
            Directory.CreateDirectory(directory);

            // The run id is unique per sync database; the timestamp keeps files
            // distinct if the state DB is rebuilt and ids restart at 1.
            var stamp = DateTimeOffset.UtcNow.ToString(
                "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            path = System.IO.Path.Combine(directory, $"{runId:D8}-{stamp}.jsonl");
            for (var suffix = 1; File.Exists(path); suffix++)
            {
                path = System.IO.Path.Combine(directory,
                    $"{runId:D8}-{stamp}-{suffix.ToString(CultureInfo.InvariantCulture)}.jsonl");
            }

            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read);
            writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex)
        {
            output.WriteLine($"warning: could not write run log: {ex.Message}");
        }

        var syncId = PdSync.State.StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot);
        var logger = new RunLogger(writer, path, sync.Name, syncId, runId, output);
        logger.Write(new
        {
            schemaVersion = SchemaVersion,
            @event = "run_start",
            sync = sync.Name,
            syncId = logger._syncId,
            runId,
            startedAt = DateTimeOffset.UtcNow,
            fast,
            runsSinceFull,
            planned = stats,
        });
        return logger;
    }

    public void Action(
        string kind,
        string path,
        string pathKey,
        string? secondaryPath,
        string outcome,
        long durationMs,
        string? error = null,
        VerificationSummary? verification = null)
    {
        Write(new
        {
            schemaVersion = SchemaVersion,
            @event = "action",
            sync = _syncName,
            runId = _runId,
            kind,
            path,
            pathKey,
            secondaryPath,
            outcome,
            durationMs,
            error,
        });

        if (verification is not null)
            Verification(kind, path, verification);
    }

    public void Verification(string kind, string path, VerificationSummary verification)
    {
        Write(new
        {
            schemaVersion = SchemaVersion,
            @event = "verification",
            sync = _syncName,
            runId = _runId,
            kind,
            path,
            method = verification.Method,
            result = verification.Result,
            downloadBack = verification.DownloadBack,
            detail = verification.Detail,
        });
    }

    public void RunFinish(
        string status,
        long durationMs,
        int succeeded,
        int failed,
        int prunedRemoteDirectories,
        string? error = null)
    {
        Write(new
        {
            schemaVersion = SchemaVersion,
            @event = "run_finish",
            sync = _syncName,
            runId = _runId,
            status,
            finishedAt = DateTimeOffset.UtcNow,
            durationMs,
            succeeded,
            failed,
            prunedRemoteDirectories,
            error,
        });
    }

    private void Write(object value)
    {
        if (_writer is null || _disposed)
            return;

        lock (_gate)
        {
            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
            }
            catch (Exception ex)
            {
                if (!_warningWritten)
                {
                    _warningWritten = true;
                    _output.WriteLine($"warning: run log write failed: {ex.Message}");
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // A log file that cannot be closed must not mask the run result.
        }
    }

    private static string SanitizeName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        var sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 || sanitized is "." or ".." ? "sync" : sanitized;
    }
}
