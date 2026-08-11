using System.Reflection;
using AgMemory.Contracts;
using Xunit;

namespace AgMemory.Contracts.Tests;

public sealed class MemoryGraphContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GraphRead_UsesDedicatedRequestAndBoundedSourceContracts()
    {
        Assert.Contains(MemoryOperation.GraphRead, Enum.GetValues<MemoryOperation>());
        AssertParameter(typeof(IMemoryGraphQueryService), nameof(IMemoryGraphQueryService.ReadAsync), typeof(MemoryGraphRequest));
        AssertParameter(typeof(IMemoryGraphSource), nameof(IMemoryGraphSource.ReadAsync), typeof(MemoryGraphSourceRequest));

        Assert.Equal(200, MemoryGraphLimits.MaximumSourceRecords);
        Assert.Equal(25, MemoryGraphLimits.NodesPerPortion);
        Assert.Equal(150, MemoryGraphLimits.MaximumVisibleNodes);
        Assert.Equal(150, MemoryGraphLimits.MaximumEdges);
        Assert.Equal([MemoryGraphEdgeKind.SharedEntity], Enum.GetValues<MemoryGraphEdgeKind>());
    }

    [Fact]
    public void GraphSourceRequest_RequiresExactActiveAndNonExpiredRecords()
    {
        var scope = new MemoryScope(new("tenant-a"), new("project-a"), new("workspace-a"), new("chat-a"), new("run-a"));
        var otherRun = scope with { RunId = new ScopeId("run-b") };
        var request = new MemoryGraphSourceRequest(
            new(new AuthorizedScopeSet([new ScopeSelector(scope)]), null, Now),
            MemoryGraphLimits.MaximumSourceRecords);

        Assert.True(request.IsEligible(Source("active", scope)));
        Assert.False(request.IsEligible(Source("foreign", otherRun)));
        Assert.False(request.IsEligible(Source("inactive", scope, MemoryLifecycleStatus.Invalid)));
        Assert.False(request.IsEligible(Source("expired", scope, expiresAt: Now)));
    }

    [Fact]
    public void BrowserGraphTypes_ExposeOnlyOpaqueIdsAndDisplayBands()
    {
        var nodeProperties = typeof(MemoryGraphNode).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var edgeProperties = typeof(MemoryGraphEdge).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.DoesNotContain(nodeProperties, property => property.PropertyType == typeof(MemoryRecord) || property.PropertyType == typeof(MemoryScope));
        Assert.DoesNotContain(edgeProperties, property => property.PropertyType == typeof(MemoryRecord) || property.PropertyType == typeof(MemoryScope));
        Assert.Contains(nodeProperties, property => property.Name == nameof(MemoryGraphNode.MemoryId) && property.PropertyType == typeof(MemoryId));
        Assert.Contains(nodeProperties, property => property.Name == nameof(MemoryGraphNode.ImportanceBand) && property.PropertyType == typeof(int));
        Assert.Contains(nodeProperties, property => property.Name == nameof(MemoryGraphNode.ConfidenceBand) && property.PropertyType == typeof(int));
    }

    [Fact]
    public void GraphPortions_UseOpaqueLineageKeysWithoutBrowserSelectedLimits()
    {
        AssertParameter(typeof(IMemoryGraphPortionQueryService), nameof(IMemoryGraphPortionQueryService.ReadPortionAsync), typeof(MemoryGraphPortionRequest));
        Assert.DoesNotContain(typeof(MemoryGraphPortionRequest).GetProperties(), property =>
            property.Name.Contains("Limit", StringComparison.Ordinal) || property.Name.Contains("PageSize", StringComparison.Ordinal));

        var browserTypes = new[] { typeof(MemoryGraphBrowserNode), typeof(MemoryGraphBrowserEdge), typeof(MemoryGraphPortion) };
        var prohibitedTypes = new[] { typeof(MemoryId), typeof(MemoryScope), typeof(ActorId), typeof(MemoryRecord) };
        foreach (var browserType in browserTypes)
        {
            var properties = browserType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            Assert.DoesNotContain(properties, property => prohibitedTypes.Contains(property.PropertyType));
        }

        Assert.Equal(typeof(GraphNodeKey), typeof(MemoryGraphBrowserNode).GetProperty(nameof(MemoryGraphBrowserNode.NodeKey))!.PropertyType);
        Assert.Equal(typeof(string), typeof(MemoryGraphBrowserNode).GetProperty(nameof(MemoryGraphBrowserNode.RecordHref))!.PropertyType);
        Assert.Contains(MemoryGraphPortionState.CatalogNotReady, Enum.GetValues<MemoryGraphPortionState>());
    }

    [Fact]
    public void GraphPortionStates_RemainDistinctAndBrowserModelsHaveNoAuthorityOrStoragePath()
    {
        Assert.Equal(
        [
            MemoryGraphPortionState.Available,
            MemoryGraphPortionState.Stale,
            MemoryGraphPortionState.Changed,
            MemoryGraphPortionState.NotFound,
            MemoryGraphPortionState.Unavailable,
            MemoryGraphPortionState.CatalogNotReady
        ],
        Enum.GetValues<MemoryGraphPortionState>());

        var forbiddenNames = new[] { "Actor", "Scope", "MemoryId", "Storage", "Path", "SourceOffset", "Limit" };
        foreach (var browserType in new[] { typeof(MemoryGraphBrowserNode), typeof(MemoryGraphBrowserEdge) })
        {
            Assert.DoesNotContain(browserType.GetProperties(BindingFlags.Public | BindingFlags.Instance), property =>
                forbiddenNames.Any(forbidden => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static void AssertParameter(Type port, string methodName, Type expectedType)
    {
        var method = Assert.Single(port.GetMethods(), item => item.Name == methodName);
        Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == expectedType);
    }

    private static MemoryGraphSourceRecord Source(
        string id,
        MemoryScope scope,
        MemoryLifecycleStatus status = MemoryLifecycleStatus.Active,
        DateTimeOffset? expiresAt = null) => new(
        new MemoryId(id), scope, MemoryRecordType.Fact, status, .5, .5, ["entity"], expiresAt);
}
