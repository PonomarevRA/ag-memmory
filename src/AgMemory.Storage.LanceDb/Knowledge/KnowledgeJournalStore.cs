using System.Text.Json;
using AgMemory.Contracts;
using Microsoft.Data.Sqlite;

namespace AgMemory.Storage.LanceDb;

/// <summary>
/// Opt-in transactional state plus durable receipt ledger. Migration fences legacy writers under their existing lease.
/// Receipts are never evicted: the namespace has a one-million-receipt bound; the database has a512MiB bound.
/// Preserved v1 files and transient SQLite rollback journals are separate from that database-byte quota.
/// </summary>
public sealed class KnowledgeJournalStore : IKnowledgeReplayStore
{
    private readonly string _directory;
    private readonly string _database;
    private readonly StorageLeaseGate _gate;
    private readonly KnowledgeFileStore _legacy;
    private readonly Action<string>? _checkpoint;
    private readonly int _maximumRequests;
    private const int MaximumStateBytes = 8 * 1024 * 1024;
    public const int MaximumDatabaseBytes = 512 * 1024 * 1024;
    public const int MaximumReceiptCount = 1_000_000;
    private const int DatabasePageSize = 4096;
    public int MaximumRequests => _maximumRequests;

    /// <summary>Selects only the server-configured knowledge namespace; construction performs no I/O or automatic migration.</summary>
    public KnowledgeJournalStore(string storagePath, bool taskWorking = false)
        : this(storagePath, taskWorking, null) { }

    /// <summary>Internal fault/resource-test seam; production uses the fixed maximum and exposes no fault command over MCP.</summary>
    internal KnowledgeJournalStore(string storagePath, bool taskWorking, Action<string>? checkpoint, int maximumRequests = MaximumReceiptCount)
    {
        if (maximumRequests is < 1 or > MaximumReceiptCount) throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        _maximumRequests = maximumRequests;
        _checkpoint = checkpoint;
        _directory = Path.Combine(Path.GetFullPath(storagePath), taskWorking ? ".agmemory-working" : ".agmemory-knowledge");
        _database = Path.Combine(_directory, "knowledge-v2.sqlite");
        _gate = new(_directory, taskWorking ? ".working.lock" : ".knowledge.lock");
        _legacy = taskWorking ? KnowledgeFileStore.ForTaskWorking(storagePath) : new(storagePath);
    }

    /// <summary>
    /// Explicitly imports one profile and all receipts, preserving exact legacy bytes. A crash before the fence leaves
    /// legacy authoritative; repeating migration replaces the unused imported snapshot before publishing the fence.
    /// </summary>
    public async Task MigrateAsync(string profileKey, CancellationToken token = default)
    {
        _legacy.PrepareMigrationLease(profileKey);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Prepare();
            if (IsFenced(profileKey)) { using var existing = Open(); ReadState(existing, profileKey); return; }
            var state = await _legacy.ReadForMigrationAsync(profileKey, token).ConfigureAwait(false);
            foreach (var (key, receipt) in state.Requests)
                if (key != receipt.Key) throw new InvalidDataException("Legacy receipt lookup key does not match its payload.");
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Execute(connection, "DELETE FROM receipts WHERE profile=$p", profileKey, transaction: transaction);
            Execute(connection, "DELETE FROM states WHERE profile=$p", profileKey, transaction: transaction);
            using (var capacity = Command(connection, "SELECT count(*) FROM receipts", transaction: transaction))
                if (Convert.ToInt64(capacity.ExecuteScalar()) + state.Requests.Count > MaximumRequests)
                    throw new InvalidOperationException("knowledge_capacity_exceeded");
            var stateJson = SerializeState(state);
            Execute(connection, "INSERT INTO states(profile,version,origin,json) VALUES($p,$v,$v,$j)", profileKey, state.Version, stateJson, transaction: transaction);
            foreach (var receipt in state.Requests.Values) InsertReceipt(connection, profileKey, receipt, transaction);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            _checkpoint?.Invoke("migration_import_committed");
            _legacy.FenceForJournal(profileKey, state.Version, token);
            _checkpoint?.Invoke("migration_fence_published");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Rollback is allowed only before any accepted journal mutation; otherwise restore/export needs an explicit operator workflow.</summary>
    public async Task RollbackBeforeWritesAsync(string profileKey, CancellationToken token = default)
    {
        _legacy.PrepareMigrationLease(profileKey);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Prepare(); RequireFence(profileKey);
            using var connection = Open();
            using var command = Command(connection, "SELECT version=origin FROM states WHERE profile=$p", profileKey);
            if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new InvalidOperationException("journal_rollback_has_new_writes");
            _legacy.RestoreBeforeJournalWrites(profileKey, token);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads only a fenced, migrated profile. The active aggregate omits receipts, which remain queryable in the ledger.</summary>
    public async Task<KnowledgeState> ReadAsync(string profileKey, CancellationToken cancellationToken)
    {
        _legacy.PrepareMigrationLease(profileKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Prepare(); RequireFence(profileKey); using var connection = Open(); return ReadState(connection, profileKey); }
        finally { _gate.Release(); }
    }

    /// <summary>Atomically CAS-publishes one aggregate and its one request receipt; stale writers retry without losing history.</summary>
    public async Task<bool> CompareExchangeAsync(string profileKey, long expectedVersion, KnowledgeState replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || replacement.Version != expectedVersion + 1 || replacement.Requests is null || replacement.Requests.Count != 1)
            throw new ArgumentException("A journal mutation must advance one version and contain one receipt.");
        var stateJson = SerializeState(replacement);
        var receipt = replacement.Requests.Values.Single();
        _legacy.PrepareMigrationLease(profileKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prepare(); RequireFence(profileKey);
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (ReadState(connection, profileKey, transaction).Version != expectedVersion) return false;
            using var duplicate = Command(connection, "SELECT 1 FROM receipts WHERE profile=$p AND key=$k", profileKey, key: receipt.Key, transaction: transaction);
            if (duplicate.ExecuteScalar() is not null) return false;
            using var capacity = Command(connection, "SELECT count(*) FROM receipts", transaction: transaction);
            if (Convert.ToInt64(capacity.ExecuteScalar()) >= MaximumRequests) throw new InvalidOperationException("knowledge_capacity_exceeded");
            Execute(connection, "UPDATE states SET version=$v,json=$j WHERE profile=$p", profileKey, replacement.Version, stateJson, transaction: transaction);
            InsertReceipt(connection, profileKey, receipt, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            _checkpoint?.Invoke("mutation_before_commit");
            transaction.Commit();
            _checkpoint?.Invoke("mutation_committed");
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Looks up the complete retained receipt, including requests older than the original 4096-entry bound.</summary>
    public async Task<KnowledgeRequestReceipt?> ReadRequestAsync(string profileKey, string requestKey, CancellationToken cancellationToken)
    {
        _legacy.PrepareMigrationLease(profileKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prepare(); RequireFence(profileKey); using var connection = Open();
            using var command = Command(connection, "SELECT json FROM receipts WHERE profile=$p AND key=$k", profileKey, key: requestKey);
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<KnowledgeRequestReceipt>(json)
                ?? throw new InvalidDataException("Invalid replay receipt.") : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<int> CountRequestsAsync(string profileKey, CancellationToken cancellationToken)
        => await CountRequestsCoreAsync(profileKey, false, cancellationToken).ConfigureAwait(false);

    /// <summary>Reports the same namespace-wide receipt count that the transactional capacity gate enforces.</summary>
    public async Task<int> CountNamespaceRequestsAsync(string profileKey, CancellationToken cancellationToken)
        => await CountRequestsCoreAsync(profileKey, true, cancellationToken).ConfigureAwait(false);

    private async Task<int> CountRequestsCoreAsync(string profileKey, bool entireNamespace, CancellationToken cancellationToken)
    {
        _legacy.PrepareMigrationLease(profileKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Prepare(); RequireFence(profileKey); using var connection = Open();
            using var command = Command(connection, entireNamespace ? "SELECT count(*) FROM receipts" : "SELECT count(*) FROM receipts WHERE profile=$p", entireNamespace ? null : profileKey);
            return Convert.ToInt32(command.ExecuteScalar());
        }
        finally { _gate.Release(); }
    }

    /// <summary>Opens a private DELETE-journal database so a namespace lease is also a complete backup snapshot boundary.</summary>
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using (var pageSize = connection.CreateCommand())
            {
                pageSize.CommandText = $"PRAGMA page_size={DatabasePageSize}; PRAGMA page_size;";
                if (Convert.ToInt64(pageSize.ExecuteScalar()) != DatabasePageSize) throw new InvalidDataException("Unsupported journal page size.");
            }
            Execute(connection, $"PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA max_page_count={MaximumDatabaseBytes / DatabasePageSize};");
            using var schema = Command(connection, "PRAGMA user_version");
            var schemaVersion = Convert.ToInt32(schema.ExecuteScalar());
            if (schemaVersion is not (0 or 2)) throw new InvalidDataException("Unsupported journal schema.");
            if (schemaVersion == 0)
            {
                using var tables = Command(connection, "SELECT count(*) FROM sqlite_master WHERE type='table'");
                if (Convert.ToInt64(tables.ExecuteScalar()) != 0) throw new InvalidDataException("Unrecognized journal database.");
                // Schema and schema-version commit together; an interrupted first open leaves an empty retryable database.
                using var transaction = connection.BeginTransaction();
                Execute(connection, """
                    CREATE TABLE states(profile TEXT PRIMARY KEY,version INTEGER NOT NULL,origin INTEGER NOT NULL,json TEXT NOT NULL);
                    CREATE TABLE receipts(profile TEXT NOT NULL,key TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(profile,key)) WITHOUT ROWID;
                    PRAGMA user_version=2;
                    """, transaction: transaction);
                transaction.Commit();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    /// <summary>Rejects linked database files and keeps the directory and database private before SQLite opens them.</summary>
    private void Prepare()
    {
        foreach (var path in new[] { Path.GetDirectoryName(_directory)!, _directory, _database, _database + "-journal", _database + "-wal", _database + "-shm" })
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
                throw new IOException("Journal paths cannot be symbolic links.");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!File.Exists(_database))
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var unused = new FileStream(_database, options);
        }
        if (new FileInfo(_database).Length > MaximumDatabaseBytes) throw new InvalidDataException("Journal database exceeds its byte limit.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_database, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Checks the minimal legacy-visible fence without accepting an unknown future schema as a migrated profile.</summary>
    private bool IsFenced(string profile)
    {
        var path = _legacy.MigrationPath(profile);
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Unsafe journal fence.");
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length > MaximumStateBytes) throw new InvalidDataException("Oversized journal fence.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var schema = document.RootElement.GetProperty("SchemaVersion").GetInt32();
        if (schema is not (1 or 2)) throw new InvalidDataException("Unsupported knowledge schema.");
        return schema == 2;
    }

    private void RequireFence(string profile)
    {
        if (!IsFenced(profile)) throw new InvalidOperationException("journal_migration_required");
    }

    /// <summary>Bounds state bytes independently of the durable receipt table; existing domain collection caps still apply.</summary>
    private string SerializeState(KnowledgeState state)
    {
        _legacy.ValidateJournalAggregate(state);
        var json = JsonSerializer.Serialize(state with { Requests = new(StringComparer.Ordinal) });
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumStateBytes) throw new InvalidDataException("Journal aggregate is too large.");
        return json;
    }

    /// <summary>Checks both SQL and JSON versions plus aggregate bounds before exposing a persisted generation.</summary>
    private KnowledgeState ReadState(SqliteConnection connection, string profile, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, "SELECT version,length(cast(json AS BLOB)),json FROM states WHERE profile=$p", profile, transaction: transaction);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Migrated journal aggregate is unavailable.");
        if (reader.GetInt64(1) > MaximumStateBytes) throw new InvalidDataException("Oversized journal aggregate.");
        var state = JsonSerializer.Deserialize<KnowledgeState>(reader.GetString(2)) ?? throw new InvalidDataException("Invalid journal aggregate.");
        _legacy.ValidateJournalAggregate(state);
        if (state.Version != reader.GetInt64(0) || state.Requests.Count != 0) throw new InvalidDataException("Journal aggregate version or receipt layout is invalid.");
        return state;
    }

    private static void InsertReceipt(SqliteConnection connection, string profile, KnowledgeRequestReceipt receipt, SqliteTransaction transaction) =>
        Execute(connection, "INSERT INTO receipts(profile,key,json) VALUES($p,$k,$j)", profile, json: JsonSerializer.Serialize(receipt), key: receipt.Key, transaction: transaction);

    /// <summary>Binds all data as SQLite parameters; SQL strings are fixed internal statements.</summary>
    private static SqliteCommand Command(SqliteConnection connection, string sql, string? profile = null,
        long version = 0, string? json = null, string? key = null, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        if (profile is not null) command.Parameters.AddWithValue("$p", profile);
        if (sql.Contains("$v", StringComparison.Ordinal)) command.Parameters.AddWithValue("$v", version);
        if (json is not null) command.Parameters.AddWithValue("$j", json);
        if (key is not null) command.Parameters.AddWithValue("$k", key);
        return command;
    }

    private static void Execute(SqliteConnection connection, string sql, string? profile = null,
        long version = 0, string? json = null, string? key = null, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, sql, profile, version, json, key, transaction);
        command.ExecuteNonQuery();
    }
}
