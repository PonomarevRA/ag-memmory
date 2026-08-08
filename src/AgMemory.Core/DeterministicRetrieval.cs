using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Pure deterministic RRF and eligibility checks; provider ranks are never invented.</summary>
public static class DeterministicRetrieval
{
    public static IReadOnlyList<MemorySearchHit> Fuse(
        IEnumerable<SearchPortCandidate> lexical,
        IEnumerable<SearchPortCandidate> vector,
        AuthorizedScopeSet authorizedScopes,
        DateTimeOffset utcNow,
        IReadOnlySet<MemoryRecordType>? types,
        IReadOnlySet<MemoryLifecycleStatus>? statuses,
        int limit,
        int reciprocalRankConstant,
        ContractVersion retrievalConfigurationVersion,
        ContractVersion rerankerConfigurationVersion,
        IReadOnlyDictionary<MemoryId, double>? graphScores = null)
    {
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(authorizedScopes);
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (reciprocalRankConstant < 0) throw new ArgumentOutOfRangeException(nameof(reciprocalRankConstant));

        var fused = new Dictionary<MemoryId, MutableCandidate>();
        AddCandidates(lexical, isLexical: true);
        AddCandidates(vector, isLexical: false);

        return fused.Values
            .Select(item => ToHit(item, graphScores?.GetValueOrDefault(item.Record.Id) ?? 0d))
            .OrderByDescending(hit => hit.Contribution.FusedScore + hit.Contribution.GraphContribution)
            .ThenBy(hit => hit.Record.Id.Value, StringComparer.Ordinal)
            .Take(limit)
            .Select((hit, index) => hit with { Rank = index + 1 })
            .ToArray();

        void AddCandidates(IEnumerable<SearchPortCandidate> candidates, bool isLexical)
        {
            foreach (var candidate in candidates)
            {
                ValidateCandidate(candidate);
                if (!IsEligible(candidate.Record, authorizedScopes, utcNow, types, statuses)) continue;
                if (!fused.TryGetValue(candidate.Record.Id, out var target))
                {
                    target = new MutableCandidate(candidate.Record);
                    fused.Add(candidate.Record.Id, target);
                }

                if (isLexical)
                {
                    if (target.LexicalRank is null || candidate.ProviderRank < target.LexicalRank)
                    {
                        target.LexicalRank = candidate.ProviderRank;
                        target.LexicalScore = candidate.ProviderScore;
                    }
                }
                else if (target.VectorRank is null || candidate.ProviderRank < target.VectorRank)
                {
                    target.VectorRank = candidate.ProviderRank;
                    target.VectorScore = candidate.ProviderScore;
                }
            }
        }

        MemorySearchHit ToHit(MutableCandidate item, double graphScore)
        {
            if (!double.IsFinite(graphScore)) graphScore = 0d;
            var lexicalContribution = item.LexicalRank is { } lexicalRank
                ? 1d / (reciprocalRankConstant + lexicalRank) : 0d;
            var vectorContribution = item.VectorRank is { } vectorRank
                ? 1d / (reciprocalRankConstant + vectorRank) : 0d;
            return new MemorySearchHit(
                item.Record,
                0,
                Math.Max(item.LexicalScore ?? double.NegativeInfinity, item.VectorScore ?? double.NegativeInfinity),
                new RetrievalContribution(
                    item.LexicalRank,
                    item.VectorRank,
                    lexicalContribution,
                    vectorContribution,
                    graphScore,
                    lexicalContribution + vectorContribution),
                retrievalConfigurationVersion,
                rerankerConfigurationVersion);
        }
    }

    public static bool IsEligible(
        MemoryRecord record,
        AuthorizedScopeSet authorizedScopes,
        DateTimeOffset utcNow,
        IReadOnlySet<MemoryRecordType>? types = null,
        IReadOnlySet<MemoryLifecycleStatus>? statuses = null) =>
        authorizedScopes.Contains(record.Scope) &&
        record.Status == MemoryLifecycleStatus.Active &&
        (record.ExpiresAt is null || record.ExpiresAt > utcNow) &&
        (types is null || types.Contains(record.Type)) &&
        (statuses is null || statuses.Contains(record.Status));

    private static void ValidateCandidate(SearchPortCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.ProviderRank <= 0) throw new ArgumentOutOfRangeException(nameof(candidate.ProviderRank));
        if (!double.IsFinite(candidate.ProviderScore)) throw new ArgumentOutOfRangeException(nameof(candidate.ProviderScore));
    }

    private sealed class MutableCandidate(MemoryRecord record)
    {
        public MemoryRecord Record { get; } = record;
        public int? LexicalRank { get; set; }
        public int? VectorRank { get; set; }
        public double? LexicalScore { get; set; }
        public double? VectorScore { get; set; }
    }
}
