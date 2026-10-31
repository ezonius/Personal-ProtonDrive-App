namespace PdSync.Exec;

/// <summary>A runtime failure in one sync/action (exit code 1).</summary>
public class SyncException : Exception
{
    public SyncException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>A refusal to execute because a safety guard tripped (exit code 6).</summary>
public sealed class SafetyException : SyncException
{
    public SafetyException(string message)
        : base(message)
    {
    }
}

/// <summary>Another process holds the per-sync run lock (exit code 5).</summary>
public sealed class LockException : SyncException
{
    public LockException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
