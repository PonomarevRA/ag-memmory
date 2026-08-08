namespace Agm.Memory.Abstractions;

public enum MemoryQueryMode { Current = 0, History = 1 }

public enum MemoryContextRelationKind
{
    ReasonToDecision = 0,
    DecisionToImplementation = 1
}

public sealed record MemoryContextProvenance(
    Guid SourceFragmentId,
    string? ExternalReference = null);

/// <summary>Provider-neutral bridge from lexical/RRF and graph retrieval into final reranking.</summary>
public sealed record MemoryContextCandidate(
    Guid MemoryId,
    MemoryScope Scope,
    CanonicalMemoryType Type,
    CanonicalMemoryStatus Status,
    string Subject,
    string Content,
    long Version,
    double RrfScore,
    double GraphScore,
    double Relevance,
    double Confidence,
    bool ExactMatch = false,
    Guid? SupersededById = null,
    string? GroupKey = null,
    IReadOnlyList<MemoryContextProvenance>? Provenance = null);

public sealed record MemoryContextRelation(
    Guid FromMemoryId,
    Guid ToMemoryId,
    MemoryContextRelationKind Kind);

public sealed record MemoryContextRerankRequest(
    MemoryScope Scope,
    IReadOnlyList<MemoryContextCandidate> Candidates,
    MemoryQueryMode Mode = MemoryQueryMode.Current);

public sealed record MemoryContextRerankOptions(
    bool Enabled = false,
    double RelevanceWeight = 0.3,
    double RrfWeight = 0.1,
    double GraphWeight = 0.1,
    double ConfidenceWeight = 0.1,
    double CurrentWeight = 0.4,
    double CurrentActiveSignal = 1,
    double CurrentSupersededSignal = 0.05,
    double HistoryActiveSignal = 1,
    double HistorySupersededSignal = 0.85);

public sealed record MemoryContextScoreBreakdown(
    double Relevance,
    double Rrf,
    double Graph,
    double Confidence,
    double Current,
    double Total);

public sealed record RankedMemoryContextCandidate(
    MemoryContextCandidate Candidate,
    int Rank,
    MemoryContextScoreBreakdown Score);

public interface IMemoryContextReranker
{
    IReadOnlyList<RankedMemoryContextCandidate> Rerank(
        MemoryContextRerankRequest request,
        MemoryContextRerankOptions options);
}

public sealed record MemoryContextBuildRequest(
    MemoryScope Scope,
    IReadOnlyList<RankedMemoryContextCandidate> Candidates,
    IReadOnlyList<MemoryContextRelation>? Relations = null,
    MemoryQueryMode Mode = MemoryQueryMode.Current);

public sealed record MemoryContextBuildOptions(
    int MinimumMemories = 3,
    int MaximumMemories = 8,
    int TokenBudget = 1_000,
    double MinimumConfidence = 0.35);

public sealed record MemoryContextCitation(
    string Tag,
    Guid MemoryId,
    long Version,
    IReadOnlyList<MemoryContextProvenance> Provenance);

public sealed record MemoryContextResult(
    string Content,
    IReadOnlyList<Guid> MemoryIds,
    IReadOnlyList<MemoryContextCitation> Citations,
    int EstimatedTokens,
    bool UsedLowConfidenceFallback);

public interface IMemoryContextBuilder
{
    MemoryContextResult Build(
        MemoryContextBuildRequest request,
        MemoryContextBuildOptions options);
}
