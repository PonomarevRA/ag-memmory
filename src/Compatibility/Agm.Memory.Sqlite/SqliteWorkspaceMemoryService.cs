using System.Globalization;
using System.Text.Json;
using Agm.Memory;
using Agm.Memory.Abstractions;
using Microsoft.Data.Sqlite;

namespace Agm.Memory.Sqlite;

public sealed class SqliteWorkspaceMemoryService(
    ISqliteConnectionSource database,
    TimeProvider timeProvider,
    IMemoryScopeValidator? scopeValidator = null) : IWorkspaceMemoryService, IWorkspaceMemoryWriter
{
    public const int MaximumMemoryCharacters = 16_000;
    private const int MaximumReasonCharacters = 200;
    private const int MaximumProvenanceItems = 100;
    private readonly IMemoryScopeValidator scopeValidator =
        scopeValidator ?? AllowAllMemoryScopeValidator.Instance;

    public async Task<WorkspaceMemorySnapshot?> GetAsync(
        Guid workspaceId, CancellationToken cancellationToken, bool includeContent = false)
    {
        ValidateWorkspaceId(workspaceId);
        if (!await scopeValidator.WorkspaceExistsAsync(workspaceId, cancellationToken)) return null;
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = includeContent
            ? """
              SELECT COALESCE(s.enabled,1),COALESCE(s.version,0),m.version,m.reason,
                LENGTH(m.content),m.provenance,m.updated_at,m.content
              FROM (SELECT $workspace AS workspace_id) scope
              LEFT JOIN workspace_memory_settings s ON s.workspace_id=scope.workspace_id
              LEFT JOIN workspace_memory m ON m.workspace_id=scope.workspace_id;
              """
            : """
              SELECT COALESCE(s.enabled,1),COALESCE(s.version,0),m.version,m.reason,
                LENGTH(m.content),m.provenance,m.updated_at
              FROM (SELECT $workspace AS workspace_id) scope
              LEFT JOIN workspace_memory_settings s ON s.workspace_id=scope.workspace_id
              LEFT JOIN workspace_memory m ON m.workspace_id=scope.workspace_id;
              """;
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var characters = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
        var content = includeContent && !reader.IsDBNull(7) ? reader.GetString(7) : null;
        return new(workspaceId, reader.GetInt32(0) == 1, reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), characters, (characters + 3) / 4,
            reader.IsDBNull(5) ? [] : JsonSerializer.Deserialize<WorkspaceMemoryProvenance[]>(reader.GetString(5)) ?? [],
            reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            content);
    }

    public async Task<WorkspaceMemoryMutationResult> SetEnabledAsync(
        Guid workspaceId, bool enabled, long expectedVersion, CancellationToken cancellationToken)
    {
        var snapshot = await GetAsync(workspaceId, cancellationToken);
        if (snapshot is null) return new(WorkspaceMemoryMutationOutcome.NotFound, null);
        if (snapshot.SettingsVersion != expectedVersion)
            return new(WorkspaceMemoryMutationOutcome.Conflict, snapshot);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspace_memory_settings(workspace_id,enabled,version,updated_at)
            VALUES($workspace,$enabled,1,$now)
            ON CONFLICT(workspace_id) DO UPDATE SET enabled=$enabled,version=version+1,updated_at=$now
            WHERE version=$expected;
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        command.Parameters.AddWithValue("$now", Date(timeProvider.GetUtcNow()));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            return new(WorkspaceMemoryMutationOutcome.Conflict, await GetAsync(workspaceId, cancellationToken));
        if (!enabled)
        {
            await using var cancel = connection.CreateCommand();
            cancel.CommandText = """
                UPDATE workspace_memory_candidates SET status=3,failure_code='WorkspaceMemoryDisabled',
                  lease_expires_at=NULL,updated_at=$now WHERE workspace_id=$workspace AND status IN (0,1);
                """;
            cancel.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
            cancel.Parameters.AddWithValue("$now", Date(timeProvider.GetUtcNow()));
            await cancel.ExecuteNonQueryAsync(cancellationToken);
        }
        return new(WorkspaceMemoryMutationOutcome.Updated, await GetAsync(workspaceId, cancellationToken));
    }

    public async Task<WorkspaceMemoryMutationResult> ForgetAsync(
        Guid workspaceId, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM workspace_memory WHERE workspace_id=$workspace AND version=$version;";
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        command.Parameters.AddWithValue("$version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            return new(WorkspaceMemoryMutationOutcome.Updated, await GetAsync(workspaceId, cancellationToken));
        var current = await GetAsync(workspaceId, cancellationToken);
        return current is null ? new(WorkspaceMemoryMutationOutcome.NotFound, null)
            : new(WorkspaceMemoryMutationOutcome.Conflict, current);
    }

    public async Task<WorkspaceMemoryInjection?> GetInjectionAsync(
        Guid workspaceId, CancellationToken cancellationToken)
    {
        ValidateWorkspaceId(workspaceId);
        if (!await scopeValidator.WorkspaceExistsAsync(workspaceId, cancellationToken)) return null;
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.content,m.reason,m.version,m.provenance FROM workspace_memory m
            LEFT JOIN workspace_memory_settings s ON s.workspace_id=m.workspace_id
            WHERE m.workspace_id=$workspace AND COALESCE(s.enabled,1)=1;
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                JsonSerializer.Deserialize<WorkspaceMemoryProvenance[]>(reader.GetString(3)) ?? [])
            : null;
    }

    public async Task<WorkspaceMemoryWriteResult> WriteAsync(
        Guid workspaceId,
        string content,
        string reason,
        IReadOnlyList<WorkspaceMemoryProvenance> provenance,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ValidateWorkspaceId(workspaceId);
        ArgumentNullException.ThrowIfNull(provenance);
        if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > MaximumReasonCharacters)
            throw new ArgumentOutOfRangeException(nameof(reason));
        if (provenance.Count > MaximumProvenanceItems ||
            provenance.Any(item => item.WorkspaceId != workspaceId))
            throw new ArgumentException("Memory provenance does not match the workspace.", nameof(provenance));
        var sanitized = MemorySecretRedactor.Redact(content ?? string.Empty).Trim();
        if (sanitized.Length is 0 or > MaximumMemoryCharacters)
            throw new ArgumentOutOfRangeException(nameof(content));
        if (!await scopeValidator.WorkspaceExistsAsync(workspaceId, cancellationToken))
            return new(WorkspaceMemoryWriteOutcome.NotFound, null);

        var current = await GetAsync(workspaceId, cancellationToken, includeContent: true);
        if (current is null) return new(WorkspaceMemoryWriteOutcome.NotFound, null);
        if (!current.Enabled) return new(WorkspaceMemoryWriteOutcome.Disabled, current);
        if ((current.Version ?? 0) != expectedVersion)
            return new(WorkspaceMemoryWriteOutcome.Conflict, current);

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspace_memory(workspace_id,content,reason,version,provenance,updated_at)
            VALUES($workspace,$content,$reason,$version,$provenance,$now)
            ON CONFLICT(workspace_id) DO UPDATE SET content=$content,reason=$reason,
              version=$version,provenance=$provenance,updated_at=$now
            WHERE version=$expected;
            """;
        command.Parameters.AddWithValue("$workspace", workspaceId.ToString("D"));
        command.Parameters.AddWithValue("$content", sanitized);
        command.Parameters.AddWithValue("$reason", reason.Trim());
        command.Parameters.AddWithValue("$version", expectedVersion + 1);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        command.Parameters.AddWithValue("$provenance", JsonSerializer.Serialize(provenance));
        command.Parameters.AddWithValue("$now", Date(timeProvider.GetUtcNow()));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            return new(WorkspaceMemoryWriteOutcome.Conflict,
                await GetAsync(workspaceId, cancellationToken, includeContent: true));
        return new(WorkspaceMemoryWriteOutcome.Updated,
            await GetAsync(workspaceId, cancellationToken, includeContent: true));
    }

    private static void ValidateWorkspaceId(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
            throw new ArgumentException("Workspace id must not be empty.", nameof(workspaceId));
    }

    private static string Date(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
