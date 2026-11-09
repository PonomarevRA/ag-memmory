using Agm.Memory.Abstractions;

namespace Agm.Memory;

public sealed class MemoryGraphReranker : IMemoryGraphReranker
{
    private const int HardMaximumNeighbors = 10;
    private const int MaximumSignalLength = 128;

    public IReadOnlyList<MemoryGraphCandidate> Rerank(
        MemoryGraphRerankRequest request,
        MemoryGraphRerankOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        if (!options.Enabled) return request.Candidates;
        if (request.Scope.TenantId == Guid.Empty)
            throw new ArgumentException("A memory graph tenant is required.", nameof(request));
        if (request.Candidates.Count == 0) return request.Candidates;
        if (request.Candidates.Any(candidate => candidate.TenantId != request.Scope.TenantId))
            throw new ArgumentException("Memory graph candidates must belong to the requested tenant.", nameof(request));
        if (request.Candidates.Any(candidate => candidate.MemoryId == Guid.Empty ||
                                                !double.IsFinite(candidate.Score)))
            throw new ArgumentException("Memory graph candidates are invalid.", nameof(request));
        if (request.Candidates.GroupBy(candidate => candidate.MemoryId).Any(group => group.Count() > 1))
            throw new ArgumentException("Memory graph candidate ids must be unique.", nameof(request));

        var byId = request.Candidates.ToDictionary(candidate => candidate.MemoryId);
        var exactSeeds = request.ExactSeedIds.Where(byId.ContainsKey).ToHashSet();
        var personalization = Personalization(request.Candidates, exactSeeds);
        var adjacency = BuildAdjacency(request, options, byId);
        var current = personalization;
        var accumulated = byId.Keys.ToDictionary(id => id, _ => 0d);
        for (var step = 0; step < options.WalkSteps; step++)
        {
            var next = byId.Keys.ToDictionary(
                id => id,
                id => options.RestartProbability * personalization[id]);
            foreach (var (source, mass) in current)
            {
                if (mass <= 0) continue;
                if (!adjacency.TryGetValue(source, out var neighbors) || neighbors.Count == 0)
                {
                    foreach (var (seed, seedMass) in personalization)
                        next[seed] += (1 - options.RestartProbability) * mass * seedMass;
                    continue;
                }

                var totalWeight = neighbors.Sum(neighbor => neighbor.Weight);
                if (totalWeight <= 0) continue;
                foreach (var neighbor in neighbors)
                    next[neighbor.MemoryId] += (1 - options.RestartProbability) *
                                               mass * neighbor.Weight / totalWeight;
            }
            current = next;
            foreach (var (id, mass) in current) accumulated[id] += mass / options.WalkSteps;
        }

        var maximumBase = Math.Max(1, request.Candidates.Max(candidate => Math.Max(0, candidate.Score)));
        var session = SessionSignals(request.Session, options);
        return request.Candidates
            .Select((candidate, index) => new Ranked(
                candidate with
                {
                    Score = candidate.Score +
                            options.Lambda * maximumBase * accumulated[candidate.MemoryId] +
                            maximumBase * SessionBoost(candidate, session, options)
                },
                index,
                exactSeeds.Contains(candidate.MemoryId)))
            // Exact lexical/vector seeds are pinned so graph expansion can add recall but cannot evict them.
            .OrderByDescending(item => item.IsExactSeed)
            .ThenByDescending(item => item.Candidate.Score)
            .ThenBy(item => item.OriginalIndex)
            .ThenBy(item => item.Candidate.MemoryId)
            .Select(item => item.Candidate)
            .ToArray();
    }

    private static Dictionary<Guid, double> Personalization(
        IReadOnlyList<MemoryGraphCandidate> candidates,
        IReadOnlySet<Guid> exactSeeds)
    {
        var selected = exactSeeds.Count > 0
            ? candidates.Where(candidate => exactSeeds.Contains(candidate.MemoryId)).ToArray()
            : candidates.Where(candidate => candidate.Score > 0).ToArray();
        if (selected.Length == 0) selected = [candidates[0]];
        var total = selected.Sum(candidate => Math.Max(0, candidate.Score));
        var uniform = total <= 0;
        return candidates.ToDictionary(
            candidate => candidate.MemoryId,
            candidate => selected.Any(seed => seed.MemoryId == candidate.MemoryId)
                ? uniform ? 1d / selected.Length : Math.Max(0, candidate.Score) / total
                : 0);
    }

    private static Dictionary<Guid, IReadOnlyList<Neighbor>> BuildAdjacency(
        MemoryGraphRerankRequest request,
        MemoryGraphRerankOptions options,
        IReadOnlyDictionary<Guid, MemoryGraphCandidate> candidates)
    {
        var adjacency = candidates.Keys.ToDictionary(id => id, _ => new List<Neighbor>());
        foreach (var edge in request.Edges)
        {
            if (edge.TenantId != request.Scope.TenantId ||
                edge.FromMemoryId == edge.ToMemoryId ||
                !candidates.ContainsKey(edge.FromMemoryId) ||
                !candidates.ContainsKey(edge.ToMemoryId) ||
                !double.IsFinite(edge.Strength) || edge.Strength <= 0)
                continue;
            var configuredWeight = options.EffectiveEdgeWeights.For(edge.Kind);
            if (!double.IsFinite(configuredWeight) || configuredWeight <= 0) continue;
            var age = edge.ObservedAt is { } observedAt && request.Now > observedAt
                ? request.Now - observedAt
                : TimeSpan.Zero;
            var decay = Math.Pow(0.5, age.TotalSeconds /
                                      options.EffectiveEdgeDecayHalfLife.TotalSeconds);
            var weight = edge.Strength * configuredWeight * decay;
            if (!double.IsFinite(weight) || weight <= 0) continue;
            adjacency[edge.FromMemoryId].Add(new(edge.ToMemoryId, weight));
            if (edge.Bidirectional)
                adjacency[edge.ToMemoryId].Add(new(edge.FromMemoryId, weight));
        }

        var maximumNeighbors = Math.Min(HardMaximumNeighbors, options.MaximumNeighbors);
        return adjacency.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<Neighbor>)pair.Value
                .GroupBy(neighbor => neighbor.MemoryId)
                .Select(group => new Neighbor(group.Key, group.Sum(item => item.Weight)))
                .OrderByDescending(neighbor => neighbor.Weight)
                .ThenBy(neighbor => neighbor.MemoryId)
                .Take(maximumNeighbors)
                .ToArray());
    }

    private static SessionSignalSet SessionSignals(
        MemoryGraphSessionContext? session,
        MemoryGraphRerankOptions options)
    {
        if (!options.SessionBoostEnabled || session is null)
            return new([], []);
        return new(
            NormalizeSignals(session.EntityKeys, options.MaximumSessionSignals),
            NormalizeSignals(session.TopicKeys, options.MaximumSessionSignals));
    }

    private static HashSet<string> NormalizeSignals(IReadOnlyList<string>? values, int maximum) =>
        (values ?? [])
        .Take(maximum)
        .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumSignalLength)
        .Select(value => value.Trim().ToUpperInvariant())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Take(maximum)
        .ToHashSet(StringComparer.Ordinal);

    private static double SessionBoost(
        MemoryGraphCandidate candidate,
        SessionSignalSet session,
        MemoryGraphRerankOptions options)
    {
        if (!options.SessionBoostEnabled) return 0;
        var entities = NormalizeSignals(candidate.EntityKeys, options.MaximumSessionSignals);
        var topics = NormalizeSignals(candidate.TopicKeys, options.MaximumSessionSignals);
        var boost = entities.Count(session.Entities.Contains) * options.SessionEntityBoost +
                    topics.Count(session.Topics.Contains) * options.SessionTopicBoost;
        return Math.Min(options.MaximumSessionBoost, boost);
    }

    private static void Validate(MemoryGraphRerankOptions options)
    {
        var weights = options.EffectiveEdgeWeights;
        var configuredWeights = new[]
        {
            weights.SameEntity, weights.SameTopic, weights.SameSession,
            weights.References, weights.DerivedFrom, weights.SameProject
        };
        if (options.WalkSteps is < 1 or > 2 ||
            options.MaximumNeighbors is < 1 or > HardMaximumNeighbors ||
            !double.IsFinite(options.RestartProbability) || options.RestartProbability is < 0 or > 1 ||
            !double.IsFinite(options.Lambda) || options.Lambda is < 0 or > 1 ||
            options.EffectiveEdgeDecayHalfLife <= TimeSpan.Zero ||
            options.MaximumSessionSignals is < 1 or > 64 ||
            !double.IsFinite(options.SessionEntityBoost) || options.SessionEntityBoost < 0 ||
            !double.IsFinite(options.SessionTopicBoost) || options.SessionTopicBoost < 0 ||
            !double.IsFinite(options.MaximumSessionBoost) || options.MaximumSessionBoost < 0 ||
            configuredWeights.Any(weight => !double.IsFinite(weight) || weight < 0))
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private sealed record Neighbor(Guid MemoryId, double Weight);
    private sealed record Ranked(MemoryGraphCandidate Candidate, int OriginalIndex, bool IsExactSeed);
    private sealed record SessionSignalSet(HashSet<string> Entities, HashSet<string> Topics);
}
