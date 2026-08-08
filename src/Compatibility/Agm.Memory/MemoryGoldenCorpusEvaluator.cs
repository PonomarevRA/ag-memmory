using Agm.Memory.Abstractions;

namespace Agm.Memory;

public sealed class MemoryGoldenCorpusEvaluator : IMemoryGoldenCorpusEvaluator
{
    public const string SupportedSchemaVersion = "1.0";

    public MemoryQualityReport Evaluate(
        MemoryGoldenCorpus corpus,
        IReadOnlyList<MemoryQueryEvaluation> evaluations,
        MemoryQualityCriteria criteria)
    {
        Validate(corpus, evaluations, criteria);

        var results = evaluations.ToDictionary(item => item.QueryId, StringComparer.Ordinal);
        var recalls = new List<double>(corpus.Queries.Count);
        var ndcgs = new List<double>(corpus.Queries.Count);
        var evidencePasses = 0;
        var evidenceQueries = 0;
        var exactPasses = 0;
        var exactQueries = 0;
        var latencies = new List<TimeSpan>(corpus.Queries.Count);

        foreach (var query in corpus.Queries)
        {
            results.TryGetValue(query.QueryId, out var evaluation);
            var hits = evaluation?.Hits.Take(criteria.Rank).ToArray() ?? [];
            var relevant = query.RelevantMemoryIds.ToHashSet(StringComparer.Ordinal);
            var relevantHits = hits.Where(hit => relevant.Contains(hit.MemoryId)).ToArray();
            recalls.Add((double)relevantHits.Select(hit => hit.MemoryId).Distinct(StringComparer.Ordinal).Count() / relevant.Count);
            ndcgs.Add(Ndcg(hits, relevant, criteria.Rank));
            latencies.Add(evaluation?.Latency ?? TimeSpan.MaxValue);

            if (query.RequiresEvidence)
            {
                evidenceQueries++;
                var expectedSources = query.ExpectedSourceIds.ToHashSet(StringComparer.Ordinal);
                if (relevantHits.Any(hit => hit.SourceIds.Any(expectedSources.Contains))) evidencePasses++;
            }

            if (query.Slice == MemoryGoldenQuerySlice.ExactIdentifier)
            {
                exactQueries++;
                if (relevantHits.Length > 0) exactPasses++;
            }
        }

        latencies.Sort();
        var metrics = new MemoryQualityMetrics(
            recalls.Average(),
            ndcgs.Average(),
            evidenceQueries == 0 ? 1 : (double)evidencePasses / evidenceQueries,
            exactQueries == 0 ? 1 : (double)exactPasses / exactQueries,
            Percentile95(latencies),
            corpus.Queries.Count);
        var failures = GetFailures(metrics, criteria);
        return new MemoryQualityReport(corpus.CorpusVersion, metrics, failures);
    }

    private static void Validate(
        MemoryGoldenCorpus corpus,
        IReadOnlyList<MemoryQueryEvaluation> evaluations,
        MemoryQualityCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(evaluations);
        ArgumentNullException.ThrowIfNull(criteria);
        if (corpus.SchemaVersion != SupportedSchemaVersion)
            throw new NotSupportedException($"Golden corpus schema '{corpus.SchemaVersion}' is not supported.");
        if (string.IsNullOrWhiteSpace(corpus.CorpusVersion))
            throw new ArgumentException("Corpus version is required.", nameof(corpus));
        if (corpus.Queries.Count == 0) throw new ArgumentException("Golden corpus must contain queries.", nameof(corpus));
        if (criteria.Rank < 1) throw new ArgumentOutOfRangeException(nameof(criteria));
        ValidateFraction(criteria.MinimumMeanRecall, nameof(criteria.MinimumMeanRecall));
        ValidateFraction(criteria.MinimumMeanNdcg, nameof(criteria.MinimumMeanNdcg));
        ValidateFraction(criteria.MinimumEvidenceCoverage, nameof(criteria.MinimumEvidenceCoverage));
        ValidateFraction(criteria.MinimumExactIdentifierPassRate, nameof(criteria.MinimumExactIdentifierPassRate));
        if (criteria.EffectiveP95LatencyBudget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(criteria));

        EnsureUnique(corpus.Queries.Select(item => item.QueryId), "query IDs");
        EnsureUnique(evaluations.Select(item => item.QueryId), "evaluation query IDs");
        foreach (var query in corpus.Queries)
        {
            if (string.IsNullOrWhiteSpace(query.QueryId) || string.IsNullOrWhiteSpace(query.Query))
                throw new ArgumentException("Golden queries require an ID and query text.", nameof(corpus));
            if (query.RelevantMemoryIds.Count == 0)
                throw new ArgumentException($"Golden query '{query.QueryId}' has no relevant memories.", nameof(corpus));
            if (query.RequiresEvidence && query.ExpectedSourceIds.Count == 0)
                throw new ArgumentException($"Golden query '{query.QueryId}' requires evidence but has no expected sources.", nameof(corpus));
        }
        foreach (var evaluation in evaluations)
            if (evaluation.Latency < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(evaluations), "Latency must not be negative.");
    }

    private static void EnsureUnique(IEnumerable<string> values, string label)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!seen.Add(value)) throw new ArgumentException($"Golden evaluation contains duplicate {label}: '{value}'.");
    }

    private static void ValidateFraction(double value, string name)
    {
        if (double.IsNaN(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(name);
    }

    private static double Ndcg(
        IReadOnlyList<MemoryEvaluationHit> hits,
        IReadOnlySet<string> relevant,
        int rank)
    {
        double score = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < hits.Count && index < rank; index++)
            if (seen.Add(hits[index].MemoryId) && relevant.Contains(hits[index].MemoryId))
                score += 1 / Math.Log2(index + 2);

        double ideal = 0;
        for (var index = 0; index < Math.Min(rank, relevant.Count); index++) ideal += 1 / Math.Log2(index + 2);
        return ideal == 0 ? 0 : score / ideal;
    }

    private static TimeSpan Percentile95(IReadOnlyList<TimeSpan> sorted)
    {
        var index = (int)Math.Ceiling(sorted.Count * 0.95) - 1;
        return sorted[Math.Max(index, 0)];
    }

    private static IReadOnlyList<string> GetFailures(MemoryQualityMetrics metrics, MemoryQualityCriteria criteria)
    {
        var failures = new List<string>();
        if (metrics.MeanRecall < criteria.MinimumMeanRecall) failures.Add("MeanRecallBelowThreshold");
        if (metrics.MeanNdcg < criteria.MinimumMeanNdcg) failures.Add("MeanNdcgBelowThreshold");
        if (metrics.EvidenceCoverage < criteria.MinimumEvidenceCoverage) failures.Add("EvidenceCoverageBelowThreshold");
        if (metrics.ExactIdentifierPassRate < criteria.MinimumExactIdentifierPassRate) failures.Add("ExactIdentifierRegression");
        if (metrics.P95Latency > criteria.EffectiveP95LatencyBudget) failures.Add("P95LatencyBudgetExceeded");
        return failures;
    }
}
