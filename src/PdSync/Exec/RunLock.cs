using System.Text;
using PdSync.Config;
using PdSync.State;

namespace PdSync.Exec;

/// <summary>
/// Per-sync run lock. A stale lock is reported with its PID/host and never
/// broken automatically (SPEC §8 rule 6).
/// </summary>
public sealed class RunLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;
    private bool _disposed;

    private RunLock(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
    }

    public static RunLock Acquire(PdsyncConfig config, SyncConfig sync)
    {
        var directory = Path.Combine(config.StateDir, "locks");
        Directory.CreateDirectory(directory);
        var syncId = StateStore.ComputeSyncId(sync.LocalRoot, sync.RemoteRoot);
        var path = Path.Combine(directory, syncId + ".lock");

        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            stream.SetLength(0);
            var text = Encoding.UTF8.GetBytes(
                $"pid={Environment.ProcessId}\nhost={Environment.MachineName}\n" +
                $"sync={sync.Name}\nstarted={DateTimeOffset.UtcNow:O}\n");
            stream.Write(text);
            stream.Flush(true);
            return new RunLock(stream, path);
        }
        catch (IOException ex)
        {
            var owner = ReadOwner(path);
            throw new LockException(
                owner is null
                    ? $"another pdsync run holds the lock for sync '{sync.Name}': {path}"
                    : $"another pdsync run holds the lock for sync '{sync.Name}': {path}\n{owner}",
                ex);
        }
    }

    private static string? ReadOwner(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _stream.Dispose();
        }
        finally
        {
            try
            {
                File.Delete(_path);
            }
            catch
            {
                // The lock is advisory; a leftover file is reported next time.
            }
        }
    }
}
