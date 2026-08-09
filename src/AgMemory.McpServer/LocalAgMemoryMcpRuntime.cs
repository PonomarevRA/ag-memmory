using System.Security.Cryptography;
using System.Text;
using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;

namespace AgMemory.McpServer;

/// <summary>Fixed local identity and exact scope supplied only by the MCP process environment.</summary>
public sealed record LocalAgMemoryMcpConfiguration(string StoragePath, ActorId Actor, MemoryScope Scope)
{
    public const string StoragePathVariable = "AGMEMORY_STORAGE_PATH";
    public const string ActorIdVariable = "AGMEMORY_ACTOR_ID";
    public const string TenantIdVariable = "AGMEMORY_TENANT_ID";
    public const string ProjectIdVariable = "AGMEMORY_PROJECT_ID";
    public const string WorkspaceIdVariable = "AGMEMORY_WORKSPACE_ID";
    public const string ChatIdVariable = "AGMEMORY_CHAT_ID";
    public const string RunIdVariable = "AGMEMORY_RUN_ID";

    public static LocalAgMemoryMcpConfiguration FromEnvironment() => Create(
        Environment.GetEnvironmentVariable(StoragePathVariable),
        Environment.GetEnvironmentVariable(ActorIdVariable),
        Environment.GetEnvironmentVariable(TenantIdVariable),
        Environment.GetEnvironmentVariable(ProjectIdVariable),
        Environment.GetEnvironmentVariable(WorkspaceIdVariable),
        Environment.GetEnvironmentVariable(ChatIdVariable),
        Environment.GetEnvironmentVariable(RunIdVariable));

    public static LocalAgMemoryMcpConfiguration Create(
        string? storagePath,
        string? actorId,
        string? tenantId,
        string? projectId = null,
        string? workspaceId = null,
        string? chatId = null,
        string? runId = null)
    {
        if (string.IsNullOrWhiteSpace(storagePath) || string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("AgMemory MCP requires an explicit storage path, actor ID, and tenant ID.");

        var scope = new MemoryScope(new ScopeId(tenantId), Optional(projectId), Optional(workspaceId), Optional(chatId), Optional(runId));
        scope.Validate();
        var actor = new ActorId(actorId);
        actor.Validate(nameof(actorId));
        return new(Path.GetFullPath(storagePath), actor, scope);
    }

    private static ScopeId? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : new ScopeId(value);
}

/// <summary>
/// Small local composition root for Codex. It never accepts actor, scope, storage path or durable IDs from a tool call.
/// </summary>
public sealed class LocalAgMemoryMcpRuntime : IAsyncDisposable
{
    private static readonly ContractVersion ContractVersion = new("agmemory-mcp-v1");
    private readonly LocalAgMemoryMcpConfiguration _configuration;
    private readonly LanceDbMemoryStore _store;
    private readonly MemoryCommandService _commands;

    public LocalAgMemoryMcpRuntime(LocalAgMemoryMcpConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _store = new LanceDbMemoryStore(new(configuration.StoragePath));
        _commands = new MemoryCommandService(
            _store,
            new ExactLocalAuthorization(configuration),
            new IdentityRedactor(),
            new LocalEmbeddingProvider(),
            new LocalEmbeddingPolicy(),
            new NoRetentionPolicy(),
            new SystemClock(),
            new GuidIds(),
            new MemoryCoreOptions(ContractVersion, ContractVersion));
    }

    public static LocalAgMemoryMcpRuntime FromEnvironment() => new(LocalAgMemoryMcpConfiguration.FromEnvironment());

    public async Task<IReadOnlyList<MemoryRecallItem>> RecallAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A recall query is required.", nameof(query));
        if (limit is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(limit), "Recall limit must be from 1 through 10.");

        var terms = Tokens(query);
        var now = DateTimeOffset.UtcNow;
        var scopes = new AuthorizedScopeSet([new ScopeSelector(_configuration.Scope)]);
        var records = await _store.ListAsync(scopes, cancellationToken).ConfigureAwait(false);
        return records
            .Where(record => record.Status == MemoryLifecycleStatus.Active && (record.ExpiresAt is null || record.ExpiresAt > now))
            .Select(record => new { Record = record, Score = Tokens(record.CanonicalText).Count(terms.Contains) })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Record.UpdatedAt)
            .ThenBy(candidate => candidate.Record.Id.Value, StringComparer.Ordinal)
            .Take(limit)
            .Select(candidate => new MemoryRecallItem(candidate.Record.Type.ToString(), candidate.Record.CanonicalText,
                candidate.Record.Importance, candidate.Record.Confidence, candidate.Record.UpdatedAt))
            .ToArray();
    }

    public async Task<MemoryRememberReceipt> RememberAsync(
        string content,
        string memoryType,
        double importance,
        double confidence,
        IReadOnlyList<string>? entities,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 6_000)
            throw new ArgumentException("Memory content is required and cannot exceed 6000 characters.", nameof(content));
        if (!Enum.TryParse<MemoryRecordType>(memoryType, ignoreCase: true, out var type))
            throw new ArgumentException("Unsupported memory type.", nameof(memoryType));
        if (type == MemoryRecordType.Decision)
            throw new ArgumentException("Decision memory requires a structured trace and is unavailable through the local MCP.", nameof(memoryType));
        if (!double.IsFinite(importance) || importance is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(importance));
        if (!double.IsFinite(confidence) || confidence is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(confidence));

        var normalizedEntities = (entities ?? [])
            .Where(entity => !string.IsNullOrWhiteSpace(entity))
            .Select(entity => entity.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
        var canonical = content.Trim();
        var idempotencyKey = Hash($"{type}|{canonical}|{string.Join('|', normalizedEntities)}");
        var command = new RememberCommand(
            new(new CommandId(Guid.NewGuid().ToString("D")), idempotencyKey, _configuration.Actor,
                new CorrelationId(Guid.NewGuid().ToString("D")), _configuration.Scope, ContractVersion),
            new(type, canonical, "Codex local MCP", importance, confidence, normalizedEntities,
                new MemoryProvenance("codex-mcp", null, null, null, null, null, null,
                    [new SourceEvidenceRef($"codex-mcp:{idempotencyKey}")]),
                EmbeddingMode: EmbeddingMode.Required));
        var result = await _commands.RememberAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null || result.Outcome == RememberOutcome.Failed)
            throw new InvalidOperationException("AgMemory could not persist the memory.");
        return new MemoryRememberReceipt(result.Outcome.ToString());
    }

    public async Task<MemoryMcpStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var scopes = new AuthorizedScopeSet([new ScopeSelector(_configuration.Scope)]);
        var records = await _store.ListAsync(scopes, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var activeCount = records.Count(record => record.Status == MemoryLifecycleStatus.Active &&
            (record.ExpiresAt is null || record.ExpiresAt > now));
        return new MemoryMcpStatus("available", activeCount);
    }

    public ValueTask DisposeAsync() => _store.DisposeAsync();

    private static IReadOnlySet<string> Tokens(string text) => text.Split(
            [' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => value.ToUpperInvariant())
        .ToHashSet(StringComparer.Ordinal);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class GuidIds : IIdGenerator { public MemoryId NewMemoryId() => new(Guid.NewGuid().ToString("D")); public CommandId NewCommandId() => new(Guid.NewGuid().ToString("D")); }
    private sealed class IdentityRedactor : IIngressRedactor { public Task<RedactionResult> RedactAsync(RedactionInput input, CancellationToken cancellationToken) => Task.FromResult(RedactionResult.Accepted(input, "codex-mcp-local-v1")); }
    private sealed class LocalEmbeddingPolicy : IEmbeddingPolicy
    {
        private static readonly EmbeddingContract Contract = new("codex-mcp", "lexical-fallback", "v1", 1, "none", ContractVersion);
        public Task<EmbeddingPolicyResult> GetAsync(MemoryScope scope, CancellationToken cancellationToken) => Task.FromResult(new EmbeddingPolicyResult(Contract, "codex-mcp-local-v1"));
    }
    private sealed class LocalEmbeddingProvider : IEmbeddingProvider
    {
        public Task<EmbeddingResult> CreateAsync(string content, EmbeddingContract contract, CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingResult(new EmbeddingReference(contract.Provider, contract.Model, contract.ModelVersion,
                contract.Dimension, contract.Normalization, Hash(content)), new float[] { 1f }));
    }
    private sealed class NoRetentionPolicy : IRetentionPolicy { public Task<RetentionPolicyResult> EvaluateForgetAsync(MemoryRecord record, ActorId actor, CancellationToken cancellationToken) => Task.FromResult(new RetentionPolicyResult(false, false, "codex-mcp-local-v1")); }
    private sealed class ExactLocalAuthorization(LocalAgMemoryMcpConfiguration configuration) : IAuthorizationScopeValidator
    {
        public Task<ScopeAuthorizationResult> AuthorizeAsync(ActorId actor, MemoryOperation operation, MemoryScope requestedScope, CancellationToken cancellationToken) => Task.FromResult(
            actor == configuration.Actor && requestedScope == configuration.Scope && operation is MemoryOperation.Remember or MemoryOperation.Search
                ? ScopeAuthorizationResult.Allowed(new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]), "codex-mcp-local-v1")
                : ScopeAuthorizationResult.Denied(MemoryErrorCode.Unauthorized, "codex-mcp-local-v1"));
    }
}

public sealed record MemoryRecallItem(string Type, string Content, double Importance, double Confidence, DateTimeOffset UpdatedAt);
public sealed record MemoryRememberReceipt(string Outcome);
public sealed record MemoryMcpStatus(string Status, int ActiveMemoryCount);
