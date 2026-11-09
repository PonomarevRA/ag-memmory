namespace Agm.Memory.Sqlite;

/// <summary>Creates only tables owned by Agm.Memory. Safe to call repeatedly.</summary>
public static class SqliteMemorySchema
{
    public const int CurrentVersion = 1;

    public static async Task InitializeAsync(
        ISqliteConnectionSource connectionSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionSource);
        await using var connection = await connectionSource.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS agm_memory_schema (
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO agm_memory_schema(component,version)
            VALUES('Agm.Memory',1);
            CREATE TABLE IF NOT EXISTS workspace_memory_settings (
                workspace_id TEXT PRIMARY KEY,
                enabled INTEGER NOT NULL DEFAULT 1,
                version INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS workspace_memory (
                workspace_id TEXT PRIMARY KEY,
                content TEXT NOT NULL,
                reason TEXT NOT NULL,
                version INTEGER NOT NULL,
                provenance TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS workspace_memory_candidates (
                candidate_id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                chat_id TEXT NOT NULL,
                run_id TEXT NULL,
                message_id TEXT NOT NULL,
                execution_id TEXT NOT NULL UNIQUE,
                content TEXT NOT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                attempts INTEGER NOT NULL DEFAULT 0,
                next_attempt_at TEXT NOT NULL,
                lease_expires_at TEXT NULL,
                failure_code TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_workspace_memory_candidates_due
                ON workspace_memory_candidates(status,next_attempt_at,lease_expires_at);
            CREATE TABLE IF NOT EXISTS fanout_memory (
                run_id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                parent_chat_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_fanout_memory_expires_at
                ON fanout_memory(expires_at);
            CREATE TABLE IF NOT EXISTS fanout_memory_events (
                run_id TEXT NOT NULL,
                event_key TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY(run_id,event_key),
                FOREIGN KEY(run_id) REFERENCES fanout_memory(run_id) ON DELETE CASCADE
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "SELECT version FROM agm_memory_schema WHERE component='Agm.Memory';";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (version != CurrentVersion)
            throw new InvalidOperationException(
                $"Agm.Memory SQLite schema version {version} is not supported; expected {CurrentVersion}.");
        await transaction.CommitAsync(cancellationToken);
    }
}
