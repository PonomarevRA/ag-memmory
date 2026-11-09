using Agm.Memory.Abstractions;
using Xunit;

namespace Agm.Memory.Tests;

public sealed class MemoryStageZeroTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Project = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void StrictAccessPolicy_IsolatesTenantsProjectsAndDisabledSourceKinds()
    {
        var policy = new StrictMemorySourceAccessPolicy([MemorySourceType.Conversation]);
        var sourceScope = new MemoryScope(Tenant, Project);
        var source = new MemorySourceDescriptor(MemorySourceType.Conversation, "chat:42");

        Assert.Equal(MemorySourceAccessOutcome.Allowed,
            policy.Evaluate(new(new MemoryScope(Tenant, Project), sourceScope, source)));
        Assert.Equal(MemorySourceAccessOutcome.TenantMismatch,
            policy.Evaluate(new(new MemoryScope(Guid.NewGuid(), Project), sourceScope, source)));
        Assert.Equal(MemorySourceAccessOutcome.ProjectMismatch,
            policy.Evaluate(new(new MemoryScope(Tenant, Guid.NewGuid()), sourceScope, source)));
        Assert.Equal(MemorySourceAccessOutcome.SourceKindDisabled,
            policy.Evaluate(new(new MemoryScope(Tenant, Project), sourceScope,
                source with { Type = MemorySourceType.Document })));
    }

    [Fact]
    public void FeatureFlags_AreOptInAndIndependent()
    {
        Assert.Equal(new MemoryFeatureFlags(), MemoryFeatureFlags.Disabled);
        Assert.False(MemoryFeatureFlags.Disabled.CanonicalMemory);
        Assert.False(MemoryFeatureFlags.Disabled.GraphReranking);

        var flags = MemoryFeatureFlags.Disabled with { CanonicalMemory = true, QualityCanary = true };
        Assert.True(flags.CanonicalMemory);
        Assert.True(flags.QualityCanary);
        Assert.False(flags.HybridRetrieval);
    }

    [Fact]
    public void TraceId_IsCanonicalRoundTrippableAndRejectsUnsafeValues()
    {
        var created = MemoryTraceId.Create();

        Assert.Equal(MemoryTraceId.Length, created.Value.Length);
        Assert.True(MemoryTraceId.TryParse(created.ToString(), out var parsed));
        Assert.Equal(created, parsed);
        Assert.False(MemoryTraceId.TryParse(created.Value.ToUpperInvariant(), out _));
        Assert.False(MemoryTraceId.TryParse(new string('0', MemoryTraceId.Length), out _));
        Assert.Throws<FormatException>(() => MemoryTraceId.Parse("trace-123"));
    }

    [Fact]
    public void GoldenCorpusEvaluator_ComputesQualityLatencyAndEvidenceGate()
    {
        var corpus = GoldenCorpusFixture.V1;
        var evaluations = corpus.Queries.Select((query, index) => new MemoryQueryEvaluation(
            query.QueryId,
            [new MemoryEvaluationHit(query.RelevantMemoryIds[0], query.ExpectedSourceIds)],
            TimeSpan.FromMilliseconds(20 + index))).ToArray();

        var report = new MemoryGoldenCorpusEvaluator().Evaluate(
            corpus, evaluations, new(P95LatencyBudget: TimeSpan.FromMilliseconds(50)));

        Assert.True(report.Passed);
        Assert.Equal(1, report.Metrics.MeanRecall);
        Assert.Equal(1, report.Metrics.MeanNdcg);
        Assert.Equal(1, report.Metrics.EvidenceCoverage);
        Assert.Equal(1, report.Metrics.ExactIdentifierPassRate);
        Assert.Equal(corpus.Queries.Count, report.Metrics.EvaluatedQueryCount);
    }

    [Fact]
    public void GoldenCorpusEvaluator_ReportsRegressionsAgainstBaseline()
    {
        var corpus = GoldenCorpusFixture.V1;
        var evaluations = corpus.Queries.Select(query => new MemoryQueryEvaluation(
            query.QueryId, [], TimeSpan.FromMilliseconds(500))).ToArray();

        var report = new MemoryGoldenCorpusEvaluator().Evaluate(corpus, evaluations, new());

        Assert.False(report.Passed);
        Assert.Contains("MeanRecallBelowThreshold", report.Failures);
        Assert.Contains("MeanNdcgBelowThreshold", report.Failures);
        Assert.Contains("EvidenceCoverageBelowThreshold", report.Failures);
        Assert.Contains("ExactIdentifierRegression", report.Failures);
        Assert.Contains("P95LatencyBudgetExceeded", report.Failures);
    }

    [Fact]
    public void GoldenCorpusEvaluator_SupportsMoreThanOneHundredQueries()
    {
        var queries = Enumerable.Range(1, 128).Select(index => GoldenCorpusFixture.Query(
            $"bulk-{index}", MemoryGoldenQuerySlice.SemanticParaphrase,
            $"memory-{index}", $"source-{index}")).ToArray();
        var corpus = new MemoryGoldenCorpus("1.0", "bulk-v1", queries);
        var evaluations = queries.Select(query => new MemoryQueryEvaluation(
            query.QueryId,
            [new MemoryEvaluationHit(query.RelevantMemoryIds[0], query.ExpectedSourceIds)],
            TimeSpan.FromMilliseconds(10))).ToArray();

        var report = new MemoryGoldenCorpusEvaluator().Evaluate(corpus, evaluations, new());

        Assert.True(report.Passed);
        Assert.Equal(128, report.Metrics.EvaluatedQueryCount);
    }

    [Fact]
    public void GoldenCorpusEvaluator_RejectsUnknownFixtureSchema()
    {
        var corpus = GoldenCorpusFixture.V1 with { SchemaVersion = "2.0" };

        Assert.Throws<NotSupportedException>(() =>
            new MemoryGoldenCorpusEvaluator().Evaluate(corpus, [], new()));
    }

    [Fact]
    public void GoldenCorpusEvaluator_DoesNotCountDuplicateRelevantHitsTwice()
    {
        var query = GoldenCorpusFixture.Query(
            "duplicates", MemoryGoldenQuerySlice.SemanticParaphrase, "memory-1", "source-1") with
        {
            RelevantMemoryIds = ["memory-1", "memory-2"]
        };
        var corpus = new MemoryGoldenCorpus("1.0", "duplicates-v1", [query]);
        var evaluations = new MemoryQueryEvaluation[]
        {
            new(query.QueryId,
                [
                    new MemoryEvaluationHit("memory-1", ["source-1"]),
                    new MemoryEvaluationHit("memory-1", ["source-1"])
                ],
                TimeSpan.FromMilliseconds(10))
        };

        var report = new MemoryGoldenCorpusEvaluator().Evaluate(
            corpus, evaluations, new(MinimumMeanRecall: 0, MinimumMeanNdcg: 0));

        Assert.Equal(0.5, report.Metrics.MeanRecall);
        Assert.InRange(report.Metrics.MeanNdcg, 0, 1);
    }
}

internal static class GoldenCorpusFixture
{
    public static MemoryGoldenCorpus V1 { get; } = new(
        "1.0",
        "agm-memory-stage-zero-v1",
        [
            Query("exact-id", MemoryGoldenQuerySlice.ExactIdentifier, "memory-exact", "source-exact"),
            Query("semantic", MemoryGoldenQuerySlice.SemanticParaphrase, "memory-semantic", "source-semantic"),
            Query("follow-up", MemoryGoldenQuerySlice.ShortFollowUp, "memory-follow-up", "source-follow-up"),
            Query("freshness", MemoryGoldenQuerySlice.Freshness, "memory-active", "source-active"),
            Query("history", MemoryGoldenQuerySlice.History, "memory-history", "source-history"),
            Query("conflict", MemoryGoldenQuerySlice.Conflict, "memory-conflict", "source-conflict")
        ]);

    public static MemoryGoldenQuery Query(
        string id,
        MemoryGoldenQuerySlice slice,
        string memoryId,
        string sourceId) => new(
            id,
            new MemoryScope(MemoryStageZeroTestsTenant, MemoryStageZeroTestsProject),
            $"Golden query {id}",
            slice,
            [memoryId],
            [sourceId]);

    private static readonly Guid MemoryStageZeroTestsTenant =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MemoryStageZeroTestsProject =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
}
