using Agm.Memory.Abstractions;
using Agm.Memory;
using Microsoft.Data.Sqlite;

namespace Agm.Memory.Sqlite;

public sealed class SqliteFanOutMemoryStore(
    ISqliteConnectionSource database,
    TimeProvider timeProvider,
    IMemoryScopeValidator? scopeValidator = null) : IFanOutMemoryStore
{
    private readonly IMemoryScopeValidator scopeValidator =
        scopeValidator ?? AllowAllMemoryScopeValidator.Instance;
    public const int MaximumAppendCharacters = FanOutMemoryLimits.MaximumAppendCharacters;
    public const int MaximumMemoryCharacters = 16_000;
    public static readonly TimeSpan MinimumTtl = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumTtl = TimeSpan.FromHours(24);

    public async Task<FanOutMemoryWriteResult> AppendAsync(
        FanOutMemoryScope scope, string content, string? captureKey, long expectedVersion, TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (!IsSafeCaptureKey(captureKey)) throw new ArgumentException("Capture key is invalid.", nameof(captureKey));
        if (ttl < MinimumTtl || ttl > MaximumTtl) throw new ArgumentOutOfRangeException(nameof(ttl));
        var sanitized = MemorySecretRedactor.Redact(content ?? string.Empty).Trim();
        if (sanitized.Length is 0 or > MaximumAppendCharacters) throw new ArgumentOutOfRangeException(nameof(content));
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await scopeValidator.FanOutScopeExistsAsync(scope, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new FanOutMemoryWriteResult(FanOutMemoryWriteOutcome.Conflict, null);
        }
        var existing = await ReadWithinTransactionAsync(connection, transaction, scope, cancellationToken);
        if (existing is null &&
            await RunMemoryExistsAsync(connection, transaction, scope.RunId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new FanOutMemoryWriteResult(FanOutMemoryWriteOutcome.Conflict, null);
        }
        var now = timeProvider.GetUtcNow();
        if (existing is not null && existing.ExpiresAt <= now)
        {
            await DeleteAsync(connection, transaction, scope.RunId, cancellationToken);
            existing = null;
        }
        if (captureKey is not null && existing is not null &&
            await CaptureExistsAsync(connection, transaction, scope.RunId, captureKey, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new FanOutMemoryWriteResult(FanOutMemoryWriteOutcome.Unchanged, existing);
        }
        if ((existing?.Version ?? 0) != expectedVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new FanOutMemoryWriteResult(FanOutMemoryWriteOutcome.Conflict, existing);
        }
        var combined = existing is null ? sanitized : $"{existing.Content}\n{sanitized}";
        if (combined.Length > MaximumMemoryCharacters)
            combined = MemoryContentFitter.Fit(combined, MaximumMemoryCharacters);
        var entry = new FanOutMemoryEntry(scope, expectedVersion + 1, combined,
            existing?.CreatedAt ?? now, now, now + ttl);
        if (!await WriteAsync(connection, transaction, entry, existing is null, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new FanOutMemoryWriteResult(FanOutMemoryWriteOutcome.Conflict, existing);
        }
        if (captureKey is not null)
        {
            await InsertCaptureAsync(connection, transaction, scope.RunId, captureKey, now, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new FanOutMemoryWriteResult(FanOutMemoryWriteOutcome.Updated, entry);
    }

    public async Task<FanOutMemoryEntry?> ReadAsync(FanOutMemoryScope scope, CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var entry = await ReadWithinTransactionAsync(connection, transaction, scope, cancellationToken);
        if (entry is not null && entry.ExpiresAt <= timeProvider.GetUtcNow())
        {
            await DeleteAsync(connection, transaction, scope.RunId, cancellationToken);
            entry = null;
        }
        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    public async Task<FanOutMemoryStatus?> GetStatusAsync(
        FanOutMemoryScope scope, CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version,length(content),updated_at,expires_at FROM fanout_memory WHERE run_id=$runId AND workspace_id=$workspaceId AND parent_chat_id=$parentChatId;";
        AddScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var characters = reader.GetInt32(1);
        var updated = DateTimeOffset.Parse(reader.GetString(2));
        var expires = DateTimeOffset.Parse(reader.GetString(3));
        return new FanOutMemoryStatus(scope.RunId, scope.ParentChatId, reader.GetInt64(0),
            characters, (characters + 3) / 4, updated, expires, expires <= timeProvider.GetUtcNow());
    }

    public async Task<FanOutMemoryClearOutcome> ClearAsync(
        FanOutMemoryScope scope, long? expectedVersion, CancellationToken cancellationToken)
    {
        ValidateScope(scope);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = expectedVersion is null
            ? "DELETE FROM fanout_memory WHERE run_id=$runId AND workspace_id=$workspaceId AND parent_chat_id=$parentChatId;"
            : "DELETE FROM fanout_memory WHERE run_id=$runId AND workspace_id=$workspaceId AND parent_chat_id=$parentChatId AND version=$version;";
        AddScope(command, scope);
        if (expectedVersion is not null) command.Parameters.AddWithValue("$version", expectedVersion.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1) return FanOutMemoryClearOutcome.Cleared;
        var current = await ReadAsync(scope, cancellationToken);
        return current is null ? FanOutMemoryClearOutcome.NotFound : FanOutMemoryClearOutcome.Conflict;
    }

    private static async Task<FanOutMemoryEntry?> ReadWithinTransactionAsync(
        SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        FanOutMemoryScope scope, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT version,content,created_at,updated_at,expires_at FROM fanout_memory WHERE run_id=$runId AND workspace_id=$workspaceId AND parent_chat_id=$parentChatId;";
        AddScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new FanOutMemoryEntry(scope, reader.GetInt64(0), reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)), DateTimeOffset.Parse(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4))) : null;
    }

    private static async Task<bool> WriteAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, FanOutMemoryEntry entry, bool insert, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = insert
            ? "INSERT INTO fanout_memory(run_id,workspace_id,parent_chat_id,version,content,created_at,updated_at,expires_at) VALUES($runId,$workspaceId,$parentChatId,$version,$content,$created,$updated,$expires);"
            : "UPDATE fanout_memory SET version=$version,content=$content,updated_at=$updated,expires_at=$expires WHERE run_id=$runId AND workspace_id=$workspaceId AND parent_chat_id=$parentChatId AND version=$expectedVersion;";
        AddScope(command, entry.Scope); command.Parameters.AddWithValue("$version", entry.Version); command.Parameters.AddWithValue("$content", entry.Content);
        command.Parameters.AddWithValue("$created", entry.CreatedAt.ToString("O")); command.Parameters.AddWithValue("$updated", entry.UpdatedAt.ToString("O")); command.Parameters.AddWithValue("$expires", entry.ExpiresAt.ToString("O"));
        if (!insert) command.Parameters.AddWithValue("$expectedVersion", entry.Version - 1);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task<bool> CaptureExistsAsync(
        SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        Guid runId, string captureKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT 1 FROM fanout_memory_events WHERE run_id=$runId AND event_key=$key LIMIT 1;";
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        command.Parameters.AddWithValue("$key", captureKey);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> RunMemoryExistsAsync(
        SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        Guid runId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT 1 FROM fanout_memory WHERE run_id=$runId LIMIT 1;";
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task InsertCaptureAsync(
        SqliteConnection connection, System.Data.Common.DbTransaction transaction,
        Guid runId, string captureKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO fanout_memory_events(run_id,event_key,created_at) VALUES($runId,$key,$created);";
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        command.Parameters.AddWithValue("$key", captureKey);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, Guid runId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM fanout_memory WHERE run_id=$runId;"; command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddScope(SqliteCommand command, FanOutMemoryScope scope)
    {
        command.Parameters.AddWithValue("$runId", scope.RunId.ToString("D")); command.Parameters.AddWithValue("$workspaceId", scope.WorkspaceId.ToString("D")); command.Parameters.AddWithValue("$parentChatId", scope.ParentChatId.ToString("D"));
    }
    private static void ValidateScope(FanOutMemoryScope scope)
    {
        if (scope.WorkspaceId == Guid.Empty || scope.ParentChatId == Guid.Empty || scope.RunId == Guid.Empty) throw new ArgumentException("Fan-out memory scope must be complete.");
    }
    private static bool IsSafeCaptureKey(string? value) => value is null ||
        value.Length is > 0 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
}
