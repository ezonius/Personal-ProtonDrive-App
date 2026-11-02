using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PdSync.Config;
using PdSync.Proton;

namespace PdSync.State;

public enum EntryKind
{
    File,
    Folder,
}

/// <summary>
/// One row of the <c>entries</c> table: the last synced snapshot for a path.
/// <see cref="PathKey"/> is the folded identity; <see cref="Path"/> preserves
/// the casing captured at the last sync.
/// </summary>
public sealed record EntryRecord(
    string PathKey,
    string Path,
    EntryKind Kind,
    long? Size,
    long? MtimeMs,
    string? Sha1,
    string? RemoteUid,
    string? RemoteRevUid,
    long? RemoteSize,
    long? RemoteMtimeMs,
    string? RemoteSha1,
    bool LocalPresent,
    bool RemotePresent,
    bool Frozen,
    DateTimeOffset? LastSyncedAt);

/// <summary>
/// One remote node that pdsync moved to Proton Trash. The UID is the only
/// unambiguous identity the CLI exposes, so it is recorded with the folded path
/// key at the moment the trash action commits.
/// </summary>
public sealed record RemoteTrashRecord(
    string PathKey,
    string Path,
    string RemoteUid,
    DateTimeOffset TrashedAt,
    long RunId);

public sealed record PendingRecord(
    long Id,
    string Op,
    string Path,
    string? PayloadJson,
    DateTimeOffset StartedAt);

public sealed record RunRecord(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,
    string? StatsJson);

public class StateException : Exception
{
    public StateException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Raised by the read-only schema check when an existing database was written
/// by an older schema version. Only <see cref="StateStore.OpenReadOnly"/> uses
/// this; a writable open migrates instead. Callers that must not write state
/// (for example <c>doctor</c>) can report it as a warning rather than a failure.
/// </summary>
public sealed class StateSchemaMismatchException : StateException
{
    public StateSchemaMismatchException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Raised when an existing database was written by a newer schema than this
/// build understands. This is not a migratable mismatch: upgrading pdsync is
/// the only remedy, so callers must keep treating it as a failure.
/// </summary>
public sealed class StateSchemaTooNewException : StateException
{
    public StateSchemaTooNewException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Per-sync SQLite state. This is the only source of truth for "what was synced
/// last time" (SPEC §7). One store per sync pair; the database is named by a
/// stable hash of the two roots so renaming a sync keeps its history.
/// </summary>
public sealed class StateStore : IDisposable
{
    public const int SchemaVersion = 1;

    private readonly SqliteConnection _connection;
    private SqliteTransaction? _transaction;
    private bool _disposed;

    public string DatabasePath { get; }
    public string SyncId { get; }
    public string SyncName { get; }
    public string LocalRoot { get; }
    public string RemoteRoot { get; }
    public bool IsReadOnly { get; }
    public bool IsInMemory { get; }

    private StateStore(
        SqliteConnection connection,
        string databasePath,
        string syncId,
        string syncName,
        string localRoot,
        string remoteRoot,
        bool isReadOnly,
        bool isInMemory)
    {
        _connection = connection;
        DatabasePath = databasePath;
        SyncId = syncId;
        SyncName = syncName;
        LocalRoot = localRoot;
        RemoteRoot = remoteRoot;
        IsReadOnly = isReadOnly;
        IsInMemory = isInMemory;
    }

    public static StateStore Open(PdsyncConfig config, SyncConfig sync) =>
        OpenCore(config, sync, readOnly: false);

    /// <summary>
    /// Opens the state without creating or migrating anything on disk. If the
    /// database does not exist, the store is backed by an in-memory empty
    /// baseline, which is exactly what a first <c>plan</c> needs.
    /// </summary>
    public static StateStore OpenReadOnly(PdsyncConfig config, SyncConfig sync) =>
        OpenCore(config, sync, readOnly: true);

    /// <summary>
    /// Opens an in-memory empty baseline without touching the on-disk database.
    /// Used by <c>rebuild --dry-run</c>: the preview must ignore the old entries
    /// and must still work when the existing file is corrupt.
    /// </summary>
    public static StateStore OpenEmpty(PdsyncConfig config, SyncConfig sync) =>
        OpenCore(config, sync, readOnly: true, forceInMemory: true);

    private static StateStore OpenCore(
        PdsyncConfig config,
        SyncConfig sync,
        bool readOnly,
        bool forceInMemory = false)
    {
        var syncId = ComputeSyncId(sync.LocalRoot, sync.RemoteRoot);
        var stateDirectory = Path.Combine(config.StateDir, "state");
        var databasePath = Path.Combine(stateDirectory, syncId + ".sqlite");
        var existed = File.Exists(databasePath);

        string dataSource;
        SqliteOpenMode mode;
        var inMemory = false;

        if (forceInMemory || (readOnly && !existed))
        {
            dataSource = ":memory:";
            mode = SqliteOpenMode.ReadWriteCreate;
            inMemory = true;
        }
        else if (readOnly)
        {
            dataSource = databasePath;
            mode = SqliteOpenMode.ReadOnly;
        }
        else
        {
            Directory.CreateDirectory(stateDirectory);
            dataSource = databasePath;
            mode = SqliteOpenMode.ReadWriteCreate;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = mode,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        try
        {
            Configure(connection, readOnly);

            if (readOnly && existed && !inMemory)
            {
                var version = ReadUserVersion(connection);
                if (version > SchemaVersion)
                {
                    throw new StateSchemaTooNewException(NewerSchemaMessage(version));
                }

                if (version < SchemaVersion)
                {
                    throw new StateSchemaMismatchException(
                        $"State database schema v{version} is not current (v{SchemaVersion}); " +
                        "run pdsync run to migrate it.");
                }
            }
            else
            {
                // An in-memory preview must never take the writable migration /
                // backup path just because a file happens to exist on disk.
                Migrate(connection, databasePath, existed && !inMemory);
                // remote_trash is additive and created lazily so an existing v1
                // DB keeps working, including in read-only plan/doctor paths.
                EnsureRemoteTrashTable(connection);
            }

            var store = new StateStore(
                connection,
                databasePath,
                syncId,
                sync.Name,
                sync.LocalRoot,
                sync.RemoteRoot,
                readOnly,
                inMemory);

            if (!readOnly || inMemory)
                store.EnsureSyncRow();

            return store;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static string ComputeSyncId(string localRoot, string remoteRoot)
    {
        var material = PathUtil.IdentityKey(localRoot) + "\n" + RemotePath.IdentityKey(RemotePath.Normalize(remoteRoot));
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    // ------------------------------------------------------------------ entries

    public EntryRecord? GetEntry(string pathKey)
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = """
            SELECT path_key, path, kind, size, mtime_ms, sha1,
                   remote_uid, remote_rev_uid, remote_size, remote_mtime_ms, remote_sha1,
                   local_present, remote_present, frozen, last_synced_at
            FROM entries
            WHERE sync_id = $sync AND path_key = $key;
            """;
        command.Parameters.AddWithValue("$sync", SyncId);
        command.Parameters.AddWithValue("$key", pathKey);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    public IReadOnlyList<EntryRecord> LoadEntries(bool includeFrozen = false)
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        var filter = includeFrozen ? "" : " AND frozen = 0";
        command.CommandText = $"""
            SELECT path_key, path, kind, size, mtime_ms, sha1,
                   remote_uid, remote_rev_uid, remote_size, remote_mtime_ms, remote_sha1,
                   local_present, remote_present, frozen, last_synced_at
            FROM entries
            WHERE sync_id = $sync{filter}
            ORDER BY path_key;
            """;
        command.Parameters.AddWithValue("$sync", SyncId);

        var result = new List<EntryRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(ReadEntry(reader));
        return result;
    }

    public int CountEntries(bool includeFrozen = false)
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM entries WHERE sync_id = $sync" +
                              (includeFrozen ? "" : " AND frozen = 0") + ";";
        command.Parameters.AddWithValue("$sync", SyncId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void UpsertEntry(EntryRecord entry)
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = """
            INSERT INTO entries (
                sync_id, path_key, path, kind, size, mtime_ms, sha1,
                remote_uid, remote_rev_uid, remote_size, remote_mtime_ms, remote_sha1,
                local_present, remote_present, frozen, last_synced_at)
            VALUES (
                $sync, $key, $path, $kind, $size, $mtime, $sha1,
                $ruid, $rrev, $rsize, $rmtime, $rsha1,
                $local, $remote, $frozen, $synced)
            ON CONFLICT(sync_id, path_key) DO UPDATE SET
                path = excluded.path,
                kind = excluded.kind,
                size = excluded.size,
                mtime_ms = excluded.mtime_ms,
                sha1 = excluded.sha1,
                remote_uid = excluded.remote_uid,
                remote_rev_uid = excluded.remote_rev_uid,
                remote_size = excluded.remote_size,
                remote_mtime_ms = excluded.remote_mtime_ms,
                remote_sha1 = excluded.remote_sha1,
                local_present = excluded.local_present,
                remote_present = excluded.remote_present,
                frozen = excluded.frozen,
                last_synced_at = excluded.last_synced_at;
            """;

        AddEntryParameters(command, entry);
        command.ExecuteNonQuery();
    }

    public void DeleteEntry(string pathKey)
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = "DELETE FROM entries WHERE sync_id = $sync AND path_key = $key;";
        command.Parameters.AddWithValue("$sync", SyncId);
        command.Parameters.AddWithValue("$key", pathKey);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Keeps a tombstone for a path that became ignored (SPEC §3.3 rule 3): it is
    /// no longer active, was not deleted on either side, and must not count as a
    /// deletion on the next run.
    /// </summary>
    public void MarkFrozen(string pathKey)
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = """
            UPDATE entries
            SET frozen = 1, local_present = 0, remote_present = 0
            WHERE sync_id = $sync AND path_key = $key;
            """;
        command.Parameters.AddWithValue("$sync", SyncId);
        command.Parameters.AddWithValue("$key", pathKey);
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------ remote trash

    /// <summary>
    /// Records the node the executor is about to move to Proton Trash. The row
    /// is committed before the CLI call because the remote side effect cannot be
    /// rolled back with a local transaction.
    /// </summary>
    public void RecordRemoteTrash(
        string path,
        string pathKey,
        string remoteUid,
        long runId,
        DateTimeOffset? trashedAt = null)
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = """
            INSERT INTO remote_trash (sync_id, path_key, path, remote_uid, trashed_at, run_id)
            VALUES ($sync, $key, $path, $uid, $trashed, $run)
            ON CONFLICT(sync_id, path_key) DO UPDATE SET
                path = excluded.path,
                remote_uid = excluded.remote_uid,
                trashed_at = excluded.trashed_at,
                run_id = excluded.run_id;
            """;
        command.Parameters.AddWithValue("$sync", SyncId);
        command.Parameters.AddWithValue("$key", pathKey);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$uid", remoteUid);
        command.Parameters.AddWithValue("$trashed", FormatTimestamp(trashedAt ?? DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$run", runId);
        command.ExecuteNonQuery();
    }

    public RemoteTrashRecord? GetRemoteTrash(string pathKey)
    {
        ThrowIfDisposed();
        // A v1 database created before this feature has no table; a read-only
        // open must treat that as "no records" instead of failing.
        if (!RemoteTrashTableExists())
            return null;

        using var command = CreateCommand();
        command.CommandText = """
            SELECT path_key, path, remote_uid, trashed_at, run_id
            FROM remote_trash
            WHERE sync_id = $sync AND path_key = $key;
            """;
        command.Parameters.AddWithValue("$sync", SyncId);
        command.Parameters.AddWithValue("$key", pathKey);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new RemoteTrashRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(3)),
            reader.GetInt64(4));
    }

    public void DeleteRemoteTrash(string pathKey)
    {
        ThrowIfDisposed();
        if (!RemoteTrashTableExists())
            return;

        using var command = CreateCommand();
        command.CommandText = "DELETE FROM remote_trash WHERE sync_id = $sync AND path_key = $key;";
        command.Parameters.AddWithValue("$sync", SyncId);
        command.Parameters.AddWithValue("$key", pathKey);
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ pending

    public long AddPending(string op, string path, string? payloadJson = null)
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = """
            INSERT INTO pending (sync_id, op, path, payload_json, started_at)
            VALUES ($sync, $op, $path, $payload, $started);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$sync", SyncId);
        command.Parameters.AddWithValue("$op", op);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$payload", (object?)payloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", FormatTimestamp(DateTimeOffset.UtcNow));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<PendingRecord> LoadPending()
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = """
            SELECT id, op, path, payload_json, started_at
            FROM pending
            WHERE sync_id = $sync
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$sync", SyncId);

        var result = new List<PendingRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new PendingRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                ParseTimestamp(reader.GetString(4))));
        }

        return result;
    }

    public void RemovePending(long id)
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = "DELETE FROM pending WHERE id = $id AND sync_id = $sync;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$sync", SyncId);
        command.ExecuteNonQuery();
    }

    public int ClearPending()
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = "DELETE FROM pending WHERE sync_id = $sync;";
        command.Parameters.AddWithValue("$sync", SyncId);
        return command.ExecuteNonQuery();
    }

    // --------------------------------------------------------------------- runs

    public long StartRun()
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = """
            INSERT INTO runs (sync_id, started_at, status)
            VALUES ($sync, $started, 'running');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$sync", SyncId);
        command.Parameters.AddWithValue("$started", FormatTimestamp(DateTimeOffset.UtcNow));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void FinishRun(long id, string status, string? statsJson = null)
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = """
            UPDATE runs
            SET finished_at = $finished, status = $status, stats_json = $stats
            WHERE id = $id AND sync_id = $sync;
            """;
        command.Parameters.AddWithValue("$finished", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$stats", (object?)statsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$sync", SyncId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<RunRecord> LoadRuns(int limit = 20)
    {
        ThrowIfDisposed();
        using var command = CreateCommand();
        command.CommandText = """
            SELECT id, started_at, finished_at, status, stats_json
            FROM runs
            WHERE sync_id = $sync
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sync", SyncId);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<RunRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new RunRecord(
                reader.GetInt64(0),
                ParseTimestamp(reader.GetString(1)),
                reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetString(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    // -------------------------------------------------------------- transactions

    /// <summary>
    /// Runs <paramref name="action"/> in one transaction. Nested transactions are
    /// rejected on purpose: the pending-row + state-update pair must be atomic.
    /// </summary>
    public void InTransaction(Action action)
    {
        ThrowIfDisposed();
        if (_transaction is not null)
            throw new InvalidOperationException("Nested transactions are not supported.");

        using var transaction = _connection.BeginTransaction();
        _transaction = transaction;
        try
        {
            action();
            transaction.Commit();
        }
        finally
        {
            _transaction = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _transaction?.Dispose();
        _connection.Dispose();
    }

    // ------------------------------------------------------------------ internals

    private void EnsureSyncRow()
    {
        using var command = CreateCommand();
        command.CommandText = """
            INSERT INTO syncs (id, name, local_root, remote_root, created_at, schema_ver)
            VALUES ($id, $name, $local, $remote, $created, $version)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, schema_ver = excluded.schema_ver;
            """;
        command.Parameters.AddWithValue("$id", SyncId);
        command.Parameters.AddWithValue("$name", SyncName);
        command.Parameters.AddWithValue("$local", LocalRoot);
        command.Parameters.AddWithValue("$remote", RemoteRoot);
        command.Parameters.AddWithValue("$created", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$version", SchemaVersion);
        command.ExecuteNonQuery();
    }

    private static void Configure(SqliteConnection connection, bool readOnly)
    {
        using var command = connection.CreateCommand();
        command.CommandText = readOnly
            ? """
              PRAGMA foreign_keys = ON;
              PRAGMA busy_timeout = 5000;
              """
            : """
              PRAGMA foreign_keys = ON;
              PRAGMA busy_timeout = 5000;
              PRAGMA synchronous = NORMAL;
              """;
        command.ExecuteNonQuery();
    }

    private static void Migrate(SqliteConnection connection, string databasePath, bool existed)
    {
        var version = ReadUserVersion(connection);
        if (version > SchemaVersion)
            throw new StateSchemaTooNewException(NewerSchemaMessage(version));

        if (version == SchemaVersion)
            return;

        if (existed && new FileInfo(databasePath).Length > 0)
            Backup(databasePath);

        if (version == 0)
        {
            using var transaction = connection.BeginTransaction();
            CreateSchemaV1(connection, transaction);
            SetUserVersion(connection, SchemaVersion, transaction);
            transaction.Commit();
            return;
        }

        throw new StateException($"No migration path from schema v{version} to v{SchemaVersion}.");
    }

    private static string NewerSchemaMessage(int version) =>
        $"State database schema v{version} is newer than this build (v{SchemaVersion}); upgrade pdsync.";

    private static void CreateSchemaV1(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE syncs (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                local_root TEXT NOT NULL,
                remote_root TEXT NOT NULL,
                created_at TEXT NOT NULL,
                schema_ver INTEGER NOT NULL
            );

            CREATE TABLE entries (
                sync_id TEXT NOT NULL,
                path_key TEXT NOT NULL,
                path TEXT NOT NULL,
                kind TEXT NOT NULL,
                size INTEGER,
                mtime_ms INTEGER,
                sha1 TEXT,
                remote_uid TEXT,
                remote_rev_uid TEXT,
                remote_size INTEGER,
                remote_mtime_ms INTEGER,
                remote_sha1 TEXT,
                local_present INTEGER NOT NULL DEFAULT 0,
                remote_present INTEGER NOT NULL DEFAULT 0,
                frozen INTEGER NOT NULL DEFAULT 0,
                last_synced_at TEXT,
                PRIMARY KEY (sync_id, path_key),
                FOREIGN KEY (sync_id) REFERENCES syncs(id) ON DELETE CASCADE
            );

            CREATE TABLE pending (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sync_id TEXT NOT NULL,
                op TEXT NOT NULL,
                path TEXT NOT NULL,
                payload_json TEXT,
                started_at TEXT NOT NULL,
                FOREIGN KEY (sync_id) REFERENCES syncs(id) ON DELETE CASCADE
            );

            CREATE TABLE runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sync_id TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                status TEXT NOT NULL,
                stats_json TEXT,
                FOREIGN KEY (sync_id) REFERENCES syncs(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_entries_sync ON entries(sync_id);
            CREATE INDEX ix_pending_sync ON pending(sync_id);
            CREATE INDEX ix_runs_sync_started ON runs(sync_id, started_at);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates the additive <c>remote_trash</c> table without bumping
    /// <c>user_version</c>: older v1 databases stay valid and read-only opens
    /// keep working.
    /// </summary>
    private static void EnsureRemoteTrashTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS remote_trash (
                sync_id TEXT NOT NULL,
                path_key TEXT NOT NULL,
                path TEXT NOT NULL,
                remote_uid TEXT NOT NULL,
                trashed_at TEXT NOT NULL,
                run_id INTEGER NOT NULL,
                PRIMARY KEY (sync_id, path_key),
                FOREIGN KEY (sync_id) REFERENCES syncs(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_remote_trash_sync ON remote_trash(sync_id);
            """;
        command.ExecuteNonQuery();
    }

    private bool RemoteTrashTableExists()
    {
        using var command = CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'remote_trash';";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void SetUserVersion(SqliteConnection connection, int version, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private static void Backup(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new StateException($"Cannot determine directory for {databasePath}");
        var name = $"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Path.GetFileNameWithoutExtension(databasePath)}.sqlite";
        File.Copy(databasePath, Path.Combine(directory, name), overwrite: false);
    }

    private SqliteCommand CreateCommand()
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        return command;
    }

    private void AddEntryParameters(SqliteCommand command, EntryRecord entry)
    {
        command.Parameters.AddWithValue("$sync", SyncId);
        command.Parameters.AddWithValue("$key", entry.PathKey);
        command.Parameters.AddWithValue("$path", entry.Path);
        command.Parameters.AddWithValue("$kind", entry.Kind switch
        {
            EntryKind.File => "file",
            EntryKind.Folder => "folder",
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown entry kind"),
        });
        command.Parameters.AddWithValue("$size", (object?)entry.Size ?? DBNull.Value);
        command.Parameters.AddWithValue("$mtime", (object?)entry.MtimeMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$sha1", (object?)entry.Sha1 ?? DBNull.Value);
        command.Parameters.AddWithValue("$ruid", (object?)entry.RemoteUid ?? DBNull.Value);
        command.Parameters.AddWithValue("$rrev", (object?)entry.RemoteRevUid ?? DBNull.Value);
        command.Parameters.AddWithValue("$rsize", (object?)entry.RemoteSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$rmtime", (object?)entry.RemoteMtimeMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$rsha1", (object?)entry.RemoteSha1 ?? DBNull.Value);
        command.Parameters.AddWithValue("$local", entry.LocalPresent ? 1 : 0);
        command.Parameters.AddWithValue("$remote", entry.RemotePresent ? 1 : 0);
        command.Parameters.AddWithValue("$frozen", entry.Frozen ? 1 : 0);
        command.Parameters.AddWithValue("$synced", entry.LastSyncedAt is null
            ? DBNull.Value
            : FormatTimestamp(entry.LastSyncedAt.Value));
    }

    private static EntryRecord ReadEntry(SqliteDataReader reader)
    {
        var kind = reader.GetString(2) switch
        {
            "file" => EntryKind.File,
            "folder" => EntryKind.Folder,
            var value => throw new StateException($"Unknown entry kind in state database: {value}"),
        };

        return new EntryRecord(
            reader.GetString(0),
            reader.GetString(1),
            kind,
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt64(11) != 0,
            reader.GetInt64(12) != 0,
            reader.GetInt64(13) != 0,
            reader.IsDBNull(14) ? null : ParseTimestamp(reader.GetString(14)));
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(StateStore));
    }
}
