using System.Text;
using Agm.Memory.Abstractions;

namespace Agm.Memory;

public sealed class DeterministicMemoryContextReranker : IMemoryContextReranker
{
    public IReadOnlyList<RankedMemoryContextCandidate> Rerank(
        MemoryContextRerankRequest request,
        MemoryContextRerankOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        if (!options.Enabled)
            return request.Candidates.Select((candidate, index) => new RankedMemoryContextCandidate(
                candidate, index + 1, new(candidate.Relevance, candidate.RrfScore,
                    candidate.GraphScore, candidate.Confidence, 0, candidate.Relevance))).ToArray();
        ValidateScope(request.Scope, request.Candidates);

        return request.Candidates
            .Select((candidate, index) =>
            {
                ValidateCandidate(candidate);
                var current = CurrentSignal(candidate.Status, request.Mode, options);
                var total = candidate.Relevance * options.RelevanceWeight +
                            candidate.RrfScore * options.RrfWeight +
                            candidate.GraphScore * options.GraphWeight +
                            candidate.Confidence * options.ConfidenceWeight +
                            current * options.CurrentWeight;
                // Exact matches settle ties, but lifecycle/currentness remains authoritative.
                return new MutableRank(candidate, index,
                    new(candidate.Relevance, candidate.RrfScore, candidate.GraphScore,
                        candidate.Confidence, current, total));
            })
            .OrderByDescending(item => item.Score.Total)
            .ThenByDescending(item => item.Candidate.ExactMatch)
            .ThenBy(item => item.OriginalIndex)
            .ThenBy(item => item.Candidate.MemoryId)
            .Select((item, index) => new RankedMemoryContextCandidate(
                item.Candidate, index + 1, item.Score))
            .ToArray();
    }

    private static double CurrentSignal(
        CanonicalMemoryStatus status,
        MemoryQueryMode mode,
        MemoryContextRerankOptions options) => (mode, status) switch
        {
            (MemoryQueryMode.Current, CanonicalMemoryStatus.Active) => options.CurrentActiveSignal,
            (MemoryQueryMode.Current, CanonicalMemoryStatus.Superseded) => options.CurrentSupersededSignal,
            (MemoryQueryMode.History, CanonicalMemoryStatus.Active) => options.HistoryActiveSignal,
            (MemoryQueryMode.History, CanonicalMemoryStatus.Superseded) => options.HistorySupersededSignal,
            _ => 0
        };

    private static void ValidateOptions(MemoryContextRerankOptions options)
    {
        var values = new[]
        {
            options.RelevanceWeight, options.RrfWeight, options.GraphWeight,
            options.ConfidenceWeight, options.CurrentWeight,
            options.CurrentActiveSignal, options.CurrentSupersededSignal,
            options.HistoryActiveSignal, options.HistorySupersededSignal
        };
        if (values.Any(value => !double.IsFinite(value) || value < 0) ||
            options.CurrentActiveSignal <= options.CurrentSupersededSignal)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    internal static void ValidateScope(
        MemoryScope requested,
        IEnumerable<MemoryContextCandidate> candidates)
    {
        requested.Validate();
        if (candidates.Any(candidate => candidate.Scope.TenantId != requested.TenantId ||
                                        candidate.Scope.ProjectId != requested.ProjectId))
            throw new ArgumentException("Memory context candidates must match the requested scope.");
    }

    private static void ValidateCandidate(MemoryContextCandidate candidate)
    {
        if (candidate.MemoryId == Guid.Empty || candidate.Version <= 0 ||
            string.IsNullOrWhiteSpace(candidate.Content) ||
            new[] { candidate.RrfScore, candidate.GraphScore, candidate.Relevance, candidate.Confidence }
                .Any(value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentException("Memory context candidate is invalid.");
    }

    private sealed record MutableRank(
        MemoryContextCandidate Candidate,
        int OriginalIndex,
        MemoryContextScoreBreakdown Score);
}

public sealed class MemoryContextBuilder : IMemoryContextBuilder
{
    private const string Fallback = "No sufficiently supported memory is available for this query.";

    public MemoryContextResult Build(
        MemoryContextBuildRequest request,
        MemoryContextBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        DeterministicMemoryContextReranker.ValidateScope(
            request.Scope, request.Candidates.Select(candidate => candidate.Candidate));

        var eligible = request.Candidates
            .Where(item => item.Candidate.Confidence >= options.MinimumConfidence)
            .Where(item => item.Candidate.Status == CanonicalMemoryStatus.Active ||
                           request.Mode == MemoryQueryMode.History &&
                           item.Candidate.Status == CanonicalMemoryStatus.Superseded)
            .OrderBy(item => item.Rank)
            .ThenByDescending(item => item.Score.Total)
            .ThenBy(item => item.Candidate.MemoryId)
            .GroupBy(item => DeduplicationKey(item.Candidate), StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(options.MaximumMemories)
            .ToArray();
        if (eligible.Length < options.MinimumMemories)
            return LowConfidence(options.TokenBudget);

        var selected = OrderByRelations(eligible, request.Relations ?? [])
            .Take(options.MaximumMemories)
            .ToArray();
        var rendered = RenderWithinBudget(selected, options.TokenBudget, options.MinimumMemories);
        if (rendered is null) return LowConfidence(options.TokenBudget);
        return rendered;
    }

    private static MemoryContextResult? RenderWithinBudget(
        IReadOnlyList<RankedMemoryContextCandidate> selected,
        int tokenBudget,
        int minimumMemories)
    {
        var characterBudget = checked(tokenBudget * 4);
        for (var count = selected.Count; count >= minimumMemories; count--)
        {
            var chosen = selected.Take(count).ToArray();
            var prefixes = chosen.Select((item, index) => Prefix(item.Candidate, index + 1)).ToArray();
            var fixedCharacters = prefixes.Sum(prefix => prefix.Length) + count - 1;
            if (fixedCharacters + count > characterBudget) continue;
            var remaining = characterBudget - fixedCharacters;
            var lines = new string[count];
            for (var index = 0; index < count; index++)
            {
                var slots = count - index;
                var allowance = Math.Max(1, remaining / slots);
                var content = NormalizeText(chosen[index].Candidate.Content);
                var length = Math.Min(content.Length, allowance);
                lines[index] = prefixes[index] + content[..length];
                remaining -= length;
            }

            var contentText = string.Join('\n', lines);
            if (contentText.Length > characterBudget) continue;
            var citations = chosen.Select((item, index) => new MemoryContextCitation(
                $"M{index + 1}", item.Candidate.MemoryId, item.Candidate.Version,
                item.Candidate.Provenance ?? [])).ToArray();
            return new(contentText, chosen.Select(item => item.Candidate.MemoryId).ToArray(),
                citations, EstimateTokens(contentText), false);
        }
        return null;
    }

    private static IReadOnlyList<RankedMemoryContextCandidate> OrderByRelations(
        IReadOnlyList<RankedMemoryContextCandidate> candidates,
        IReadOnlyList<MemoryContextRelation> relations)
    {
        var byId = candidates.ToDictionary(item => item.Candidate.MemoryId);
        var outgoing = byId.Keys.ToDictionary(id => id, _ => new HashSet<Guid>());
        var indegree = byId.Keys.ToDictionary(id => id, _ => 0);
        foreach (var relation in relations
                     .Where(relation => byId.ContainsKey(relation.FromMemoryId) &&
                                        byId.ContainsKey(relation.ToMemoryId) &&
                                        relation.FromMemoryId != relation.ToMemoryId)
                     .OrderBy(relation => relation.Kind)
                     .ThenBy(relation => relation.FromMemoryId)
                     .ThenBy(relation => relation.ToMemoryId))
        {
            if (outgoing[relation.FromMemoryId].Add(relation.ToMemoryId))
                indegree[relation.ToMemoryId]++;
        }

        var remaining = new HashSet<Guid>(byId.Keys);
        var result = new List<RankedMemoryContextCandidate>(candidates.Count);
        while (remaining.Count > 0)
        {
            var next = remaining.Where(id => indegree[id] == 0)
                .OrderBy(id => byId[id].Candidate.GroupKey ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(id => byId[id].Rank)
                .ThenBy(id => id)
                .FirstOrDefault();
            // Deterministically break malformed relation cycles without looping.
            if (next == Guid.Empty)
                next = remaining.OrderBy(id => byId[id].Rank).ThenBy(id => id).First();
            remaining.Remove(next);
            result.Add(byId[next]);
            foreach (var target in outgoing[next]) indegree[target]--;
        }
        return result;
    }

    private static string Prefix(MemoryContextCandidate candidate, int citation) =>
        $"[M{citation}] [{NormalizeGroup(candidate.GroupKey)}] {NormalizeText(candidate.Subject)}: ";

    private static string NormalizeGroup(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "memory" : NormalizeText(value).ToLowerInvariant();

    private static string DeduplicationKey(MemoryContextCandidate candidate) =>
        $"{NormalizeGroup(candidate.GroupKey)}\n{NormalizeText(candidate.Content).ToUpperInvariant()}";

    private static string NormalizeText(string value) =>
        string.Join(' ', value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static MemoryContextResult LowConfidence(int tokenBudget)
    {
        var content = Fallback[..Math.Min(Fallback.Length, checked(tokenBudget * 4))];
        return new(content, [], [], EstimateTokens(content), true);
    }

    private static int EstimateTokens(string content) => (content.Length + 3) / 4;

    private static void ValidateOptions(MemoryContextBuildOptions options)
    {
        if (options.MinimumMemories is < 3 or > 8 ||
            options.MaximumMemories is < 3 or > 8 ||
            options.MinimumMemories > options.MaximumMemories ||
            options.TokenBudget is < 16 or > 32_000 ||
            !double.IsFinite(options.MinimumConfidence) || options.MinimumConfidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options));
    }
}
