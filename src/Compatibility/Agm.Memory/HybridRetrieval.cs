using Agm.Memory.Abstractions;

namespace Agm.Memory;

/// <summary>Deterministic, provider-neutral reciprocal-rank fusion.</summary>
public static class HybridMemoryRetrieval
{
    public static IReadOnlyList<HybridMemoryResult> Fuse(HybridRetrievalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var exactIds = request.ExactIds ?? new HashSet<string>(StringComparer.Ordinal);
        var items = new Dictionary<string, MutableFusion>(StringComparer.Ordinal);
        foreach (var candidate in request.LexicalCandidates
                     .Where(item => InScope(item.Document, request.Scope)))
        {
            var item = GetOrAdd(items, candidate.Document);
            if (item.LexicalRank is null || candidate.Rank < item.LexicalRank)
                item.LexicalRank = candidate.Rank;
        }
        foreach (var candidate in request.VectorCandidates
                     .Where(item => InScope(item.Document, request.Scope)))
        {
            var item = GetOrAdd(items, candidate.Document);
            if (item.VectorRank is null || candidate.Rank < item.VectorRank)
                item.VectorRank = candidate.Rank;
        }

        return items.Values
            .Select(item => Explain(item, exactIds.Contains(item.Document.Id), request.ReciprocalRankConstant))
            .OrderByDescending(item => item.Explain.ExactIdMatch)
            .ThenByDescending(item => item.Explain.FusedScore)
            .ThenBy(item => item.Document.Id, StringComparer.Ordinal)
            .Take(request.Limit)
            .Select((item, index) => item with { Rank = index + 1 })
            .ToArray();
    }

    private static bool InScope(MemoryRetrievalDocument document, MemoryRetrievalScope scope) =>
        document.State == MemoryRecordState.Active &&
        string.Equals(document.TenantId, scope.TenantId, StringComparison.Ordinal) &&
        string.Equals(document.ProjectId, scope.ProjectId, StringComparison.Ordinal);

    private static MutableFusion GetOrAdd(
        IDictionary<string, MutableFusion> items,
        MemoryRetrievalDocument document)
    {
        if (items.TryGetValue(document.Id, out var existing)) return existing;
        var created = new MutableFusion(document);
        items.Add(document.Id, created);
        return created;
    }

    private static HybridMemoryResult Explain(MutableFusion item, bool exact, int constant)
    {
        var lexical = item.LexicalRank is { } lexicalRank ? 1d / (constant + lexicalRank) : 0d;
        var vector = item.VectorRank is { } vectorRank ? 1d / (constant + vectorRank) : 0d;
        return new(item.Document, 0,
            new(exact, item.LexicalRank, item.VectorRank, lexical, vector, lexical + vector));
    }

    private static void Validate(HybridRetrievalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Scope);
        ArgumentNullException.ThrowIfNull(request.LexicalCandidates);
        ArgumentNullException.ThrowIfNull(request.VectorCandidates);
        if (string.IsNullOrWhiteSpace(request.Scope.TenantId))
            throw new ArgumentException("TenantId is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Scope.ProjectId))
            throw new ArgumentException("ProjectId is required.", nameof(request));
        if (request.Limit <= 0) throw new ArgumentOutOfRangeException(nameof(request), "Limit must be positive.");
        if (request.ReciprocalRankConstant < 0)
            throw new ArgumentOutOfRangeException(nameof(request), "The RRF constant cannot be negative.");
        foreach (var candidate in request.LexicalCandidates)
            ValidateCandidate(candidate.Document, candidate.Rank, candidate.Score, request.ReciprocalRankConstant);
        foreach (var candidate in request.VectorCandidates)
            ValidateCandidate(candidate.Document, candidate.Rank, candidate.Similarity, request.ReciprocalRankConstant);
    }

    private static void ValidateCandidate(
        MemoryRetrievalDocument document, int rank, double score, int constant)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(document.Id) ||
            string.IsNullOrWhiteSpace(document.TenantId) ||
            string.IsNullOrWhiteSpace(document.ProjectId))
            throw new ArgumentException("Candidate identity and scope are required.");
        if (rank <= 0 || constant + rank <= 0) throw new ArgumentOutOfRangeException(nameof(rank));
        if (!double.IsFinite(score)) throw new ArgumentOutOfRangeException(nameof(score));
    }

    private sealed class MutableFusion(MemoryRetrievalDocument document)
    {
        public MemoryRetrievalDocument Document { get; } = document;
        public int? LexicalRank { get; set; }
        public int? VectorRank { get; set; }
    }
}

/// <summary>Binary-relevance metrics with deterministic duplicate handling.</summary>
public static class RetrievalEvaluation
{
    public static RetrievalEvaluationMetrics Evaluate(RetrievalEvaluationCase evaluation, int k)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(evaluation.RelevantIds);
        ArgumentNullException.ThrowIfNull(evaluation.RankedIds);
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));

        var relevant = new HashSet<string>(
            evaluation.RelevantIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
        var ranked = evaluation.RankedIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(k)
            .ToArray();
        if (relevant.Count == 0) return new(0d, 0d, 0d);

        var hits = ranked.Count(relevant.Contains);
        var recall = (double)hits / relevant.Count;
        var dcg = 0d;
        for (var index = 0; index < ranked.Length; index++)
            if (relevant.Contains(ranked[index])) dcg += Discount(index);
        var ideal = Enumerable.Range(0, Math.Min(k, relevant.Count)).Sum(Discount);
        var first = Array.FindIndex(ranked, relevant.Contains);
        return new(recall, ideal == 0d ? 0d : dcg / ideal, first < 0 ? 0d : 1d / (first + 1));
    }

    public static RetrievalEvaluationMetrics EvaluateMean(
        IEnumerable<RetrievalEvaluationCase> evaluations, int k)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        var metrics = evaluations.Select(item => Evaluate(item, k)).ToArray();
        if (metrics.Length == 0) return new(0d, 0d, 0d);
        return new(
            metrics.Average(item => item.RecallAtK),
            metrics.Average(item => item.NormalizedDiscountedCumulativeGainAtK),
            metrics.Average(item => item.MeanReciprocalRank));
    }

    private static double Discount(int zeroBasedRank) => 1d / Math.Log2(zeroBasedRank + 2d);
}
