using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests.Graph;

public sealed class MemoryGraphQueryServiceTests
{
    [Fact]
    public async Task Read_DeniedBeforeSourceInvocation_UsesGraphRead()
    {
        var authorization = new FixedAuthorization();
        authorization.Deny(TestData.Actor, MemoryOperation.GraphRead);
        var source = new TestGraphSource();
        var service = Service(source, authorization);

        var result = await service.ReadAsync(Request(), default);

        Assert.Equal(MemoryErrorCode.Unauthorized, result.Error!.Code);
        Assert.Empty(result.Nodes);
        Assert.Empty(result.Edges);
        Assert.Equal(1, authorization.Calls);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Read_UsesValidatorIssuedExactScopeAndServerClockBeforeDerivation()
    {
        var authorization = new FixedAuthorization();
        var otherRun = TestData.Scope with { RunId = new ScopeId("run-b") };
        authorization.Allow(TestData.Actor, MemoryOperation.GraphRead, TestData.Scope, otherRun);
        var source = new TestGraphSource
        {
            Records =
            [
                Record("allowed", TestData.Scope, entities: ["shared"]),
                Record("foreign", otherRun, entities: ["shared"]),
                Record("inactive", TestData.Scope, MemoryLifecycleStatus.Invalid, ["shared"]),
                Record("expired", TestData.Scope, expiresAt: TestData.Now, entities: ["shared"])
            ]
        };
        var service = Service(source, authorization);

        var result = await service.ReadAsync(Request(sourceLimit: 201, visibleNodeLimit: 76, edgeLimit: 151), default);

        Assert.Null(result.Error);
        Assert.Equal([new MemoryId("allowed")], result.Nodes.Select(node => node.MemoryId));
        Assert.Empty(result.Edges);
        Assert.Equal(1, source.Calls);
        Assert.NotNull(source.LastRequest);
        Assert.Equal(MemoryGraphLimits.MaximumSourceRecords, source.LastRequest!.Limit);
        Assert.Equal(TestData.Now, source.LastRequest.Eligibility.AsOfUtc);
        Assert.True(source.LastRequest.Eligibility.AuthorizedScopes.Contains(TestData.Scope));
        Assert.False(source.LastRequest.Eligibility.AuthorizedScopes.Contains(otherRun));
    }

    [Fact]
    public async Task Read_DerivesNormalizedUnorderedSharedEntityEdgesDeterministically()
    {
        var authorization = new FixedAuthorization();
        authorization.Allow(TestData.Actor, MemoryOperation.GraphRead, TestData.Scope);
        var source = new TestGraphSource
        {
            Records =
            [
                Record("b", TestData.Scope, entities: ["risk", "  Team\tPhoenix  ", "RISK"]),
                Record("a", TestData.Scope, entities: ["TEAM PHOENIX", "Risk"]),
                Record("c", TestData.Scope, entities: ["team phoenix"])
            ]
        };
        var service = Service(source, authorization);

        var result = await service.ReadAsync(Request(), default);

        Assert.Null(result.Error);
        Assert.Equal([new MemoryId("a"), new MemoryId("b"), new MemoryId("c")], result.Nodes.Select(node => node.MemoryId));
        Assert.Collection(result.Edges,
            edge =>
            {
                Assert.Equal(new MemoryId("a"), edge.FirstMemoryId);
                Assert.Equal(new MemoryId("b"), edge.SecondMemoryId);
                Assert.Equal(2, edge.Weight);
                Assert.Equal(MemoryGraphEdgeKind.SharedEntity, edge.Kind);
            },
            edge =>
            {
                Assert.Equal(new MemoryId("a"), edge.FirstMemoryId);
                Assert.Equal(new MemoryId("c"), edge.SecondMemoryId);
                Assert.Equal(1, edge.Weight);
            },
            edge =>
            {
                Assert.Equal(new MemoryId("b"), edge.FirstMemoryId);
                Assert.Equal(new MemoryId("c"), edge.SecondMemoryId);
                Assert.Equal(1, edge.Weight);
            });
        Assert.All(result.Nodes, node => Assert.Equal(2, node.Degree));
        Assert.All(result.Nodes, node => Assert.Equal(4, node.ImportanceBand));
        Assert.All(result.Nodes, node => Assert.Equal(4, node.ConfidenceBand));
    }

    [Fact]
    public async Task Read_ClampsSourceNodeAndEdgeLimits()
    {
        var authorization = new FixedAuthorization();
        authorization.Allow(TestData.Actor, MemoryOperation.GraphRead, TestData.Scope);
        var source = new TestGraphSource
        {
            Records = Enumerable.Range(0, MemoryGraphLimits.MaximumSourceRecords)
                .Select(index => Record($"memory-{index:D3}", TestData.Scope, entities: ["shared"]))
                .ToArray()
        };
        var service = Service(source, authorization);

        var result = await service.ReadAsync(Request(int.MaxValue, int.MaxValue, int.MaxValue), default);

        Assert.Null(result.Error);
        Assert.Equal(MemoryGraphLimits.MaximumSourceRecords, source.LastRequest!.Limit);
        Assert.Equal(MemoryGraphLimits.MaximumVisibleNodes, result.Nodes.Count);
        Assert.Equal(MemoryGraphLimits.MaximumEdges, result.Edges.Count);
        Assert.All(result.Edges, edge => Assert.Equal(MemoryGraphEdgeKind.SharedEntity, edge.Kind));
    }

    [Fact]
    public async Task Read_InvalidLimitsOrSourceFaultReturnsSafeErrorWithoutGraphData()
    {
        var authorization = new FixedAuthorization();
        authorization.Allow(TestData.Actor, MemoryOperation.GraphRead, TestData.Scope);
        var source = new TestGraphSource();
        var service = Service(source, authorization);

        var invalid = await service.ReadAsync(Request(sourceLimit: 0), default);

        Assert.Equal(MemoryErrorCode.InvalidArgument, invalid.Error!.Code);
        Assert.Empty(invalid.Nodes);
        Assert.Empty(invalid.Edges);
        Assert.Equal(0, source.Calls);

        source.Failure = new InvalidOperationException("test-only source fault");
        var faulted = await service.ReadAsync(Request(), default);

        Assert.Equal(MemoryErrorCode.DependencyFailure, faulted.Error!.Code);
        Assert.Null(faulted.Error.Field);
        Assert.Empty(faulted.Nodes);
        Assert.Empty(faulted.Edges);
    }

    private static MemoryGraphQueryService Service(TestGraphSource source, FixedAuthorization authorization) => new(
        source, authorization, new TestClock(TestData.Now), TestData.ContractVersion);

    private static MemoryGraphRequest Request(
        int sourceLimit = 10,
        int visibleNodeLimit = 10,
        int edgeLimit = 10) => new(
        TestData.Actor,
        TestData.Scope,
        sourceLimit,
        visibleNodeLimit,
        edgeLimit,
        TestData.ContractVersion);

    private static MemoryGraphSourceRecord Record(
        string id,
        MemoryScope scope,
        MemoryLifecycleStatus status = MemoryLifecycleStatus.Active,
        IReadOnlyList<string>? entities = null,
        DateTimeOffset? expiresAt = null) => new(
        new MemoryId(id), scope, MemoryRecordType.Fact, status, .7, .7, entities ?? ["entity"], expiresAt);

    private sealed class TestGraphSource : IMemoryGraphSource
    {
        public IReadOnlyList<MemoryGraphSourceRecord> Records { get; init; } = [];
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public MemoryGraphSourceRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<MemoryGraphSourceRecord>> ReadAsync(
            MemoryGraphSourceRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            if (Failure is not null)
                return Task.FromException<IReadOnlyList<MemoryGraphSourceRecord>>(Failure);
            return Task.FromResult(Records);
        }
    }
}
