using AgMemory.Contracts;

namespace AgMemory.Core.Tests;

internal sealed class TestClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow.ToUniversalTime();
}

internal sealed class SequentialIds : IIdGenerator
{
    private int _memory;
    private int _command;
    public MemoryId NewMemoryId() => new($"memory-{++_memory:D4}");
    public CommandId NewCommandId() => new($"command-{++_command:D4}");
}

internal sealed class FixedAuthorization : IAuthorizationScopeValidator
{
    private readonly Dictionary<(ActorId Actor, MemoryOperation Operation), ScopeAuthorizationResult> _rules = [];
    public int Calls { get; private set; }
    public List<AuthorizedScopeSet> IssuedScopes { get; } = [];

    public void Allow(ActorId actor, MemoryOperation operation, params MemoryScope[] scopes) =>
        _rules[(actor, operation)] = ScopeAuthorizationResult.Allowed(
            new AuthorizedScopeSet(scopes.Select(scope => new ScopeSelector(scope))), "test-auth-v1");

    public void Deny(ActorId actor, MemoryOperation operation) =>
        _rules[(actor, operation)] = ScopeAuthorizationResult.Denied(MemoryErrorCode.Unauthorized, "test-auth-v1");

    public Task<ScopeAuthorizationResult> AuthorizeAsync(
        ActorId actor, MemoryOperation operation, MemoryScope requestedScope, CancellationToken cancellationToken)
    {
        Calls++;
        var result = _rules.TryGetValue((actor, operation), out var configured)
            ? configured
            : ScopeAuthorizationResult.Denied(MemoryErrorCode.Unauthorized, "test-auth-v1");
        if (result.AuthorizedScopes is not null) IssuedScopes.Add(result.AuthorizedScopes);
        return Task.FromResult(result);
    }
}

internal sealed class TestRedactor : IIngressRedactor
{
    public const string Secret = "TOP_SECRET";
    public bool Reject { get; set; }
    public int Calls { get; private set; }

    public Task<RedactionResult> RedactAsync(RedactionInput input, CancellationToken cancellationToken)
    {
        Calls++;
        if (Reject) return Task.FromResult(RedactionResult.Rejected(MemoryErrorCode.RedactionRejected, "test-redaction-v1"));
        string Redact(string value) => value.Replace(Secret, "[redacted]", StringComparison.Ordinal);
        var details = input.DecisionDetails is null ? null : input.DecisionDetails with
        {
            Problem = Redact(input.DecisionDetails.Problem),
            Context = Redact(input.DecisionDetails.Context),
            Options = input.DecisionDetails.Options.Select(Redact).ToArray(),
            Decision = Redact(input.DecisionDetails.Decision),
            Reason = Redact(input.DecisionDetails.Reason),
            Consequences = input.DecisionDetails.Consequences.Select(Redact).ToArray(),
            Outcome = input.DecisionDetails.Outcome is null ? null : Redact(input.DecisionDetails.Outcome)
        };
        return Task.FromResult(RedactionResult.Accepted(new(
            Redact(input.CanonicalText), input.Reason is null ? null : Redact(input.Reason), details), "test-redaction-v1", 1));
    }
}

internal sealed class TestEmbeddingPolicy : IEmbeddingPolicy
{
    public EmbeddingPolicyResult Result { get; set; } = new(null, "embedding-unconfigured-v1");
    public Task<EmbeddingPolicyResult> GetAsync(MemoryScope scope, CancellationToken cancellationToken) => Task.FromResult(Result);
}

internal sealed class TestEmbeddingProvider : IEmbeddingProvider
{
    public int Calls { get; private set; }
    public bool ReturnMismatch { get; set; }
    public Task<EmbeddingResult> CreateAsync(string redactedContent, EmbeddingContract contract, CancellationToken cancellationToken)
    {
        Calls++;
        var reference = new EmbeddingReference(contract.Provider, ReturnMismatch ? "other-model" : contract.Model,
            contract.ModelVersion, contract.Dimension, contract.Normalization, "test-content-hash");
        return Task.FromResult(new EmbeddingResult(reference, new float[contract.Dimension]));
    }
}

internal sealed class TestRetentionPolicy : IRetentionPolicy
{
    public RetentionPolicyResult Result { get; set; } = new(false, false, "retention-unconfigured-v1");
    public Task<RetentionPolicyResult> EvaluateForgetAsync(MemoryRecord record, ActorId actor, CancellationToken cancellationToken) =>
        Task.FromResult(Result);
}

internal sealed class InMemoryStore : IMemoryStore
{
    private Dictionary<MemoryId, MemoryRecord> _records = [];
    private Dictionary<string, SessionHotMemory> _hot = [];
    private Dictionary<string, IdempotencyReceipt> _receipts = [];
    private List<OutboxMessage> _outbox = [];

    public IReadOnlyCollection<MemoryRecord> Records => _records.Values;
    public IReadOnlyCollection<SessionHotMemory> HotMemory => _hot.Values;
    public IReadOnlyCollection<OutboxMessage> Outbox => _outbox;
    public int BeginCalls { get; private set; }

    public void Seed(MemoryRecord record) => _records[record.Id] = record;
    public void Seed(SessionHotMemory memory) => _hot[Key(memory.Scope)] = memory;

    public Task<IMemoryStoreTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        BeginCalls++;
        return Task.FromResult<IMemoryStoreTransaction>(new Transaction(this,
            new(_records), new(_hot), new(_receipts), new(_outbox)));
    }

    public Task<MemoryRecord?> GetAsync(AuthorizedScopeSet scopes, MemoryId id, CancellationToken cancellationToken) =>
        Task.FromResult(_records.TryGetValue(id, out var record) && scopes.Contains(record.Scope) ? record : null);

    public Task<IReadOnlyList<MemoryRecord>> ListAsync(AuthorizedScopeSet scopes, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MemoryRecord>>(_records.Values.Where(record => scopes.Contains(record.Scope))
            .OrderBy(record => record.Id.Value, StringComparer.Ordinal).ToArray());

    public Task<SessionHotMemory?> GetHotMemoryAsync(AuthorizedScopeSet scopes, MemoryScope exactScope, CancellationToken cancellationToken) =>
        Task.FromResult(_hot.TryGetValue(Key(exactScope), out var memory) && scopes.Contains(memory.Scope) ? memory : null);

    private static string Key(MemoryScope scope) => string.Join("|", new[]
    {
        scope.TenantId.Value, scope.ProjectId?.Value ?? "<null>", scope.WorkspaceId?.Value ?? "<null>",
        scope.ChatId?.Value ?? "<null>", scope.RunId?.Value ?? "<null>"
    });

    private sealed class Transaction(
        InMemoryStore owner,
        Dictionary<MemoryId, MemoryRecord> records,
        Dictionary<string, SessionHotMemory> hot,
        Dictionary<string, IdempotencyReceipt> receipts,
        List<OutboxMessage> outbox) : IMemoryStoreTransaction
    {
        public Task<IdempotencyReceipt?> FindReceiptAsync(
            AuthorizedScopeSet authorizedScopes,
            string kind,
            ScopeSelector scope,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(authorizedScopes.Contains(scope.Scope)
                ? receipts.GetValueOrDefault(ReceiptKey(kind, scope, idempotencyKey))
                : null);

        public Task<MemoryRecord?> FindByDeduplicationKeyAsync(
            AuthorizedScopeSet authorizedScopes,
            ScopeSelector scope,
            string deduplicationKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(authorizedScopes.Contains(scope.Scope)
                ? records.Values.SingleOrDefault(record => new ScopeSelector(record.Scope) == scope &&
                    string.Equals(record.DeduplicationKey, deduplicationKey, StringComparison.Ordinal))
                : null);

        public Task<MemoryRecord?> FindRecordAsync(AuthorizedScopeSet scopes, MemoryId id, CancellationToken cancellationToken) =>
            Task.FromResult(records.TryGetValue(id, out var record) && scopes.Contains(record.Scope) ? record : null);

        public Task<SessionHotMemory?> FindHotMemoryAsync(AuthorizedScopeSet scopes, MemoryScope exactScope, CancellationToken cancellationToken) =>
            Task.FromResult(hot.TryGetValue(Key(exactScope), out var memory) && scopes.Contains(memory.Scope) ? memory : null);

        public Task<ConditionalWriteResult> WriteRecordAsync(
            AuthorizedScopeSet authorizedScopes,
            MemoryRecord record,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            if (!authorizedScopes.Contains(record.Scope)) return Task.FromResult(new ConditionalWriteResult(false, null));
            if (records.TryGetValue(record.Id, out var existing))
            {
                if (!authorizedScopes.Contains(existing.Scope)) return Task.FromResult(new ConditionalWriteResult(false, null));
                if (expectedVersion == 0 || existing.Version != expectedVersion) return Task.FromResult(new ConditionalWriteResult(false, existing.Version));
            }
            else if (expectedVersion != 0) return Task.FromResult(new ConditionalWriteResult(false, null));
            records[record.Id] = record;
            return Task.FromResult(new ConditionalWriteResult(true, record.Version));
        }

        public async Task<IReadOnlyList<ConditionalWriteResult>> WriteRecordsAsync(
            AuthorizedScopeSet authorizedScopes,
            IReadOnlyList<ConditionalRecordWrite> writes,
            CancellationToken cancellationToken)
        {
            var results = new List<ConditionalWriteResult>(writes.Count);
            foreach (var write in writes)
                results.Add(await WriteRecordAsync(authorizedScopes, write.Record, write.ExpectedVersion, cancellationToken));
            return results;
        }

        public Task<ConditionalWriteResult> DeleteRecordAsync(
            AuthorizedScopeSet authorizedScopes,
            MemoryRecord record,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            if (!records.TryGetValue(record.Id, out var existing)) return Task.FromResult(new ConditionalWriteResult(false, null));
            if (!authorizedScopes.Contains(record.Scope) || !authorizedScopes.Contains(existing.Scope))
                return Task.FromResult(new ConditionalWriteResult(false, null));
            if (existing.Version != expectedVersion) return Task.FromResult(new ConditionalWriteResult(false, existing.Version));
            records.Remove(record.Id);
            return Task.FromResult(new ConditionalWriteResult(true, null));
        }

        public Task<ConditionalWriteResult> WriteHotMemoryAsync(
            AuthorizedScopeSet authorizedScopes,
            SessionHotMemory memory,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            if (!authorizedScopes.Contains(memory.Scope)) return Task.FromResult(new ConditionalWriteResult(false, null));
            var key = Key(memory.Scope);
            if (hot.TryGetValue(key, out var existing))
            {
                if (!authorizedScopes.Contains(existing.Scope)) return Task.FromResult(new ConditionalWriteResult(false, null));
                if (expectedVersion == 0 || existing.Version != expectedVersion) return Task.FromResult(new ConditionalWriteResult(false, existing.Version));
            }
            else if (expectedVersion != 0) return Task.FromResult(new ConditionalWriteResult(false, null));
            hot[key] = memory;
            return Task.FromResult(new ConditionalWriteResult(true, memory.Version));
        }

        public Task SaveReceiptAsync(AuthorizedScopeSet authorizedScopes, IdempotencyReceipt receipt, CancellationToken cancellationToken)
        {
            RequireAuthorized(authorizedScopes, receipt.Scope);
            receipts[ReceiptKey(receipt.CommandKind, receipt.Scope, receipt.IdempotencyKey)] = receipt;
            return Task.CompletedTask;
        }

        public Task EnqueueAsync(AuthorizedScopeSet authorizedScopes, OutboxMessage message, CancellationToken cancellationToken)
        {
            RequireAuthorized(authorizedScopes, message.Scope);
            outbox.Add(message);
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            owner._records = records;
            owner._hot = hot;
            owner._receipts = receipts;
            owner._outbox = outbox;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static string ReceiptKey(string kind, ScopeSelector scope, string key) => $"{kind}|{Key(scope.Scope)}|{key}";
        private static void RequireAuthorized(AuthorizedScopeSet authorizedScopes, ScopeSelector scope)
        {
            if (!authorizedScopes.Contains(scope.Scope))
                throw new UnauthorizedAccessException("A storage transaction cannot write outside its authorized exact selectors.");
        }
    }
}

internal sealed class TestLexicalSearch : ILexicalSearch
{
    public IReadOnlyList<SearchPortCandidate> Candidates { get; set; } = [];
    public int Calls { get; private set; }
    public SearchPortRequest? LastRequest { get; private set; }
    public Task<IReadOnlyList<SearchPortCandidate>> SearchAsync(SearchPortRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request;
        return Task.FromResult(Candidates);
    }
}

internal sealed class TestVectorSearch : IVectorSearch
{
    public IReadOnlyList<SearchPortCandidate> Candidates { get; set; } = [];
    public int Calls { get; private set; }
    public SearchPortRequest? LastRequest { get; private set; }
    public Task<IReadOnlyList<SearchPortCandidate>> SearchAsync(SearchPortRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request;
        return Task.FromResult(Candidates);
    }
}

internal sealed class TestGraph : IMemoryGraph
{
    public IReadOnlyList<GraphRerankResult> Results { get; set; } = [];
    public int Calls { get; private set; }
    public Task<IReadOnlyList<GraphRerankResult>> RerankAsync(GraphRerankRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Results);
    }
}

internal static class TestData
{
    public static readonly ContractVersion ContractVersion = new("v1");
    public static readonly ContractVersion RetrievalVersion = new("rrf-v1");
    public static readonly ContractVersion ContextVersion = new("context-v1");
    public static readonly ActorId Actor = new("test-actor");
    public static readonly MemoryScope Scope = new(new("tenant-a"), new("project-a"), new("workspace-a"), new("chat-a"), new("run-a"));
    public static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    public static CommandEnvelope Envelope(string key = "key-1", MemoryScope? scope = null) => new(
        new($"command-{key}"), key, Actor, new($"correlation-{key}"), scope ?? Scope, ContractVersion);

    public static MemoryProvenance Provenance(string evidence = "evidence-1") => new(
        "test", "legacy-1", Scope.WorkspaceId, Scope.ChatId, Scope.RunId, "message-1", "execution-1", [new(evidence)]);

    public static MemoryRecord Record(
        string id, MemoryScope? scope = null, string text = "bounded retry policy", MemoryRecordType type = MemoryRecordType.Fact,
        MemoryLifecycleStatus status = MemoryLifecycleStatus.Active, long version = 1, DateTimeOffset? expiresAt = null, int cost = 5) => new(
        new(id), scope ?? Scope, type, status, text, null, .7, .8, cost, Now, Now, version,
        ["memory"], Provenance($"evidence-{id}"), null, expiresAt, $"dedup-{id}");

    public static MemoryCoreOptions Options => new(ContractVersion, new("reranker-v1"));
}
