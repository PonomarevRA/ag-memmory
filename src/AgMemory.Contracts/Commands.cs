namespace AgMemory.Contracts;

public sealed record CommandEnvelope(
    CommandId CommandId,
    string IdempotencyKey,
    ActorId Actor,
    CorrelationId CorrelationId,
    MemoryScope RequestedScope,
    ContractVersion ContractVersion)
{
    public void Validate()
    {
        CommandId.Validate(nameof(CommandId));
        Actor.Validate(nameof(Actor));
        CorrelationId.Validate(nameof(CorrelationId));
        ContractVersion.Validate(nameof(ContractVersion));
        ArgumentException.ThrowIfNullOrWhiteSpace(IdempotencyKey, nameof(IdempotencyKey));
        if (IdempotencyKey.Length > 256) throw new ArgumentOutOfRangeException(nameof(IdempotencyKey));
        RequestedScope.Validate();
    }
}

public enum EmbeddingMode { None, Required }

public sealed record MemoryRecordInput(
    MemoryRecordType Type,
    string CanonicalText,
    string? Reason,
    double Importance,
    double Confidence,
    IReadOnlyList<string> Entities,
    MemoryProvenance Provenance,
    DateTimeOffset? ExpiresAt = null,
    DecisionDetails? DecisionDetails = null,
    EmbeddingMode EmbeddingMode = EmbeddingMode.None);

public sealed record RememberCommand(CommandEnvelope Envelope, MemoryRecordInput Record);

public enum RememberOutcome { Created, Reinforced, IdempotencyReplay, DuplicateConflict, Failed }

public sealed record RememberResult(
    RememberOutcome Outcome,
    MemoryRecord? Memory,
    MemoryError? Error,
    ContractVersion ContractVersion);

public sealed record LifecycleCommand(
    CommandEnvelope Envelope,
    MemoryId MemoryId,
    MemoryLifecycleAction Action,
    long ExpectedVersion,
    MemoryId? RelatedMemoryId = null,
    string? Reason = null);

public enum LifecycleOutcome { Applied, NotFound, StaleVersion, InvalidTransition, Failed }

public sealed record LifecycleResult(
    LifecycleOutcome Outcome,
    MemoryRecord? Memory,
    long? CurrentVersion,
    MemoryError? Error,
    ContractVersion ContractVersion);

public sealed record AppendHotMemoryCommand(
    CommandEnvelope Envelope,
    string Content,
    MemoryProvenance Provenance,
    DateTimeOffset ExpiresAt,
    string CaptureKey,
    long ExpectedVersion);

public enum HotMemoryOutcome { Created, Updated, IdempotencyReplay, Conflict, StaleVersion, Failed }

public sealed record HotMemoryResult(
    HotMemoryOutcome Outcome,
    SessionHotMemory? Memory,
    long? CurrentVersion,
    MemoryError? Error,
    ContractVersion ContractVersion);

public sealed record ForgetMemoryCommand(
    CommandEnvelope Envelope,
    MemoryId MemoryId,
    long ExpectedVersion);

public enum ForgetOutcome { Forgotten, NotFound, StaleVersion, Failed }

public sealed record ForgetResult(
    ForgetOutcome Outcome,
    long? CurrentVersion,
    MemoryError? Error,
    ContractVersion ContractVersion);

public enum MemoryErrorCode
{
    InvalidArgument,
    UnsupportedContractVersion,
    Unauthorized,
    RedactionRejected,
    IdempotencyKeyConflict,
    Conflict,
    StaleVersion,
    NotFound,
    InvalidTransition,
    PolicyNotConfigured,
    EmbeddingContractMismatch,
    DependencyFailure
}

/// <summary>Privacy-safe failure shape. It deliberately has no free-form message field.</summary>
public sealed record MemoryError(MemoryErrorCode Code, string? Field, string? PolicyOrRuleVersion = null);

public enum MemoryOperation { Remember, Lifecycle, AppendHotMemory, Forget, Search, BuildContext, ReadHotMemory }

public sealed record MemorySearchRequest(
    ActorId Actor,
    MemoryScope RequestedScope,
    string? QueryText,
    EmbeddingReference? QueryEmbedding,
    IReadOnlySet<MemoryRecordType>? Types,
    IReadOnlySet<MemoryLifecycleStatus>? Statuses,
    int Limit,
    ContractVersion RetrievalConfigurationVersion,
    ContractVersion ContractVersion);

public sealed record HotMemoryReadRequest(
    ActorId Actor,
    MemoryScope RequestedScope,
    ContractVersion ContractVersion);

public sealed record MemoryContextRequest(
    ActorId Actor,
    MemoryScope RequestedScope,
    string? QueryText,
    EmbeddingReference? QueryEmbedding,
    IReadOnlySet<MemoryRecordType>? Types,
    int SearchLimit,
    int TokenBudget,
    bool RequireCitations,
    ContractVersion RetrievalConfigurationVersion,
    ContractVersion ContextConfigurationVersion,
    ContractVersion ContractVersion,
    bool AllowSingleSummaryFallback = false);

public sealed record RetrievalContribution(
    int? LexicalRank,
    int? VectorRank,
    double LexicalContribution,
    double VectorContribution,
    double GraphContribution,
    double FusedScore);

public sealed record MemorySearchHit(
    MemoryRecord Record,
    int Rank,
    double ProviderScore,
    RetrievalContribution Contribution,
    ContractVersion RetrievalConfigurationVersion,
    ContractVersion? RerankerConfigurationVersion);

public sealed record MemorySearchResult(
    IReadOnlyList<MemorySearchHit> Hits,
    MemoryError? Error,
    ContractVersion ContractVersion,
    ContractVersion RetrievalConfigurationVersion);

public sealed record MemoryCitation(MemoryId MemoryId, IReadOnlyList<string> EvidenceIds);

public sealed record MemoryContext(
    string Content,
    IReadOnlyList<MemoryCitation> Citations,
    int EstimatedTokenCost,
    int OmittedCount,
    MemoryError? Error,
    ContractVersion ContractVersion,
    ContractVersion RetrievalConfigurationVersion,
    ContractVersion ContextConfigurationVersion);
