namespace AgMemory.Contracts;

public interface IMemoryCommandService
{
    Task<RememberResult> RememberAsync(RememberCommand command, CancellationToken cancellationToken);
    Task<LifecycleResult> ApplyLifecycleAsync(LifecycleCommand command, CancellationToken cancellationToken);
    Task<HotMemoryResult> AppendHotMemoryAsync(AppendHotMemoryCommand command, CancellationToken cancellationToken);
    Task<ForgetResult> ForgetAsync(ForgetMemoryCommand command, CancellationToken cancellationToken);
}

public interface IMemoryQueryService
{
    Task<MemorySearchResult> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken);
    Task<MemoryContext> BuildContextAsync(MemoryContextRequest request, CancellationToken cancellationToken);
    Task<SessionHotMemory?> ReadHotMemoryAsync(HotMemoryReadRequest request, CancellationToken cancellationToken);
}

public interface IHotMemoryService
{
    Task<HotMemoryStateResult> UpdateAsync(UpdateHotMemoryStateCommand command, CancellationToken cancellationToken);
    Task<HotMemoryStateSnapshot?> ReadStateAsync(HotMemoryReadRequest request, CancellationToken cancellationToken);
    Task<HotMemoryPromotionResult> PromoteAsync(HotMemoryPromotionCommand command, CancellationToken cancellationToken);
}

public sealed record ScopeAuthorizationResult(
    AuthorizedScopeSet? AuthorizedScopes,
    MemoryError? Error,
    string PolicyVersion)
{
    public bool IsAllowed => AuthorizedScopes is not null && Error is null;
    public static ScopeAuthorizationResult Allowed(AuthorizedScopeSet scopes, string policyVersion) =>
        new(scopes, null, policyVersion);
    public static ScopeAuthorizationResult Denied(MemoryErrorCode code, string policyVersion) =>
        new(null, new(code, null, policyVersion), policyVersion);
}

public interface IAuthorizationScopeValidator
{
    Task<ScopeAuthorizationResult> AuthorizeAsync(
        ActorId actor,
        MemoryOperation operation,
        MemoryScope requestedScope,
        CancellationToken cancellationToken);
}

public sealed record RedactionInput(
    string CanonicalText,
    string? Reason,
    DecisionDetails? DecisionDetails);

public sealed record RedactionResult(
    RedactionInput? Content,
    MemoryError? Error,
    string RuleVersion,
    int TransformationCount)
{
    public bool IsAccepted => Content is not null && Error is null;
    public static RedactionResult Accepted(RedactionInput content, string ruleVersion, int count = 0) =>
        new(content, null, ruleVersion, count);
    public static RedactionResult Rejected(MemoryErrorCode code, string ruleVersion) =>
        new(null, new(code, null, ruleVersion), ruleVersion, 0);
}

public interface IIngressRedactor
{
    Task<RedactionResult> RedactAsync(RedactionInput input, CancellationToken cancellationToken);
}

public interface IClock { DateTimeOffset UtcNow { get; } }
public interface IIdGenerator
{
    MemoryId NewMemoryId();
    CommandId NewCommandId();
}

public sealed record IdempotencyReceipt(
    string CommandKind,
    ScopeSelector Scope,
    string IdempotencyKey,
    string PayloadFingerprint,
    string? MemoryId,
    string Outcome,
    ContractVersion ContractVersion);

public sealed record ConditionalWriteResult(bool Applied, long? CurrentVersion);

/// <summary>
/// A provider-neutral conditional write used by import and adapter batching.
/// Ordering of results must match ordering of the supplied writes; transaction commit policy remains with the caller.
/// </summary>
public sealed record ConditionalRecordWrite(MemoryRecord Record, long ExpectedVersion);

public sealed record OutboxMessage(
    ScopeSelector Scope,
    string MessageId,
    string Kind,
    CommandId CommandId,
    CorrelationId CorrelationId,
    string? MemoryId,
    ContractVersion ContractVersion);

public interface IMemoryStore
{
    Task<IMemoryStoreTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
    Task<MemoryRecord?> GetAsync(AuthorizedScopeSet scopes, MemoryId id, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> ListAsync(AuthorizedScopeSet scopes, CancellationToken cancellationToken);
    Task<SessionHotMemory?> GetHotMemoryAsync(AuthorizedScopeSet scopes, MemoryScope exactScope, CancellationToken cancellationToken);
}

public interface IMemoryStoreTransaction : IAsyncDisposable
{
    Task<IdempotencyReceipt?> FindReceiptAsync(
        AuthorizedScopeSet authorizedScopes,
        string commandKind,
        ScopeSelector scope,
        string idempotencyKey,
        CancellationToken cancellationToken);
    Task<MemoryRecord?> FindByDeduplicationKeyAsync(
        AuthorizedScopeSet authorizedScopes,
        ScopeSelector scope,
        string deduplicationKey,
        CancellationToken cancellationToken);
    Task<MemoryRecord?> FindRecordAsync(AuthorizedScopeSet scopes, MemoryId id, CancellationToken cancellationToken);
    Task<SessionHotMemory?> FindHotMemoryAsync(AuthorizedScopeSet scopes, MemoryScope exactScope, CancellationToken cancellationToken);
    Task<ConditionalWriteResult> WriteRecordAsync(
        AuthorizedScopeSet authorizedScopes,
        MemoryRecord record,
        long expectedVersion,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ConditionalWriteResult>> WriteRecordsAsync(
        AuthorizedScopeSet authorizedScopes,
        IReadOnlyList<ConditionalRecordWrite> writes,
        CancellationToken cancellationToken);
    Task<ConditionalWriteResult> DeleteRecordAsync(
        AuthorizedScopeSet authorizedScopes,
        MemoryRecord record,
        long expectedVersion,
        CancellationToken cancellationToken);
    Task<ConditionalWriteResult> WriteHotMemoryAsync(
        AuthorizedScopeSet authorizedScopes,
        SessionHotMemory memory,
        long expectedVersion,
        CancellationToken cancellationToken);
    Task SaveReceiptAsync(AuthorizedScopeSet authorizedScopes, IdempotencyReceipt receipt, CancellationToken cancellationToken);
    Task EnqueueAsync(AuthorizedScopeSet authorizedScopes, OutboxMessage message, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Provider-neutral predicate that every retrieval adapter applies before it assigns a provider rank.
/// It intentionally fixes normal recall to active, non-expired records; callers cannot widen either rule.
/// </summary>
public sealed record MemorySearchEligibility(
    AuthorizedScopeSet AuthorizedScopes,
    IReadOnlySet<MemoryRecordType>? Types,
    DateTimeOffset AsOfUtc)
{
    public MemoryLifecycleStatus RequiredLifecycleStatus => MemoryLifecycleStatus.Active;

    public bool ExcludesExpiredRecords => true;

    public bool IsEligible(MemoryRecord record) =>
        AuthorizedScopes.Contains(record.Scope) &&
        record.Status == RequiredLifecycleStatus &&
        (record.ExpiresAt is null || record.ExpiresAt > AsOfUtc) &&
        (Types is null || Types.Contains(record.Type));
}

public sealed record SearchPortRequest(
    MemorySearchEligibility Eligibility,
    string? QueryText,
    ReadOnlyMemory<float>? QueryVector,
    EmbeddingReference? QueryEmbedding,
    EmbeddingContract? EmbeddingContract,
    int Limit);

public sealed record SearchPortCandidate(
    MemoryRecord Record,
    int ProviderRank,
    double ProviderScore,
    EmbeddingReference? Embedding);

public interface IVectorSearch
{
    Task<IReadOnlyList<SearchPortCandidate>> SearchAsync(SearchPortRequest request, CancellationToken cancellationToken);
}

public interface ILexicalSearch
{
    Task<IReadOnlyList<SearchPortCandidate>> SearchAsync(SearchPortRequest request, CancellationToken cancellationToken);
}

public sealed record GraphRerankRequest(
    MemorySearchEligibility Eligibility,
    IReadOnlyList<MemoryRecord> Candidates);

public sealed record GraphRerankResult(MemoryId MemoryId, double Score);

public interface IMemoryGraph
{
    Task<IReadOnlyList<GraphRerankResult>> RerankAsync(GraphRerankRequest request, CancellationToken cancellationToken);
}

public sealed record EmbeddingResult(EmbeddingReference Reference, ReadOnlyMemory<float> Vector);

public interface IEmbeddingProvider
{
    Task<EmbeddingResult> CreateAsync(
        string redactedContent,
        EmbeddingContract contract,
        CancellationToken cancellationToken);
}

public sealed record EmbeddingPolicyResult(EmbeddingContract? Contract, string PolicyVersion)
{
    public bool IsConfigured => Contract is not null;
}

public interface IEmbeddingPolicy
{
    Task<EmbeddingPolicyResult> GetAsync(MemoryScope scope, CancellationToken cancellationToken);
}

public sealed record RetentionPolicyResult(bool IsConfigured, bool AllowsForget, string PolicyVersion);

public interface IRetentionPolicy
{
    Task<RetentionPolicyResult> EvaluateForgetAsync(
        MemoryRecord record,
        ActorId actor,
        CancellationToken cancellationToken);
}

public interface IOutbox
{
    Task AcknowledgeAsync(string messageId, CancellationToken cancellationToken);
}

public interface ICommandReceiver
{
    Task<RememberResult> ReceiveAsync(RememberCommand command, CancellationToken cancellationToken);
}
