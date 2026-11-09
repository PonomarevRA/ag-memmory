using Agm.Memory.Abstractions;
using Xunit;

namespace Agm.Memory.Tests;

public sealed class MemoryContextBuilderTests
{
    private static readonly Guid Tenant = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid Project = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly MemoryScope Scope = new(Tenant, Project);

    [Fact]
    public void CurrentQueryStronglyPrefersActiveMemory()
    {
        var superseded = Candidate(1, CanonicalMemoryStatus.Superseded, "old");
        var active = Candidate(2, CanonicalMemoryStatus.Active, "current");

        var result = Rerank([superseded, active]);

        Assert.Equal(active.MemoryId, result[0].Candidate.MemoryId);
        Assert.True(result[0].Score.Current > result[1].Score.Current * 10);
    }

    [Fact]
    public void HistoryModeAllowsVersionsAndOrdersReasonDecisionImplementationChain()
    {
        var reason = Candidate(1, CanonicalMemoryStatus.Superseded, "Latency exceeded the target",
            CanonicalMemoryType.Incident, group: "migration");
        var decision = Candidate(2, CanonicalMemoryStatus.Superseded, "Use the cache",
            CanonicalMemoryType.Decision, group: "migration");
        var implementation = Candidate(3, CanonicalMemoryStatus.Active, "Cache deployed",
            CanonicalMemoryType.Event, group: "migration");
        var ranked = Rerank([implementation, decision, reason], MemoryQueryMode.History);

        var result = Build(ranked,
            [new(reason.MemoryId, decision.MemoryId, MemoryContextRelationKind.ReasonToDecision),
             new(decision.MemoryId, implementation.MemoryId, MemoryContextRelationKind.DecisionToImplementation)],
            MemoryQueryMode.History);

        Assert.Equal([reason.MemoryId, decision.MemoryId, implementation.MemoryId], result.MemoryIds);
        Assert.Contains("Latency exceeded", result.Content);
        Assert.Contains("Use the cache", result.Content);
        Assert.Contains("Cache deployed", result.Content);
    }

    [Fact]
    public void BuilderGroupsAndDeduplicatesEquivalentMemories()
    {
        var first = Candidate(1, CanonicalMemoryStatus.Active, "Use PostgreSQL", group: "storage");
        var duplicate = Candidate(2, CanonicalMemoryStatus.Active, "  Use   PostgreSQL ", group: "STORAGE");
        var second = Candidate(3, CanonicalMemoryStatus.Active, "Keep migrations reversible", group: "storage");
        var third = Candidate(4, CanonicalMemoryStatus.Active, "Capture rollback evidence", group: "release");

        var result = Build(Rerank([first, duplicate, second, third]));

        Assert.Equal(3, result.MemoryIds.Count);
        Assert.Contains("[storage]", result.Content);
        Assert.Contains("[release]", result.Content);
        Assert.True(result.MemoryIds.Contains(first.MemoryId) ^ result.MemoryIds.Contains(duplicate.MemoryId));
    }

    [Fact]
    public void BuilderNeverExceedsStrictTokenBudget()
    {
        var ranked = Rerank(Enumerable.Range(1, 8)
            .Select(index => Candidate(index, CanonicalMemoryStatus.Active,
                new string((char)('a' + index), 500), group: "budget"))
            .ToArray());

        var result = Build(ranked, options: new(TokenBudget: 40));

        Assert.False(result.UsedLowConfidenceFallback);
        Assert.InRange(result.MemoryIds.Count, 3, 8);
        Assert.True(result.EstimatedTokens <= 40);
        Assert.True(result.Content.Length <= 160);
    }

    [Fact]
    public void CitationTagsMapEveryRenderedMemoryToProvenance()
    {
        var candidates = Enumerable.Range(1, 3).Select(index => Candidate(
            index, CanonicalMemoryStatus.Active, $"fact {index}", provenance:
            [new(new Guid(index + 20, 0, 0, new byte[8]), $"doc:{index}")])).ToArray();

        var result = Build(Rerank(candidates));

        Assert.Equal(["M1", "M2", "M3"], result.Citations.Select(item => item.Tag));
        Assert.All(result.Citations, citation =>
        {
            Assert.Contains($"[{citation.Tag}]", result.Content);
            Assert.Single(citation.Provenance);
            Assert.NotNull(citation.Provenance[0].ExternalReference);
        });
    }

    [Fact]
    public void LowConfidenceFallsBackWithoutInventingMemoryOrCitation()
    {
        var candidates = Enumerable.Range(1, 3)
            .Select(index => Candidate(index, CanonicalMemoryStatus.Active, $"unsupported fact {index}") with
            { Confidence = 0.1 })
            .ToArray();

        var result = Build(Rerank(candidates));

        Assert.True(result.UsedLowConfidenceFallback);
        Assert.Empty(result.MemoryIds);
        Assert.Empty(result.Citations);
        Assert.DoesNotContain("unsupported fact", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No sufficiently supported memory", result.Content);
    }

    [Fact]
    public void TenantAndProjectScopeMismatchFailsClosed()
    {
        var foreign = Candidate(1, CanonicalMemoryStatus.Active, "foreign") with
        { Scope = new MemoryScope(Guid.NewGuid(), Project) };

        Assert.Throws<ArgumentException>(() => Rerank([foreign]));
        var ranked = new[] { Ranked(foreign, 1) };
        Assert.Throws<ArgumentException>(() => Build(ranked));
    }

    [Fact]
    public void DisabledRerankerPreservesCandidateIdentityAndOrder()
    {
        IReadOnlyList<MemoryContextCandidate> candidates =
            [Candidate(1, CanonicalMemoryStatus.Superseded, "first"),
             Candidate(2, CanonicalMemoryStatus.Active, "second")];

        var result = new DeterministicMemoryContextReranker().Rerank(
            new(Scope, candidates), new());

        Assert.Equal(candidates, result.Select(item => item.Candidate));
        Assert.Equal([1, 2], result.Select(item => item.Rank));
        Assert.Same(candidates[0], result[0].Candidate);
        Assert.Same(candidates[1], result[1].Candidate);
    }

    private static IReadOnlyList<RankedMemoryContextCandidate> Rerank(
        IReadOnlyList<MemoryContextCandidate> candidates,
        MemoryQueryMode mode = MemoryQueryMode.Current) =>
        new DeterministicMemoryContextReranker().Rerank(
            new(Scope, candidates, mode), new(Enabled: true));

    private static MemoryContextResult Build(
        IReadOnlyList<RankedMemoryContextCandidate> candidates,
        IReadOnlyList<MemoryContextRelation>? relations = null,
        MemoryQueryMode mode = MemoryQueryMode.Current,
        MemoryContextBuildOptions? options = null) =>
        new MemoryContextBuilder().Build(
            new(Scope, candidates, relations, mode), options ?? new());

    private static MemoryContextCandidate Candidate(
        int id,
        CanonicalMemoryStatus status,
        string content,
        CanonicalMemoryType type = CanonicalMemoryType.Fact,
        string? group = null,
        IReadOnlyList<MemoryContextProvenance>? provenance = null) =>
        new(new Guid(id, 0, 0, new byte[8]), Scope, type, status,
            $"subject-{id}", content, 1, 0.7, 0.6, 0.8, 0.9,
            GroupKey: group, Provenance: provenance);

    private static RankedMemoryContextCandidate Ranked(MemoryContextCandidate candidate, int rank) =>
        new(candidate, rank, new(0.8, 0.7, 0.6, candidate.Confidence, 1, 0.8));
}
