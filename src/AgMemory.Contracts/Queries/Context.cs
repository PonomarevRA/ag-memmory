namespace AgMemory.Contracts;

/// <summary>
/// Requests hot memory that is authorised for one exact scope.
/// </summary>
public sealed record HotMemoryReadRequest(
    ActorId Actor,
    MemoryScope RequestedScope,
    ContractVersion ContractVersion);

/// <summary>
/// Requests an authorised, token-bounded context assembled from durable memory.
/// </summary>
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
    bool AllowSingleSummaryFallback = false,
    ReadOnlyMemory<float>? QueryVector = null);

/// <summary>
/// Identifies the authorised memory evidence supporting generated context.
/// </summary>
public sealed record MemoryCitation(MemoryId MemoryId, IReadOnlyList<string> EvidenceIds);

/// <summary>
/// Contains token-bounded context, citations and a privacy-safe error if assembly could not complete.
/// </summary>
public sealed record MemoryContext(
    string Content,
    IReadOnlyList<MemoryCitation> Citations,
    int EstimatedTokenCost,
    int OmittedCount,
    MemoryError? Error,
    ContractVersion ContractVersion,
    ContractVersion RetrievalConfigurationVersion,
    ContractVersion ContextConfigurationVersion);
