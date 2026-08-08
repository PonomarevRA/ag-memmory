using Agm.Memory.Abstractions;
using Xunit;

namespace Agm.Memory.Tests;

public sealed class HybridRetrievalTests
{
    private static readonly MemoryRetrievalScope Scope = new("tenant-a", "project-a");

    [Fact]
    public void Fuse_PrioritizesExactIdsThenUsesSemanticAndLexicalRanks()
    {
        var exact = Document("exact");
        var shared = Document("shared");
        var semantic = Document("semantic");
        var results = HybridMemoryRetrieval.Fuse(new(
            Scope,
            [new(shared, 1, 12), new(exact, 20, 1)],
            [new(semantic, 1, .99), new(shared, 2, .95), new(exact, 30, .5)],
            new HashSet<string>(["exact"], StringComparer.Ordinal),
            Limit: 3,
            ReciprocalRankConstant: 60));

        Assert.Equal(["exact", "shared", "semantic"], results.Select(item => item.Document.Id));
        Assert.True(results[0].Explain.ExactIdMatch);
        Assert.Equal(1, results[1].Explain.LexicalRank);
        Assert.Equal(2, results[1].Explain.VectorRank);
        Assert.Equal(1d / 61d, results[1].Explain.LexicalContribution, 12);
        Assert.Equal(1d / 62d, results[1].Explain.VectorContribution, 12);
        Assert.Equal(results[1].Explain.LexicalContribution + results[1].Explain.VectorContribution,
            results[1].Explain.FusedScore, 12);
    }

    [Fact]
    public void Fuse_FiltersSupersededOtherTenantAndOtherProjectBeforeRrf()
    {
        var active = Document("active");
        var results = HybridMemoryRetrieval.Fuse(new(
            Scope,
            [
                new(active, 4, 1),
                new(Document("superseded", state: MemoryRecordState.Superseded), 1, 100),
                new(Document("other-tenant", tenant: "tenant-b"), 1, 100),
                new(Document("other-project", project: "project-b"), 1, 100)
            ],
            [new(active, 3, .8)],
            ExactIds: new HashSet<string>(["other-tenant"], StringComparer.Ordinal)));

        var result = Assert.Single(results);
        Assert.Equal("active", result.Document.Id);
        Assert.Equal(1, result.Rank);
    }

    [Fact]
    public void Fuse_UsesBestDuplicateRankAndStableIdTieBreak()
    {
        var a = Document("a");
        var b = Document("b");
        var results = HybridMemoryRetrieval.Fuse(new(
            Scope,
            [new(b, 2, 1), new(a, 2, 1), new(a, 5, 99), new(a, 1, 2)],
            [],
            Limit: 2));

        Assert.Equal(["a", "b"], results.Select(item => item.Document.Id));
        Assert.Equal(1, results[0].Explain.LexicalRank);
    }

    [Fact]
    public void Evaluation_ComputesRecallNdcgAndMrrAtKWithoutDuplicateInflation()
    {
        var metrics = RetrievalEvaluation.Evaluate(new(
            new HashSet<string>(["a", "b", "c"], StringComparer.Ordinal),
            ["x", "a", "a", "b", "z"]), k: 4);

        Assert.Equal(2d / 3d, metrics.RecallAtK, 12);
        var expectedDcg = 1d / Math.Log2(3) + 1d / Math.Log2(4);
        var idealDcg = 1d + 1d / Math.Log2(3) + 1d / Math.Log2(4);
        Assert.Equal(expectedDcg / idealDcg, metrics.NormalizedDiscountedCumulativeGainAtK, 12);
        Assert.Equal(.5d, metrics.MeanReciprocalRank, 12);
    }

    [Fact]
    public void EvaluationMean_IsDeterministicAndHandlesNoRelevantItems()
    {
        var empty = RetrievalEvaluation.Evaluate(
            new(new HashSet<string>(StringComparer.Ordinal), ["a"]), 3);
        var mean = RetrievalEvaluation.EvaluateMean([
            new(new HashSet<string>(["a"], StringComparer.Ordinal), ["a"]),
            new(new HashSet<string>(["b"], StringComparer.Ordinal), ["x", "b"])
        ], 2);

        Assert.Equal(new RetrievalEvaluationMetrics(0, 0, 0), empty);
        Assert.Equal(1, mean.RecallAtK, 12);
        Assert.Equal((1 + 1 / Math.Log2(3)) / 2, mean.NormalizedDiscountedCumulativeGainAtK, 12);
        Assert.Equal(.75, mean.MeanReciprocalRank, 12);
    }

    private static MemoryRetrievalDocument Document(
        string id,
        string tenant = "tenant-a",
        string project = "project-a",
        MemoryRecordState state = MemoryRecordState.Active) =>
        new(id, tenant, project, state);
}
