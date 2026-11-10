using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using AgMemory.ActivityJournal;
using System.Text;
using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;

namespace AgMemory.McpServer;

/// <summary>Fixed local identity and exact scope supplied only by the MCP process environment.</summary>
public sealed record LocalAgMemoryMcpConfiguration(string StoragePath, ActorId Actor, MemoryScope Scope, string OriginClient)
{
    /// <summary>Optional operator-configured local team; null preserves the existing actor partition.</summary>
    public string? KnowledgeTeamId { get; init; }
    /// <summary>Opt-in BM25 ranking; false preserves the installed lexical-overlap behavior.</summary>
    public bool UseBm25Recall { get; init; }
    /// <summary>Bounded packaged RU/EN aliases; operators can disable expansion independently of lexical/BM25 ranking.</summary>
    public bool UseQueryAliases { get; init; } = true;
    public const string QueryAliasesVariable = "AGMEMORY_QUERY_ALIASES";
    public const string RecallRankingVariable = "AGMEMORY_RECALL_RANKING";
    public bool UseRecallIndex { get; init; } = true;
    public const string RecallIndexVariable = "AGMEMORY_RECALL_INDEX";
    /// <summary>Explicit operator migration is required before using journal state; legacy remains the default.</summary>
    public bool UseKnowledgeJournal { get; init; }
    public const string KnowledgeStorageVariable = "AGMEMORY_KNOWLEDGE_STORAGE";

    public const string KnowledgeTeamIdVariable = "AGMEMORY_KNOWLEDGE_TEAM_ID";
    /// <summary>Explicit journal root for direct composition; stdio startup supplies the app-data directory by default.</summary>
    public string? ActivityDataDirectory { get; init; }
    public LocalMcpUsageAuditConfiguration UsageAudit { get; init; } = LocalMcpUsageAuditConfiguration.Disabled;

    public const string StoragePathVariable = "AGMEMORY_STORAGE_PATH";
    public const string ActorIdVariable = "AGMEMORY_ACTOR_ID";
    public const string TenantIdVariable = "AGMEMORY_TENANT_ID";
    public const string ProjectIdVariable = "AGMEMORY_PROJECT_ID";
    public const string WorkspaceIdVariable = "AGMEMORY_WORKSPACE_ID";
    public const string ChatIdVariable = "AGMEMORY_CHAT_ID";
    public const string RunIdVariable = "AGMEMORY_RUN_ID";
    /// <summary>Fixed client label recorded in provenance; it is never accepted from an MCP tool call.</summary>
    public const string ClientIdVariable = "AGMEMORY_CLIENT_ID";

    public static LocalAgMemoryMcpConfiguration FromEnvironment() => Create(
        Environment.GetEnvironmentVariable(StoragePathVariable),
        Environment.GetEnvironmentVariable(ActorIdVariable),
        Environment.GetEnvironmentVariable(TenantIdVariable),
        Environment.GetEnvironmentVariable(ProjectIdVariable),
        Environment.GetEnvironmentVariable(WorkspaceIdVariable),
        Environment.GetEnvironmentVariable(ChatIdVariable),
        Environment.GetEnvironmentVariable(RunIdVariable),
        Environment.GetEnvironmentVariable(ClientIdVariable),
        Environment.GetEnvironmentVariable(KnowledgeTeamIdVariable)) with
        {
            UsageAudit = LocalMcpUsageAuditConfiguration.FromEnvironment(),
            ActivityDataDirectory = ActivityIdentity.DataDirectory(),
            UseBm25Recall = ParseRecallRanking(Environment.GetEnvironmentVariable(RecallRankingVariable)),
            UseRecallIndex = ParseQueryAliases(Environment.GetEnvironmentVariable(RecallIndexVariable)),
            UseQueryAliases = ParseQueryAliases(Environment.GetEnvironmentVariable(QueryAliasesVariable)),
            UseKnowledgeJournal = ParseKnowledgeStorage(Environment.GetEnvironmentVariable(KnowledgeStorageVariable))
        };

    /// <summary>Never silently migrates existing data or accepts a misspelled storage mode.</summary>
    public static bool ParseKnowledgeStorage(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "legacy" => false,
        "journal" => true,
        _ => throw new ArgumentException("Knowledge storage must be legacy or journal.", nameof(value))
    };

    /// <summary>Defaults to the packaged bounded dictionary; off/none disables it without changing the underlying ranker.</summary>
    public static bool ParseQueryAliases(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "on" or "v1" => true,
        "off" or "none" => false,
        _ => throw new ArgumentException("Query aliases must be on, v1, off or none.", nameof(value))
    };

    /// <summary>Rejects unknown ranking settings rather than silently claiming semantic retrieval.</summary>
    public static bool ParseRecallRanking(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "lexical" => false,
        "bm25" => true,
        _ => throw new ArgumentException("Recall ranking must be lexical or bm25.", nameof(value))
    };

    public static LocalAgMemoryMcpConfiguration Create(
        string? storagePath,
        string? actorId,
        string? tenantId,
        string? projectId = null,
        string? workspaceId = null,
        string? chatId = null,
        string? runId = null,
        string? originClient = null,
        string? knowledgeTeamId = null)
    {
        if (string.IsNullOrWhiteSpace(storagePath) || string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("AgMemory MCP requires an explicit storage path, actor ID, and tenant ID.");

        var scope = new MemoryScope(new ScopeId(tenantId), Optional(projectId), Optional(workspaceId), Optional(chatId), Optional(runId));
        scope.Validate();
        var actor = new ActorId(actorId);
        actor.Validate(nameof(actorId));
        var client = string.IsNullOrWhiteSpace(originClient) ? "codex" : originClient.Trim();
        if (!client.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new ArgumentException("Origin client must contain only letters, digits, hyphens, or underscores.", nameof(originClient));
        KnowledgeProfileIdentity.ValidateTeamId(knowledgeTeamId);
        return new(Path.GetFullPath(storagePath), actor, scope, client) { KnowledgeTeamId = knowledgeTeamId };
    }

    private static ScopeId? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : new ScopeId(value);
}

/// <summary>
/// Small local composition root for a configured client. It never accepts actor, scope, storage path or durable IDs from a tool call.
/// </summary>
public sealed partial class LocalAgMemoryMcpRuntime : IAsyncDisposable
{
    private static readonly ContractVersion ContractVersion = new("agmemory-mcp-v1");
    private readonly LocalAgMemoryMcpConfiguration _configuration;
    private readonly LanceDbMemoryStore _store;
    private readonly MemoryCommandService _commands;
    private readonly LocalMcpUsageAuditObserver _usageAudit;
    private readonly Lazy<LocalAgMemoryMcpRuntime> _sharedProcedureLibrary;
    private readonly SegmentJournal? _activityStore;
    private readonly ActivityWriter? _activityWriter;
    private readonly ActivityObserver? _activity;

    public LocalAgMemoryMcpRuntime(LocalAgMemoryMcpConfiguration configuration, IUsageAuditStore? auditStore = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _sharedProcedureLibrary = new(() => new LocalAgMemoryMcpRuntime(SharedProcedureConfiguration(configuration)));
        // Activity is independent of the legacy opt-in audit. Failure to initialize never disables memory work.
        try
        {
            // A directly composed runtime without an explicit journal root preserves its no-I/O-before-validation contract.
            if (configuration.ActivityDataDirectory is { } dataDirectory)
            {
                var options = ActivityJournalOptions.Load(dataDirectory);
                _activityStore = new(Path.Combine(dataDirectory, "agent-activity"), options);
                _activityWriter = new(_activityStore, options);
                _activity = new(_activityWriter, ActivityIdentity.Partition(configuration.Actor, configuration.Scope, configuration.StoragePath), configuration.OriginClient, options.CaptureQueryPreview);
            }
        }
        catch { /* A read-only settings/data directory must not prevent MCP startup. */ }
        KnowledgeProfileIdentity.ValidateTeamId(configuration.KnowledgeTeamId);
        _agentReferences = new(() => new AgentReferenceProtector(configuration));
        _store = new LanceDbMemoryStore(new(configuration.StoragePath));
        // A timed-out native audit append may keep its own gate. It must never hold the primary memory-operation gate.
        var ownedAuditStore = auditStore is null && configuration.UsageAudit.Factory is not null
            ? new LanceDbMemoryStore(new(configuration.StoragePath)) : null;
        _usageAudit = new(configuration.UsageAudit, auditStore ?? ownedAuditStore ?? _store, configuration.OriginClient, ownedAuditStore);
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

    /// <summary>Recalls memory and observes one invocation, including validation and storage failures.</summary>
    public Task<IReadOnlyList<MemoryRecallItem>> RecallAsync(string query, int limit, CancellationToken cancellationToken) =>
        ObserveAsync(UsageAuditOperation.Recall, query,
            () => RecallCoreAsync(query, limit, cancellationToken), hits => new(hits.Count, hits.Sum(hit => hit.Content.Length)), requestSummary: $"Поиск памяти; максимум {limit} записей");

    /// <summary>Uses the bounded lexical projection in one exact configured scope; storage failures expose only a safe message.</summary>
    private async Task<IReadOnlyList<MemoryRecallItem>> RecallCoreAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A recall query is required.", nameof(query));
        if (limit is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(limit), "Recall limit must be from 1 through 10.");

        var eligibility = new MemorySearchEligibility(new AuthorizedScopeSet([new ScopeSelector(_configuration.Scope)]), null, DateTimeOffset.UtcNow);
        MemoryLexicalRecallPage result;
        try
        {
            result = await SearchAgentCandidatesAsync(eligibility, query.Trim(), limit, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidOperationException("AgMemory could not recall memories."); }

        var hits = new List<MemoryRecallItem>(result.Items.Count);
        foreach (var item in result.Items)
        {
            _activity?.Target(item.Id.Value);
            hits.Add(new(item.Type.ToString(), item.Content, item.Importance, item.Confidence, item.UpdatedAt));
        }
        return hits;
    }

    /// <summary>Persists validated memory and audits only safe counts, never a descriptor of the memory content.</summary>
    public Task<MemoryRememberReceipt> RememberAsync(string content, string memoryType, double importance, double confidence,
        IReadOnlyList<string>? entities, CancellationToken cancellationToken) =>
        ObserveAsync(UsageAuditOperation.Remember, "",
            () => RememberCoreAsync(content, memoryType, importance, confidence, entities, cancellationToken), _ => new(1));

    /// <summary>Validates a symbolic record type and persists or reinforces normalized content; identical input replays safely.</summary>
    private async Task<MemoryRememberReceipt> RememberCoreAsync(
        string content,
        string memoryType,
        double importance,
        double confidence,
        IReadOnlyList<string>? entities,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 6_000)
            throw new ArgumentException("Memory content is required and cannot exceed 6000 characters.", nameof(content));
        if (!Enum.TryParse<MemoryRecordType>(memoryType, ignoreCase: true, out var type) ||
            !Enum.IsDefined(type) || !string.Equals(memoryType.Trim(), type.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Unsupported memory type.", nameof(memoryType));
        if (type == MemoryRecordType.Decision)
            throw new ArgumentException("Decision memory requires a structured trace and is unavailable through the local MCP.", nameof(memoryType));
        if (!double.IsFinite(importance) || importance is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(importance));
        if (!double.IsFinite(confidence) || confidence is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(confidence));

        var normalizedEntities = NormalizeEntities(entities);
        var canonical = NormalizeWhitespace(content);
        // Hash a structured payload so delimiters in content/entities cannot alias another request.
        // Changed confidence, importance or client provenance must reach Core reinforcement rather than replay.
        var payload = JsonSerializer.Serialize(new
        {
            Type = type.ToString(),
            Content = canonical,
            Importance = importance,
            Confidence = confidence,
            Entities = normalizedEntities.Select(entity => entity.ToUpperInvariant()).ToArray(),
            Client = _configuration.OriginClient
        });
        var idempotencyKey = Hash(payload);
        var command = new RememberCommand(
            new(new CommandId(Guid.NewGuid().ToString("D")), idempotencyKey, _configuration.Actor,
                new CorrelationId(Guid.NewGuid().ToString("D")), _configuration.Scope, ContractVersion),
            new(type, canonical, $"{_configuration.OriginClient} local MCP", importance, confidence, normalizedEntities,
                new MemoryProvenance($"{_configuration.OriginClient}-mcp", null, null, null, null, null, null,
                    [new SourceEvidenceRef($"{_configuration.OriginClient}-mcp:{idempotencyKey}")]),
                EmbeddingMode: EmbeddingMode.Required));
        var result = await _commands.RememberAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null || result.Outcome == RememberOutcome.Failed)
            throw new InvalidOperationException("AgMemory could not persist the memory.");
        if (result.Memory is not null) _activity?.Target(result.Memory.Id.Value);
        return new MemoryRememberReceipt(result.Outcome.ToString());
    }

    /// <summary>Counts active, unexpired records in the configured exact scope without loading memory content.</summary>
    public Task<MemoryMcpStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        ObserveAsync(UsageAuditOperation.Status, "", () => GetStatusCoreAsync(cancellationToken),
            status => status.Status == "available" ? new(1) : new(0, Outcome: UsageAuditOutcome.Degraded, FailureClass: UsageAuditFailureClass.Storage));

    /// <summary>Reads only the eligible row count; the optional observer never loads record content.</summary>
    private async Task<MemoryMcpStatus> GetStatusCoreAsync(CancellationToken cancellationToken)
    {
        var scopes = new AuthorizedScopeSet([new ScopeSelector(_configuration.Scope)]);
        var eligibility = new MemorySearchEligibility(scopes, null, DateTimeOffset.UtcNow);
        var activeCount = await _store.CountEligibleAsync(eligibility, cancellationToken).ConfigureAwait(false);
        return new MemoryMcpStatus("available", checked((int)activeCount));
    }

    /// <summary>Closes primary memory storage independently of optional audit work still completing in its separate connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_sharedProcedureLibrary.IsValueCreated) await _sharedProcedureLibrary.Value.DisposeAsync().ConfigureAwait(false);
        await _store.DisposeAsync().ConfigureAwait(false);
        await _usageAudit.DisposeAsync().ConfigureAwait(false);
        if (_activityWriter is not null) await _activityWriter.DisposeAsync().ConfigureAwait(false);
        if (_activityWriter?.Completion.IsCompleted != false) _activityStore?.Dispose();
        else _ = DisposeActivityStoreWhenDrainedAsync(_activityWriter.Completion, _activityStore);
    }

    /// <summary>Keeps the optional store alive for a late filesystem drain without extending the two-second shutdown budget.</summary>
    private static async Task DisposeActivityStoreWhenDrainedAsync(Task completion, SegmentJournal? store)
    {
        try { await completion.ConfigureAwait(false); } catch { /* Optional writer errors remain isolated. */ }
        try { store?.Dispose(); } catch { /* Cleanup cannot change primary memory shutdown. */ }
    }

    /// <summary>Observes the existing audit-wrapped invocation once, retaining only safe outcome/count metadata.</summary>
    private Task<T> ObserveAsync<T>(UsageAuditOperation operation, string? query, Func<Task<T>> invoke,
        Func<T, McpUsageAuditResult> summarize, [CallerMemberName] string operationName = "unknown", string? requestSummary = null)
    {
        Task<T> Execute() => _usageAudit.ExecuteAsync(operation, query, invoke, summarize);
        if (_activity is null) return Execute();
        return _activity.ExecuteAsync(operationName, Execute, result =>
        {
            var summary = summarize(result);
            return new(summary.Outcome == UsageAuditOutcome.Succeeded ? "succeeded" : "failed", result is MemoryMcpStatus status ? status.ActiveMemoryCount : summary.Count,
                summary.FailureClass?.ToString().ToLowerInvariant());
        }, requestSummary, query);
    }

    /// <summary>Preserves the first sixteen distinct labels while making equivalent label order deterministic.</summary>
    private static string[] NormalizeEntities(IReadOnlyList<string>? entities)
    {
        var labels = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities ?? [])
        {
            if (string.IsNullOrWhiteSpace(entity)) continue;
            var normalized = NormalizeWhitespace(entity);
            if (!seen.Add(normalized)) continue;
            labels.Add(normalized);
            if (labels.Count == 16) break;
        }
        labels.Sort(StringComparer.OrdinalIgnoreCase);
        return labels.ToArray();
    }

    /// <summary>Uses the same whitespace normalization as the command service before computing replay identity.</summary>
    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
            actor == configuration.Actor && requestedScope == configuration.Scope && operation is MemoryOperation.Remember or MemoryOperation.Search or MemoryOperation.BuildContext or MemoryOperation.Lifecycle
                ? ScopeAuthorizationResult.Allowed(new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]), "local-mcp-v1")
                : ScopeAuthorizationResult.Denied(MemoryErrorCode.Unauthorized, "local-mcp-v1"));
    }
}

public sealed record MemoryRecallItem(string Type, string Content, double Importance, double Confidence, DateTimeOffset UpdatedAt);
public sealed record MemoryRememberReceipt(string Outcome);
public sealed record MemoryMcpStatus(string Status, int ActiveMemoryCount);
