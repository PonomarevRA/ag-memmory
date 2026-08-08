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
            new(TestData.Record("expired", expiresAt: TestData.Now.AddSeconds(-1)), 1, .9, null)
        ];
        fixture.Vector.Candidates = [new(allowed, 1, .8, null)];

        var result = await fixture.Service.SearchAsync(Request(), default);

        Assert.Equal([allowed.Id], result.Hits.Select(hit => hit.Record.Id));
        Assert.Equal(1, fixture.Lexical.Calls);
        Assert.Equal(1, fixture.Vector.Calls);
        Assert.True(fixture.Lexical.LastRequest!.Eligibility.AuthorizedScopes.Contains(TestData.Scope));
        Assert.False(fixture.Lexical.LastRequest.Eligibility.AuthorizedScopes.Contains(foreignTenant));
        Assert.True(fixture.Lexical.LastRequest.Eligibility.ExcludesExpiredRecords);
        Assert.Equal(MemoryLifecycleStatus.Active, fixture.Lexical.LastRequest.Eligibility.RequiredLifecycleStatus);
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

    private static MemorySearchRequest Request() => new(
        TestData.Actor, TestData.Scope, "query", null, null, null, 10, TestData.RetrievalVersion, TestData.ContractVersion);

    private static QueryFixture Fixture()
    {
        var store = new InMemoryStore();
        var authorization = new FixedAuthorization();
        var lexical = new TestLexicalSearch();
        var vector = new TestVectorSearch();
        var graph = new TestGraph();
        var service = new MemoryQueryService(store, authorization, lexical, vector, graph, new TestEmbeddingPolicy(),
            new TestClock(TestData.Now), TestData.Options);
        return new(service, store, authorization, lexical, vector, graph);
    }

    private sealed record QueryFixture(
        MemoryQueryService Service,
        InMemoryStore Store,
        FixedAuthorization Authorization,
        TestLexicalSearch Lexical,
        TestVectorSearch Vector,
        TestGraph Graph);
}
