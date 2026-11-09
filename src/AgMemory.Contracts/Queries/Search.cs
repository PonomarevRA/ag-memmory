namespace AgMemory.Contracts;

/// <summary>
/// Requests authorised hybrid retrieval of durable memory records.
/// </summary>
public sealed record MemorySearchRequest(
    ActorId Actor,
    MemoryScope RequestedScope,
    string? QueryText,
    EmbeddingReference? QueryEmbedding,
    IReadOnlySet<MemoryRecordType>? Types,
    IReadOnlySet<MemoryLifecycleStatus>? Statuses,
    int Limit,
    ContractVersion RetrievalConfigurationVersion,
    ContractVersion ContractVersion,
    ReadOnlyMemory<float>? QueryVector = null);

/// <summary>
/// Explains the deterministic contributions that produced a hybrid retrieval score.
/// </summary>
public sealed record RetrievalContribution(
    int? LexicalRank,
    int? VectorRank,
    double LexicalContribution,
    double VectorContribution,
    double GraphContribution,
    double FusedScore);

/// <summary>
/// Contains one authorised memory hit and the configuration used to rank it.
/// </summary>
public sealed record MemorySearchHit(
    MemoryRecord Record,
    int Rank,
    double ProviderScore,
    RetrievalContribution Contribution,
    ContractVersion RetrievalConfigurationVersion,
    ContractVersion? RerankerConfigurationVersion);

/// <summary>
/// Describes whether each optional retrieval source contributed to a query.
/// </summary>
public enum RetrievalSourceStatus
{
    NotRequested,
    Completed,
    Unavailable
}

/// <summary>
/// Provides provider-neutral execution state without an exception or provider detail.
/// </summary>
public sealed record RetrievalExecution(
    RetrievalSourceStatus Lexical,
    RetrievalSourceStatus Vector,
    RetrievalSourceStatus Graph);

/// <summary>
/// Returns authorised retrieval hits, safe failures and the versions that governed the result.
/// </summary>
public sealed record MemorySearchResult(
    IReadOnlyList<MemorySearchHit> Hits,
    MemoryError? Error,
    ContractVersion ContractVersion,
    ContractVersion RetrievalConfigurationVersion,
    RetrievalExecution? Execution = null);
