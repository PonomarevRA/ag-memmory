using System.Security.Cryptography;
using System.Text;
using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;
using AgMemory.Web.Features.MemoryGraph;

namespace AgMemory.Web.Features.Chat;

public sealed class LocalChatMemoryFeature : IAsyncDisposable
{
    private static readonly ContractVersion ContractVersion = new("local-chat-memory-v1");
    private readonly MemoryGraphHostConfiguration? _configuration;
    private readonly object _sync = new();
    private LanceDbMemoryStore? _store;
    private MemoryCommandService? _commands;
    private MemoryQueryService? _query;

    public LocalChatMemoryFeature(MemoryGraphHostOptions options, string dataDirectory) =>
        _configuration = options.TryCreate(dataDirectory);

    public bool IsConfigured => _configuration is not null;

    public async Task<string?> RecallAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_configuration is null || string.IsNullOrWhiteSpace(prompt)) return null;
        var runtime = GetOrCreateRuntime(_configuration);
        var scope = new AuthorizedScopeSet([new ScopeSelector(_configuration.Scope)]);
        var queryTerms = Tokens(prompt);
        var now = DateTimeOffset.UtcNow;
        var records = await runtime.Store.ListAsync(scope, cancellationToken).ConfigureAwait(false);
        var hits = records.Where(record => record.Status == MemoryLifecycleStatus.Active &&
                (record.ExpiresAt is null || record.ExpiresAt > now))
            .Select(record => new { Record = record, Score = Tokens(record.CanonicalText).Count(queryTerms.Contains) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Record.UpdatedAt)
            .Take(4)
            .Select(item => item.Record)
            .ToArray();
        if (hits.Length == 0) return null;
        var builder = new StringBuilder();
        foreach (var hit in hits)
        {
            var remaining = 3_000 - builder.Length;
            if (remaining <= 3) break;
            var text = hit.CanonicalText;
            builder.Append("- ").Append(text[..Math.Min(text.Length, remaining - 3)]).AppendLine();
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    public async Task RememberAsync(string threadId, string role, string content, CancellationToken cancellationToken)
    {
        if (_configuration is null || string.IsNullOrWhiteSpace(content)) return;
        var canonical = $"{(role == "assistant" ? "assistant" : "user")}: {content.Trim()}";
        var idempotencyKey = Hash($"{threadId}|{canonical}");
        var command = new RememberCommand(new(new CommandId(Guid.NewGuid().ToString("D")), idempotencyKey,
            _configuration.Actor, new CorrelationId(Guid.NewGuid().ToString("D")), _configuration.Scope, ContractVersion), new(
            MemoryRecordType.Event, canonical, "Local Yuki chat", .6d, .8d, [$"thread:{threadId}"],
            new MemoryProvenance("local-yuki-chat", null, null, null, null, threadId, null,
                [new SourceEvidenceRef($"chat:{threadId}:{idempotencyKey}")]), EmbeddingMode: EmbeddingMode.Required));
        var result = await GetOrCreateRuntime(_configuration).Commands.RememberAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null || result.Outcome == RememberOutcome.Failed)
            throw new InvalidOperationException("Local chat memory could not persist the message.");
    }

    public async ValueTask DisposeAsync()
    {
        LanceDbMemoryStore? store;
        lock (_sync) { store = _store; _store = null; _commands = null; _query = null; }
        if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
    }

    private (LanceDbMemoryStore Store, MemoryCommandService Commands, MemoryQueryService Query) GetOrCreateRuntime(MemoryGraphHostConfiguration configuration)
    {
        lock (_sync)
        {
            if (_store is not null && _commands is not null && _query is not null) return (_store, _commands, _query);
            _store = new LanceDbMemoryStore(new(configuration.StoragePath));
            var authorization = new ExactLocalAuthorization(configuration);
            var policy = new LocalEmbeddingPolicy();
            var clock = new SystemClock();
            var options = new MemoryCoreOptions(ContractVersion, ContractVersion);
            _commands = new MemoryCommandService(_store, authorization, new IdentityRedactor(), new LocalEmbeddingProvider(), policy,
                new NoRetentionPolicy(), clock, new GuidIds(), options);
            _query = new MemoryQueryService(_store, authorization, _store, _store, null, policy, clock, options);
            return (_store, _commands, _query);
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static IReadOnlySet<string> Tokens(string text) => text.Split([' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => value.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
    private sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class GuidIds : IIdGenerator { public MemoryId NewMemoryId() => new(Guid.NewGuid().ToString("D")); public CommandId NewCommandId() => new(Guid.NewGuid().ToString("D")); }
    private sealed class IdentityRedactor : IIngressRedactor { public Task<RedactionResult> RedactAsync(RedactionInput input, CancellationToken cancellationToken) => Task.FromResult(RedactionResult.Accepted(input, "local-chat-identity-v1")); }
    private sealed class LocalEmbeddingPolicy : IEmbeddingPolicy
    {
        private static readonly EmbeddingContract Contract = new("local-chat", "lexical-fallback", "v1", 1, "none", ContractVersion);
        public Task<EmbeddingPolicyResult> GetAsync(MemoryScope scope, CancellationToken cancellationToken) => Task.FromResult(new EmbeddingPolicyResult(Contract, "local-chat-embedding-v1"));
    }
    private sealed class LocalEmbeddingProvider : IEmbeddingProvider
    {
        public Task<EmbeddingResult> CreateAsync(string content, EmbeddingContract contract, CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingResult(new EmbeddingReference(contract.Provider, contract.Model, contract.ModelVersion, contract.Dimension, contract.Normalization, Hash(content)), new float[] { 1f }));
    }
    private sealed class NoRetentionPolicy : IRetentionPolicy { public Task<RetentionPolicyResult> EvaluateForgetAsync(MemoryRecord record, ActorId actor, CancellationToken cancellationToken) => Task.FromResult(new RetentionPolicyResult(false, false, "local-chat-no-retention-v1")); }
    private sealed class ExactLocalAuthorization(MemoryGraphHostConfiguration configuration) : IAuthorizationScopeValidator
    {
        public Task<ScopeAuthorizationResult> AuthorizeAsync(ActorId actor, MemoryOperation operation, MemoryScope scope, CancellationToken cancellationToken) => Task.FromResult(
            actor == configuration.Actor && scope == configuration.Scope && operation is MemoryOperation.Remember or MemoryOperation.Search
                ? ScopeAuthorizationResult.Allowed(new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]), "local-chat-v1")
                : ScopeAuthorizationResult.Denied(MemoryErrorCode.Unauthorized, "local-chat-v1"));
    }
}
