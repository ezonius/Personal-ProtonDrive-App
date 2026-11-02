using System.Text.Json;
using System.Text.Json.Serialization;

namespace FakeProtonCli;

public sealed class FakeState
{
    public int SchemaVersion { get; set; } = 1;
    public bool Auth { get; set; } = true;
    public string? Version { get; set; }
    public int NextId { get; set; } = 1;
    public List<FakeNode> Nodes { get; set; } = new();
    public Dictionary<string, int> RuleHits { get; set; } = new();
}

public sealed class FakeNode
{
    public string Uid { get; set; } = "";
    public string ParentUid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "file"; // file | folder
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public DateTime? ClaimedMtimeUtc { get; set; }
    public string? RevUid { get; set; }
    public bool Trashed { get; set; }
    public DateTime? TrashTime { get; set; }
    public string? OriginalParentUid { get; set; }
}

public static class FakeJson
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class StateStore : IDisposable
{
    private readonly FileStream _lock;
    private readonly string _dir;

    public FakeState State { get; private set; }

    public string StateFile => Path.Combine(_dir, "state.json");
    public string BlobDir => Path.Combine(_dir, "blobs");
    public string InvocationFile => Path.Combine(_dir, "invocations.jsonl");

    private StateStore(string dir, FileStream lockStream, FakeState state)
    {
        _dir = dir;
        _lock = lockStream;
        State = state;
        Directory.CreateDirectory(BlobDir);
    }

    public static StateStore Open(string dir)
    {
        Directory.CreateDirectory(dir);
        FileStream? lockStream = null;
        var lockPath = Path.Combine(dir, "state.lock");
        for (var i = 0; i < 200; i++)
        {
            try
            {
                lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }

        if (lockStream is null)
            throw new IOException($"Could not acquire fake state lock: {lockPath}");

        var stateFile = Path.Combine(dir, "state.json");
        FakeState state;
        if (File.Exists(stateFile))
        {
            var json = File.ReadAllText(stateFile);
            state = JsonSerializer.Deserialize<FakeState>(json, FakeJson.Pretty) ?? NewState();
        }
        else
        {
            state = NewState();
        }

        return new StateStore(dir, lockStream, state);
    }

    public static FakeState NewState()
    {
        var now = DateTime.UtcNow;
        return new FakeState
        {
            Auth = true,
            Nodes =
            {
                new FakeNode
                {
                    Uid = "root-my-files",
                    ParentUid = "",
                    Name = "my-files",
                    Type = "folder",
                    CreatedUtc = now,
                    ModifiedUtc = now,
                },
            },
        };
    }

    public void Replace(FakeState state) => State = state;

    public void Save()
    {
        var tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(State, FakeJson.Pretty));
        File.Move(tmp, StateFile, overwrite: true);
    }

    public void LogInvocation(string[] args)
    {
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            cwd = Environment.CurrentDirectory,
            args,
        }, FakeJson.Compact);
        File.AppendAllText(InvocationFile, line + Environment.NewLine);
    }

    public string BlobPath(string sha1) => Path.Combine(BlobDir, sha1);

    public void SaveBlob(string sha1, byte[] bytes)
    {
        var path = BlobPath(sha1);
        if (!File.Exists(path))
            File.WriteAllBytes(path, bytes);
    }

    public byte[]? ReadBlob(string? sha1)
    {
        if (string.IsNullOrEmpty(sha1))
            return null;
        var path = BlobPath(sha1);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void Dispose() => _lock.Dispose();
}
