using System.Security.Cryptography;
using System.Text;
using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;
using AgMemory.Web.Features.MemoryGraph;
using AgMemory.Web.Gateway;

namespace AgMemory.Web.Features.Chat;

public sealed class LocalChatMemoryFeature : IAsyncDisposable
{
    private static readonly ContractVersion ContractVersion = new("local-chat-memory-v1");
    private readonly MemoryGraphHostConfiguration? _configuration;
    private readonly object _sync = new();
    private int _lastInjectHitCount;
    private LanceDbMemoryStore? _store;
    private MemoryCommandService? _commands;
    private MemoryQueryService? _query;

    public LocalChatMemoryFeature(MemoryGraphHostOptions options, string dataDirectory) =>
        _configuration = options.TryCreate(dataDirectory);

    public bool IsConfigured => _configuration is not null;
    public int LastInjectHitCount
    {
        get { lock (_sync) return _lastInjectHitCount; }
    }

    public async Task<string?> RecallAsync(string prompt, CancellationToken cancellationToken)
    {
        var result = await RecallDetailedAsync(prompt, cancellationToken).ConfigureAwait(false);
        return result?.LlmContext;
    }

    public async Task<LocalChatRecallResult?> RecallDetailedAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_configuration is null || string.IsNullOrWhiteSpace(prompt))
        {
            SetLastInjectHitCount(0);
            return null;
        }

        var runtime = GetOrCreateRuntime(_configuration);
        var trimmed = prompt.Trim();
        var request = new MemoryContextRequest(
            _configuration.Actor,
            _configuration.Scope,
            trimmed,
            null,
            null,
            SearchLimit: 8,
            TokenBudget: 800,
            RequireCitations: false,
            RetrievalConfigurationVersion: ContractVersion,
            ContextConfigurationVersion: ContractVersion,
            ContractVersion: ContractVersion);

        var context = await runtime.Query.BuildContextAsync(request, cancellationToken).ConfigureAwait(false);
        if (context.Error is not null)
        {
            SetLastInjectHitCount(0);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(context.Content))
        {
            var records = await LoadRecordsFromContextAsync(runtime.Store, _configuration.Scope, context.Content, cancellationToken)
                .ConfigureAwait(false);
            var components = records.Select(ToComponent).ToArray();
            var notes = StripIds(context.Content);
            SetLastInjectHitCount(components.Length > 0 ? components.Length : notes.Count);
            return new(components.Length > 0 ? components : notes.Select(note => ToFallbackComponent(note[2..])).ToArray(),
                notes.Count == 0 ? null : string.Join("\n", notes));
        }

        var search = await runtime.Query.SearchAsync(new(
            _configuration.Actor,
            _configuration.Scope,
            trimmed,
            null,
            null,
            null,
            8,
            ContractVersion,
            ContractVersion), cancellationToken).ConfigureAwait(false);
        if (search.Error is null && search.Hits.Count > 0)
        {
            var components = search.Hits.Select(hit => ToComponent(hit.Record)).ToArray();
            var notes = components.Select(component => $"- {component.Preview}").ToArray();
            SetLastInjectHitCount(components.Length);
            return new(components, string.Join("\n", notes));
        }

        var handoff = await ReadHandoffAsync(runtime.Store, _configuration.Scope, cancellationToken).ConfigureAwait(false);
        if (handoff is null)
        {
            SetLastInjectHitCount(0);
            return null;
        }

        SetLastInjectHitCount(1);
        return new([handoff.Value.Component], handoff.Value.LlmLine);
    }

    public async Task RememberAsync(string threadId, string role, string content, CancellationToken cancellationToken) =>
        await RememberAsync(threadId, role, content, MemoryRecordType.Event, cancellationToken).ConfigureAwait(false);

    public async Task RememberAsync(string threadId, string role, string content, MemoryRecordType type, CancellationToken cancellationToken)
    {
        if (_configuration is null || string.IsNullOrWhiteSpace(content)) return;
        var canonical = $"{(role == "assistant" ? "assistant" : "user")}: {content.Trim()}";
        var idempotencyKey = Hash($"{threadId}|{type}|{canonical}");
        var command = new RememberCommand(new(new CommandId(Guid.NewGuid().ToString("D")), idempotencyKey,
            _configuration.Actor, new CorrelationId(Guid.NewGuid().ToString("D")), _configuration.Scope, ContractVersion), new(
            type, canonical, "Local Yuki chat", .6d, .8d, [$"thread:{threadId}"],
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

    private void SetLastInjectHitCount(int value)
    {
        lock (_sync) _lastInjectHitCount = value;
    }

    private static async Task<IReadOnlyList<MemoryRecord>> LoadRecordsFromContextAsync(
        LanceDbMemoryStore store,
        MemoryScope scope,
        string content,
        CancellationToken cancellationToken)
    {
        var scopes = new AuthorizedScopeSet([new ScopeSelector(scope)]);
        var records = new List<MemoryRecord>();
        foreach (var id in ParseIds(content))
        {
            var record = await store.GetAsync(scopes, id, cancellationToken).ConfigureAwait(false);
            if (record is not null) records.Add(record);
        }
        return records;
    }

    private static IEnumerable<MemoryId> ParseIds(string content)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length <= 2 || line[0] != '[') continue;
            var end = line.IndexOf("] ", StringComparison.Ordinal);
            if (end <= 1) continue;
            yield return new MemoryId(line[1..end]);
        }
    }

    private static ChatMemoryComponent ToComponent(MemoryRecord record)
    {
        var line = record.CanonicalText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Запись памяти";
        var preview = record.CanonicalText.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return new(record.Type.ToString(), Bound(line, 120), Bound(preview, MemoryRecordBrowserLimits.MaximumPreviewCharacters));
    }

    private static ChatMemoryComponent ToFallbackComponent(string preview) =>
        new("Context", Bound(preview, 120), Bound(preview, MemoryRecordBrowserLimits.MaximumPreviewCharacters));

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : string.Concat(value.AsSpan(0, maximum - 1), "…");

    private static List<string> StripIds(string content) =>
        content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.IndexOf("] ", StringComparison.Ordinal) is var marker && marker >= 0
                ? $"- {line[(marker + 2)..]}"
                : $"- {line}")
            .ToList();

    private static async Task<(ChatMemoryComponent Component, string LlmLine)?> ReadHandoffAsync(
        LanceDbMemoryStore store,
        MemoryScope scope,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var records = (await store.ListAsync(new AuthorizedScopeSet([new ScopeSelector(scope)]), cancellationToken).ConfigureAwait(false))
            .Where(record => record.Status == MemoryLifecycleStatus.Active && (record.ExpiresAt is null || record.ExpiresAt > now))
            .ToArray();
        var handoff = records
            .Where(record => record.Type == MemoryRecordType.Summary)
            .OrderByDescending(record => record.UpdatedAt)
            .ThenBy(record => record.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? records
                .Where(record => record.Type == MemoryRecordType.Event)
                .OrderByDescending(record => record.UpdatedAt)
                .ThenBy(record => record.Id.Value, StringComparer.Ordinal)
                .FirstOrDefault();
        if (handoff is null) return null;
        var component = ToComponent(handoff);
        var text = component.Preview;
        if (text.Length == 0) return null;
        if (text.Length > 3200) text = text[..3200];
        return (component, $"- {text}");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
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
            actor == configuration.Actor && scope == configuration.Scope && operation is MemoryOperation.Remember or MemoryOperation.Search or MemoryOperation.BuildContext
                ? ScopeAuthorizationResult.Allowed(new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]), "local-chat-v1")
                : ScopeAuthorizationResult.Denied(MemoryErrorCode.Unauthorized, "local-chat-v1"));
    }
}
