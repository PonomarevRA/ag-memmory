namespace Agm.Memory.Abstractions;

/// <summary>Tenant and project boundary that every retrieval candidate must match.</summary>
public sealed record MemoryRetrievalScope(string TenantId, string ProjectId);

public enum MemoryRecordState
{
    Active,
    Superseded
}

/// <summary>Provider-neutral identity and lifecycle metadata for a retrievable memory.</summary>
public sealed record MemoryRetrievalDocument(
    string Id,
    string TenantId,
    string ProjectId,
    MemoryRecordState State);

/// <summary>A ranked candidate returned by a lexical search provider.</summary>
public sealed record LexicalMemoryCandidate(
    MemoryRetrievalDocument Document,
    int Rank,
    double Score);

/// <summary>A ranked candidate returned by an embedding/vector search provider.</summary>
public sealed record VectorMemoryCandidate(
    MemoryRetrievalDocument Document,
    int Rank,
    double Similarity);

public sealed record HybridRetrievalRequest(
    MemoryRetrievalScope Scope,
    IReadOnlyList<LexicalMemoryCandidate> LexicalCandidates,
    IReadOnlyList<VectorMemoryCandidate> VectorCandidates,
    IReadOnlySet<string>? ExactIds = null,
    int Limit = 10,
    int ReciprocalRankConstant = 60);

/// <summary>Explainable reciprocal-rank-fusion contribution for one result.</summary>
public sealed record ReciprocalRankFusionBreakdown(
    bool ExactIdMatch,
    int? LexicalRank,
    int? VectorRank,
    double LexicalContribution,
    double VectorContribution,
    double FusedScore);

public sealed record HybridMemoryResult(
    MemoryRetrievalDocument Document,
    int Rank,
    ReciprocalRankFusionBreakdown Explain);

public sealed record RetrievalEvaluationCase(
    IReadOnlySet<string> RelevantIds,
    IReadOnlyList<string> RankedIds);

public sealed record RetrievalEvaluationMetrics(
    double RecallAtK,
    double NormalizedDiscountedCumulativeGainAtK,
    double MeanReciprocalRank);
