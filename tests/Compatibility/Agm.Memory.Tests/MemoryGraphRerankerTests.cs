using Agm.Memory.Abstractions;
using Xunit;

namespace Agm.Memory.Tests;

public sealed class MemoryGraphRerankerTests
{
    private static readonly Guid Tenant = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LinkedTwoStepChainImprovesRecall()
    {
        var seed = Candidate(1, 1);
        var first = Candidate(2, 0.15);
        var second = Candidate(3, 0.14);
        var unrelated = Candidate(4, 0.2);
        var candidates = new[] { seed, unrelated, first, second };
        var result = Rerank(candidates,
            [Edge(seed, first, MemoryGraphEdgeKind.DerivedFrom),
             Edge(first, second, MemoryGraphEdgeKind.DerivedFrom)],
            seed.MemoryId,
            new(Enabled: true, WalkSteps: 2, RestartProbability: 0.2, Lambda: 1));

        Assert.True(Score(result, second) > second.Score);
        Assert.True(Index(result, second) < Index(result, unrelated));
    }

    [Fact]
    public void SameProjectIsAWeakConfigurableSignal()
    {
        var seed = Candidate(1, 1);
        var entity = Candidate(2, 0.1);
        var project = Candidate(3, 0.1);
        var result = Rerank([seed, project, entity],
            [Edge(seed, entity, MemoryGraphEdgeKind.SameEntity),
             Edge(seed, project, MemoryGraphEdgeKind.SameProject)], seed.MemoryId);

        Assert.True(Score(result, entity) > Score(result, project));
    }

    [Fact]
    public void ExactSeedCannotBeDisplacedByGraphExpansion()
    {
        var exact = Candidate(1, 0.01);
        var linked = Candidate(2, 10);
        var result = Rerank([linked, exact],
            [Edge(exact, linked, MemoryGraphEdgeKind.SameEntity)], exact.MemoryId,
            new(Enabled: true, Lambda: 1));

        Assert.Equal(exact.MemoryId, result[0].MemoryId);
    }

    [Fact]
    public void DisabledRerankingIsIdentity()
    {
        IReadOnlyList<MemoryGraphCandidate> candidates = [Candidate(1, 0.2), Candidate(2, 0.9)];
        var request = Request(candidates, [], candidates[0].MemoryId);

        var result = new MemoryGraphReranker().Rerank(request, new());

        Assert.Same(candidates, result);
    }

    [Fact]
    public void CyclesAndMoreThanTenNeighborsRemainBoundedAndDeterministic()
    {
        var seed = Candidate(1, 1);
        var candidates = Enumerable.Range(1, 16).Select(index => Candidate(index, 0.1)).ToArray();
        candidates[0] = seed;
        var edges = candidates.Skip(1)
            .Select((candidate, index) => new MemoryGraphEdge(
                Tenant, seed.MemoryId, candidate.MemoryId, MemoryGraphEdgeKind.SameTopic,
                Strength: 20 - index, Bidirectional: true))
            .Concat([
                Edge(candidates[1], candidates[2], MemoryGraphEdgeKind.References),
                Edge(candidates[2], seed, MemoryGraphEdgeKind.References)
            ]).ToArray();
        var options = new MemoryGraphRerankOptions(Enabled: true, WalkSteps: 2, MaximumNeighbors: 10);

        var first = Rerank(candidates, edges, seed.MemoryId, options);
        var second = Rerank(candidates, edges.Reverse().ToArray(), seed.MemoryId, options);

        Assert.Equal(first.Select(item => item.MemoryId), second.Select(item => item.MemoryId));
        Assert.Equal(first.Select(item => item.Score), second.Select(item => item.Score));
        Assert.All(first, item => Assert.True(double.IsFinite(item.Score)));
        Assert.Equal(candidates.Length, first.Count);
    }

    [Fact]
    public void EdgesFromAnotherTenantCannotInfluenceScores()
    {
        var seed = Candidate(1, 1);
        var target = Candidate(2, 0.1);
        var foreignEdge = Edge(seed, target, MemoryGraphEdgeKind.SameEntity) with
        {
            TenantId = Guid.Parse("20000000-0000-0000-0000-000000000002")
        };

        var isolated = Rerank([seed, target], [foreignEdge], seed.MemoryId);
        var baseline = Rerank([seed, target], [], seed.MemoryId);

        Assert.Equal(Score(baseline, target), Score(isolated, target));
        Assert.Equal(baseline.Select(item => item.MemoryId), isolated.Select(item => item.MemoryId));
    }

    [Fact]
    public void DecayAndOptionalBoundedSessionBoostAreAppliedOnlyWhenConfigured()
    {
        var seed = Candidate(1, 1);
        var fresh = Candidate(2, 0.1) with { EntityKeys = ["customer:42"] };
        var old = Candidate(3, 0.1) with { EntityKeys = ["customer:42"] };
        var edges = new[]
        {
            Edge(seed, fresh, MemoryGraphEdgeKind.SameEntity) with { ObservedAt = Now },
            Edge(seed, old, MemoryGraphEdgeKind.SameEntity) with { ObservedAt = Now - TimeSpan.FromDays(60) }
        };
        var withoutBoost = Rerank([seed, fresh, old], edges, seed.MemoryId,
            new(Enabled: true, EdgeDecayHalfLife: TimeSpan.FromDays(30)),
            new(["customer:42", "customer:42"], null));
        var withBoost = Rerank([seed, fresh, old], edges, seed.MemoryId,
            new(Enabled: true, EdgeDecayHalfLife: TimeSpan.FromDays(30),
                SessionBoostEnabled: true, MaximumSessionSignals: 1,
                SessionEntityBoost: 0.05, MaximumSessionBoost: 0.05),
            new(["customer:42", "ignored"], null));

        Assert.True(Score(withoutBoost, fresh) > Score(withoutBoost, old));
        Assert.True(Score(withBoost, fresh) > Score(withoutBoost, fresh));
        Assert.True(Score(withBoost, old) > Score(withoutBoost, old));
    }

    private static IReadOnlyList<MemoryGraphCandidate> Rerank(
        IReadOnlyList<MemoryGraphCandidate> candidates,
        IReadOnlyList<MemoryGraphEdge> edges,
        Guid seed,
        MemoryGraphRerankOptions? options = null,
        MemoryGraphSessionContext? session = null) =>
        new MemoryGraphReranker().Rerank(
            Request(candidates, edges, seed, session),
            options ?? new MemoryGraphRerankOptions(Enabled: true));

    private static MemoryGraphRerankRequest Request(
        IReadOnlyList<MemoryGraphCandidate> candidates,
        IReadOnlyList<MemoryGraphEdge> edges,
        Guid seed,
        MemoryGraphSessionContext? session = null) =>
        new(new(Tenant), candidates, edges, new HashSet<Guid> { seed }, Now, session);

    private static MemoryGraphCandidate Candidate(int id, double score) =>
        new(new Guid(id, 0, 0, new byte[8]), Tenant, score);

    private static MemoryGraphEdge Edge(
        MemoryGraphCandidate from,
        MemoryGraphCandidate to,
        MemoryGraphEdgeKind kind) =>
        new(Tenant, from.MemoryId, to.MemoryId, kind);

    private static double Score(
        IReadOnlyList<MemoryGraphCandidate> result,
        MemoryGraphCandidate candidate) =>
        result.Single(item => item.MemoryId == candidate.MemoryId).Score;

    private static int Index(
        IReadOnlyList<MemoryGraphCandidate> result,
        MemoryGraphCandidate candidate) =>
        result.Select((item, index) => (item, index))
            .Single(pair => pair.item.MemoryId == candidate.MemoryId).index;
}
