using System.Security.Cryptography;

namespace PdSync.Scan;

/// <summary>
/// SHA-1 is pdsync's content fingerprint: it matches Proton's plaintext
/// <c>claimedDigests.sha1</c> and is used for change detection and convergence,
/// never as a security primitive (SPEC §4.1).
/// </summary>
public static class ContentHash
{
    private const int BufferSize = 128 * 1024;

    public static string ComputeSha1(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.SequentialScan);

        return ComputeSha1(stream);
    }

    public static string ComputeSha1(Stream stream) =>
        Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();

    public static string ComputeSha1(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
}

/// <summary>
/// Cache keyed by the metadata that makes a hash reusable: absolute path,
/// size, and mtime. The scanner owns the policy; this is only a lookup table.
/// </summary>
public interface IHashCache
{
    bool TryGet(string fullPath, long size, long mtimeUtcTicks, out string sha1);

    void Set(string fullPath, long size, long mtimeUtcTicks, string sha1);
}

public sealed class ContentHashCache : IHashCache
{
    private readonly record struct Key(string FullPath, long Size, long MtimeUtcTicks);

    private readonly Dictionary<Key, string> _entries = new();
    private readonly Queue<Key> _order = new();
    private readonly int _capacity;
    private readonly object _gate = new();

    public ContentHashCache(int capacity = 20_000)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public bool TryGet(string fullPath, long size, long mtimeUtcTicks, out string sha1)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(new Key(fullPath, size, mtimeUtcTicks), out var found))
            {
                sha1 = found;
                return true;
            }

            sha1 = string.Empty;
            return false;
        }
    }

    public void Set(string fullPath, long size, long mtimeUtcTicks, string sha1)
    {
        lock (_gate)
        {
            var key = new Key(fullPath, size, mtimeUtcTicks);
            if (_entries.ContainsKey(key))
                return;

            while (_entries.Count >= _capacity && _order.Count > 0)
                _entries.Remove(_order.Dequeue());

            _entries[key] = sha1;
            _order.Enqueue(key);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
        }
    }
}
