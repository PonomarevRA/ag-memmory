namespace Agm.Memory.Abstractions;

public enum MemoryGoldenQuerySlice
{
    ExactIdentifier = 0,
    SemanticParaphrase = 1,
    ShortFollowUp = 2,
    Freshness = 3,
    History = 4,
    Conflict = 5
}

public sealed record MemoryGoldenQuery(
    string QueryId,
    MemoryScope Scope,
    string Query,
    MemoryGoldenQuerySlice Slice,
    IReadOnlyList<string> RelevantMemoryIds,
    IReadOnlyList<string> ExpectedSourceIds,
    bool RequiresEvidence = true);

public sealed record MemoryGoldenCorpus(
    string SchemaVersion,
    string CorpusVersion,
    IReadOnlyList<MemoryGoldenQuery> Queries);

public sealed record MemoryEvaluationHit(
    string MemoryId,
    IReadOnlyList<string> SourceIds);

public sealed record MemoryQueryEvaluation(
    string QueryId,
    IReadOnlyList<MemoryEvaluationHit> Hits,
    TimeSpan Latency);

public sealed record MemoryQualityCriteria(
    int Rank = 10,
    double MinimumMeanRecall = 0.80,
    double MinimumMeanNdcg = 0.75,
    double MinimumEvidenceCoverage = 1.0,
    double MinimumExactIdentifierPassRate = 1.0,
    TimeSpan? P95LatencyBudget = null)
{
    public TimeSpan EffectiveP95LatencyBudget => P95LatencyBudget ?? TimeSpan.FromMilliseconds(250);
}

public sealed record MemoryQualityMetrics(
    double MeanRecall,
    double MeanNdcg,
    double EvidenceCoverage,
    double ExactIdentifierPassRate,
    TimeSpan P95Latency,
    int EvaluatedQueryCount);

public sealed record MemoryQualityReport(
    string CorpusVersion,
    MemoryQualityMetrics Metrics,
    IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}

public interface IMemoryGoldenCorpusEvaluator
{
    MemoryQualityReport Evaluate(
        MemoryGoldenCorpus corpus,
        IReadOnlyList<MemoryQueryEvaluation> evaluations,
        MemoryQualityCriteria criteria);
}
