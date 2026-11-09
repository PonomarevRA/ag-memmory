using System.Reflection;
using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests;

public sealed class CoreQueryTests
{
    [Fact]
    public async Task Search_UsesOnlyValidatorSelectors_AndFiltersForeignInactiveAndExpiredCandidates()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
        var foreignTenant = TestData.Scope with { TenantId = new ScopeId("tenant-b") };
        var otherRun = TestData.Scope with { RunId = new ScopeId("run-b") };
        var allowed = TestData.Record("allowed");
        fixture.Lexical.Candidates =
        [
            new(allowed, 2, .4, null),
            new(TestData.Record("foreign", foreignTenant), 1, .9, null),
            new(TestData.Record("run", otherRun), 1, .9, null),
            new(TestData.Record("inactive", status: MemoryLifecycleStatus.Invalid), 1, .9, null),
            new(TestData.Record("expired", expiresAt: TestData.Now.AddSeconds(-1)), 1, .9, null),
            new(TestData.Record("superseded", status: MemoryLifecycleStatus.Superseded), 1, .9, null)
        ];
        fixture.Vector.Candidates = [new(allowed, 1, .8, null)];
        fixture.Graph.Results = [new(new MemoryId("foreign"), 100d), new(allowed.Id, .1d)];

        var result = await fixture.Service.SearchAsync(Request(), default);

        Assert.Equal([allowed.Id], result.Hits.Select(hit => hit.Record.Id));
        Assert.Equal(1, fixture.Lexical.Calls);
        Assert.Equal(1, fixture.Vector.Calls);
        Assert.True(fixture.Lexical.LastRequest!.Eligibility.AuthorizedScopes.Contains(TestData.Scope));
        Assert.False(fixture.Lexical.LastRequest.Eligibility.AuthorizedScopes.Contains(foreignTenant));
        Assert.True(fixture.Lexical.LastRequest.Eligibility.ExcludesExpiredRecords);
        Assert.Equal(MemoryLifecycleStatus.Active, fixture.Lexical.LastRequest.Eligibility.RequiredLifecycleStatus);
        Assert.Equal([allowed.Id], fixture.Graph.LastRequest!.Candidates.Select(candidate => candidate.Id));
    }

    [Fact]
    public async Task Search_DenialDoesNotCallRetrievalPorts()
    {
        var fixture = Fixture();
        fixture.Authorization.Deny(TestData.Actor, MemoryOperation.Search);

        var result = await fixture.Service.SearchAsync(Request(), default);

        Assert.Equal(MemoryErrorCode.Unauthorized, result.Error!.Code);
        Assert.Equal(0, fixture.Lexical.Calls);
        Assert.Equal(0, fixture.Vector.Calls);
        Assert.Equal(0, fixture.Graph.Calls);
    }

    [Fact]
    public void Rrf_UsesBestPositiveRankHasStableIdTieBreakAndPreservesContributions()
    {
        var first = TestData.Record("a");
        var second = TestData.Record("b");
        var scopes = new AuthorizedScopeSet([new ScopeSelector(TestData.Scope)]);
        var hits = DeterministicRetrieval.Fuse(
            [new(first, 2, .1, null), new(first, 1, .2, null), new(second, 1, .3, null)],
            [new(first, 1, .4, null), new(second, 2, .5, null)], scopes, TestData.Now, null, null, 10, 60,
            TestData.RetrievalVersion, new("reranker-v1"));

        Assert.Equal([first.Id, second.Id], hits.Select(hit => hit.Record.Id));
        Assert.Equal(1, hits[0].Contribution.LexicalRank);
        Assert.Equal(1, hits[0].Contribution.VectorRank);
        Assert.Equal(1d / 61d, hits[0].Contribution.LexicalContribution);
        Assert.Equal(2, hits[1].Contribution.VectorRank);
    }

    [Fact]
    public async Task Search_ForwardsCompatibleVectorAndFusesLexicalAndVectorRanks()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
        var contract = new EmbeddingContract("test", "small", "v1", 3, "l2", TestData.ContractVersion);
        var queryEmbedding = new EmbeddingReference("test", "small", "v1", 3, "l2", "query-content");
        fixture.EmbeddingPolicy.Result = new(contract, "embedding-v1");
        var first = TestData.Record("a");
        var second = TestData.Record("b");
        fixture.Lexical.Candidates = [new(first, 1, .9, null), new(second, 2, .8, null)];
        fixture.Vector.Candidates = [new(second, 1, .7, null), new(first, 2, .6, null)];

        var result = await fixture.Service.SearchAsync(Request(new float[] { 1f, 0f, 0f }, queryEmbedding), default);

        Assert.Null(result.Error);
        Assert.Equal([first.Id, second.Id], result.Hits.Select(hit => hit.Record.Id));
        Assert.Equal(1, result.Hits[0].Contribution.LexicalRank);
        Assert.Equal(2, result.Hits[0].Contribution.VectorRank);
        Assert.Equal(new float[] { 1f, 0f, 0f }, fixture.Vector.LastRequest!.QueryVector!.Value.ToArray());
        Assert.Equal(queryEmbedding, fixture.Vector.LastRequest.QueryEmbedding);
        Assert.Equal(contract, fixture.Vector.LastRequest.EmbeddingContract);
        Assert.Equal(RetrievalSourceStatus.Completed, result.Execution!.Vector);
    }

    [Fact]
    public async Task Search_WithoutVectorStillInvokesBothPortsAndUsesLexicalResult()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
        var lexical = TestData.Record("lexical");
        fixture.Lexical.Candidates = [new(lexical, 1, .9, null)];
        fixture.Vector.ReturnEmptyWhenQueryVectorMissing = true;

        var result = await fixture.Service.SearchAsync(Request(), default);

        Assert.Null(result.Error);
        Assert.Equal([lexical.Id], result.Hits.Select(hit => hit.Record.Id));
        Assert.Equal(1, fixture.Lexical.Calls);
        Assert.Equal(1, fixture.Vector.Calls);
        Assert.Null(fixture.Vector.LastRequest!.QueryVector);
        Assert.Equal(RetrievalSourceStatus.NotRequested, result.Execution!.Vector);
    }

    [Fact]
    public async Task Search_VectorFailureLeavesEligibleLexicalResultUsable()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
        var contract = new EmbeddingContract("test", "small", "v1", 3, "l2", TestData.ContractVersion);
        var queryEmbedding = new EmbeddingReference("test", "small", "v1", 3, "l2", "query-content");
        fixture.EmbeddingPolicy.Result = new(contract, "embedding-v1");
        var lexical = TestData.Record("lexical");
        fixture.Lexical.Candidates = [new(lexical, 1, .9, null)];
        fixture.Vector.Failure = new InvalidOperationException("test-only vector failure");

        var result = await fixture.Service.SearchAsync(Request(new float[] { 1f, 0f, 0f }, queryEmbedding), default);

        Assert.Null(result.Error);
        Assert.Equal([lexical.Id], result.Hits.Select(hit => hit.Record.Id));
        Assert.Equal(RetrievalSourceStatus.Unavailable, result.Execution!.Vector);
        Assert.Equal(1, fixture.Lexical.Calls);
        Assert.Equal(1, fixture.Vector.Calls);
    }

    [Fact]
    public async Task Search_GraphFailureDoesNotDiscardFusedHits()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
        var lexical = TestData.Record("lexical");
        fixture.Lexical.Candidates = [new(lexical, 1, .9, null)];
        fixture.Graph.Failure = new InvalidOperationException("test-only graph failure");

        var result = await fixture.Service.SearchAsync(Request(), default);

        Assert.Null(result.Error);
        Assert.Equal([lexical.Id], result.Hits.Select(hit => hit.Record.Id));
        Assert.Equal(RetrievalSourceStatus.Unavailable, result.Execution!.Graph);
        Assert.Equal(0d, result.Hits[0].Contribution.GraphContribution);
    }

    [Fact]
    public async Task Search_AllowsAnAbsentOptionalGraph()
    {
        var store = new InMemoryStore();
        var authorization = new FixedAuthorization();
        authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
        var lexical = new TestLexicalSearch
        {
            Candidates = [new(TestData.Record("lexical"), 1, .9, null)]
        };
        var service = new MemoryQueryService(store, authorization, lexical, new TestVectorSearch(), null,
            new TestEmbeddingPolicy(), new TestClock(TestData.Now), TestData.Options);

        var result = await service.SearchAsync(Request(), default);

        Assert.Null(result.Error);
        Assert.Equal(RetrievalSourceStatus.NotRequested, result.Execution!.Graph);
    }

    [Fact]
    public void Rrf_CollapsesSameDeduplicationKeyAndExcludesSupersededRecords()
    {
        var first = TestData.Record("a") with { DeduplicationKey = "same-memory" };
        var duplicate = TestData.Record("b") with { DeduplicationKey = "same-memory" };
        var superseded = TestData.Record("c", status: MemoryLifecycleStatus.Superseded);
        var scopes = new AuthorizedScopeSet([new ScopeSelector(TestData.Scope)]);

        var hits = DeterministicRetrieval.Fuse(
            [new(first, 1, .9, null), new(duplicate, 2, .8, null), new(superseded, 1, .99, null)],
            [], scopes, TestData.Now, null, null, 10, 60, TestData.RetrievalVersion, new("reranker-v1"));

        var hit = Assert.Single(hits);
        Assert.Equal(first.Id, hit.Record.Id);
        Assert.Equal(1, hit.Contribution.LexicalRank);
    }

    [Fact]
    public async Task Context_IsCitedDeterministicBudgeted_AndCanUseOneEligibleSummaryFallback()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.BuildContext, TestData.Scope);
        var summary = TestData.Record("summary", text: "small approved summary", type: MemoryRecordType.Summary, cost: 4);
        fixture.Store.Seed(summary);

        var result = await fixture.Service.BuildContextAsync(new(
            TestData.Actor, TestData.Scope, "query", null, null, 10, 4, true,
            TestData.RetrievalVersion, TestData.ContextVersion, TestData.ContractVersion, AllowSingleSummaryFallback: true), default);

        Assert.Equal(4, result.EstimatedTokenCost);
        Assert.Single(result.Citations);
        Assert.Equal(summary.Id, result.Citations[0].MemoryId);
        Assert.Contains(summary.CanonicalText, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_PrioritizesHotMemory_DeduplicatesAndFitsExactlyByEstimatedCost()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.BuildContext, TestData.Scope);
        fixture.Store.Seed(new SessionHotMemory(new("hot"), TestData.Scope, "hot", TestData.Provenance("hot-evidence"), 1,
            TestData.Now, TestData.Now, TestData.Now.AddMinutes(5)));
        var duplicate = TestData.Record("duplicate", text: "hot", cost: 1) with { DeduplicationKey = "durable-hot" };
        var tooLarge = TestData.Record("too-large", text: "skip", cost: 5);
        var durable = TestData.Record("durable", text: "durable", cost: 3);
        fixture.Lexical.Candidates = [new(duplicate, 1, .9, null), new(tooLarge, 2, .8, null), new(durable, 3, .7, null)];

        var result = await fixture.Service.BuildContextAsync(new(
            TestData.Actor, TestData.Scope, "query", null, null, 10, 4, true,
            TestData.RetrievalVersion, TestData.ContextVersion, TestData.ContractVersion), default);

        Assert.Null(result.Error);
        Assert.Equal(4, result.EstimatedTokenCost);
        Assert.Equal(1, result.OmittedCount);
        Assert.Equal("[hot] hot\n[durable] durable", result.Content);
        Assert.Equal([new MemoryId("hot"), durable.Id], result.Citations.Select(citation => citation.MemoryId));
        Assert.Equal(["hot-evidence"], result.Citations[0].EvidenceIds);
        Assert.Equal(["evidence-durable"], result.Citations[1].EvidenceIds);
    }

    [Fact]
    public async Task Context_ForwardsQueryVectorAndCanSuppressCitationPayload()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.BuildContext, TestData.Scope);
        var contract = new EmbeddingContract("test", "small", "v1", 3, "l2", TestData.ContractVersion);
        var queryEmbedding = new EmbeddingReference("test", "small", "v1", 3, "l2", "query-content");
        fixture.EmbeddingPolicy.Result = new(contract, "embedding-v1");
        var durable = TestData.Record("durable", text: "durable", cost: 2);
        fixture.Lexical.Candidates = [new(durable, 1, .9, null)];

        var result = await fixture.Service.BuildContextAsync(new(
            TestData.Actor, TestData.Scope, "query", queryEmbedding, null, 10, 2, false,
            TestData.RetrievalVersion, TestData.ContextVersion, TestData.ContractVersion, QueryVector: new float[] { 1f, 0f, 0f }), default);

        Assert.Null(result.Error);
        Assert.Empty(result.Citations);
        Assert.Equal(new float[] { 1f, 0f, 0f }, fixture.Vector.LastRequest!.QueryVector!.Value.ToArray());
        Assert.Equal(queryEmbedding, fixture.Vector.LastRequest.QueryEmbedding);
    }

    [Fact]
    public async Task Context_FallbackRejectsForeignInactiveExpiredAndWrongTypeRecords()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.BuildContext, TestData.Scope);
        var foreign = TestData.Scope with { TenantId = new ScopeId("tenant-b") };
        fixture.Store.Seed(TestData.Record("foreign", foreign, text: "foreign summary", type: MemoryRecordType.Summary));
        fixture.Store.Seed(TestData.Record("inactive", text: "inactive summary", type: MemoryRecordType.Summary,
            status: MemoryLifecycleStatus.Invalid));
        fixture.Store.Seed(TestData.Record("expired", text: "expired summary", type: MemoryRecordType.Summary,
            expiresAt: TestData.Now));
        fixture.Store.Seed(TestData.Record("fact", text: "not a summary"));
        var eligible = TestData.Record("eligible", text: "eligible summary", type: MemoryRecordType.Summary, cost: 3);
        fixture.Store.Seed(eligible);

        var result = await fixture.Service.BuildContextAsync(new(
            TestData.Actor, TestData.Scope, "query", null, null, 10, 3, true,
            TestData.RetrievalVersion, TestData.ContextVersion, TestData.ContractVersion, AllowSingleSummaryFallback: true), default);

        Assert.Null(result.Error);
        Assert.Equal("[eligible] eligible summary", result.Content);
        Assert.Equal([eligible.Id], result.Citations.Select(citation => citation.MemoryId));
    }

    [Fact]
    public async Task ReadHotMemory_RejectsExpiredValueAndNeverWidensScope()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.ReadHotMemory, TestData.Scope);
        fixture.Store.Seed(new SessionHotMemory(new("hot"), TestData.Scope, "expired", TestData.Provenance(), 1,
            TestData.Now, TestData.Now, TestData.Now));

        var result = await fixture.Service.ReadHotMemoryAsync(new(TestData.Actor, TestData.Scope, TestData.ContractVersion), default);

        Assert.Null(result);
    }

    [Fact]
    public void PublicApiAndReferences_DoNotExposeForbiddenReverseDependencies()
    {
        var forbidden = new[] { "Lance", "Arrow", "Sqlite", "Microsoft.AspNetCore", "Agm.", "IServiceProvider" };
        var assemblies = new[] { typeof(MemoryScope).Assembly, typeof(MemoryQueryService).Assembly };

        foreach (var assembly in assemblies)
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                forbidden.Any(token => reference.Name?.Contains(token, StringComparison.OrdinalIgnoreCase) == true));
            foreach (var type in assembly.GetExportedTypes())
            {
                Assert.False(IsForbidden(type, forbidden), $"Forbidden public type: {type.FullName}");
                foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                {
                    var memberTypes = member switch
                    {
                        PropertyInfo property => [property.PropertyType],
                        FieldInfo field => [field.FieldType],
                        MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
                            .Append(method.ReturnType).ToArray(),
                        _ => []
                    };
                    Assert.DoesNotContain(memberTypes, candidate => IsForbidden(candidate, forbidden));
                }
            }
        }
    }

    private static bool IsForbidden(Type type, IEnumerable<string> forbidden)
    {
        if (forbidden.Any(token => (type.FullName ?? type.Name).Contains(token, StringComparison.OrdinalIgnoreCase)))
            return true;
        return type.IsGenericType && type.GetGenericArguments().Any(argument => IsForbidden(argument, forbidden));
    }

    private static MemorySearchRequest Request(
        ReadOnlyMemory<float>? queryVector = null,
        EmbeddingReference? queryEmbedding = null) => new(
        TestData.Actor, TestData.Scope, "query", queryEmbedding, null, null, 10, TestData.RetrievalVersion,
        TestData.ContractVersion, queryVector);

    private static QueryFixture Fixture()
    {
        var store = new InMemoryStore();
        var authorization = new FixedAuthorization();
        var lexical = new TestLexicalSearch();
        var vector = new TestVectorSearch();
        var graph = new TestGraph();
        var embeddingPolicy = new TestEmbeddingPolicy();
        var service = new MemoryQueryService(store, authorization, lexical, vector, graph, embeddingPolicy,
            new TestClock(TestData.Now), TestData.Options);
        return new(service, store, authorization, lexical, vector, graph, embeddingPolicy);
    }

    private sealed record QueryFixture(
        MemoryQueryService Service,
        InMemoryStore Store,
        FixedAuthorization Authorization,
        TestLexicalSearch Lexical,
        TestVectorSearch Vector,
        TestGraph Graph,
        TestEmbeddingPolicy EmbeddingPolicy);
}
