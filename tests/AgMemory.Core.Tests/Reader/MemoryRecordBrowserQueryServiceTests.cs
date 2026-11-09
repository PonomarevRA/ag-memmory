using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests.Reader;

public sealed class MemoryRecordBrowserQueryServiceTests
{
    private static readonly ActorId Actor = new("browser-actor");
    private static readonly MemoryScope Scope = new(new("tenant"), new("project"), new("workspace"), new("chat"), new("run"));
    private static readonly ContractVersion Version = new("memory-record-browser-v1");

    [Fact]
    public async Task Browse_HasNoDurableOrRouteKey_BoundsPreview_AndBindsCursorToSnapshot()
    {
        var source = new Source([Record("one", "First line\n" + new string('x', 400), ["Team Alpha"]), Record("two", "Second", ["other"])]);
        var auth = new FixedAuthorization(); auth.Allow(Actor, MemoryOperation.RecordBrowserRead, Scope);
        var service = new MemoryRecordBrowserQueryService(source, auth, new TestClock(DateTimeOffset.UtcNow), Version);

        var page = await service.BrowseAsync(new(Actor, Scope, null, new(null, null, "team alpha", null, MemoryRecordBrowserSort.UpdatedDescending), Version), default);

        var item = Assert.Single(page.Records);
        Assert.Equal("First line", item.Title);
        Assert.EndsWith("…", item.Preview);
        Assert.DoesNotContain(typeof(MemoryRecordBrowserItem).GetProperties(), property => property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase) || property.PropertyType == typeof(MemoryId));
        var changed = await service.BrowseAsync(new(Actor, Scope, new("wrong", 0), new(null, null, null, null, MemoryRecordBrowserSort.UpdatedDescending), Version), default);
        Assert.Equal(MemoryRecordBrowserState.Changed, changed.State);
    }

    [Fact]
    public async Task Browse_ReturnsChanged_WhenARecordMutatesBetweenPages()
    {
        var records = Enumerable.Range(0, MemoryRecordBrowserLimits.PageSize + 1)
            .Select(index => Record($"record-{index:D2}", $"Record {index}", []) with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-index) }).ToList();
        var source = new Source(records);
        var auth = new FixedAuthorization(); auth.Allow(Actor, MemoryOperation.RecordBrowserRead, Scope);
        var service = new MemoryRecordBrowserQueryService(source, auth, new TestClock(DateTimeOffset.UtcNow), Version);
        var request = new MemoryRecordBrowserRequest(Actor, Scope, null, new(null, null, null, null, MemoryRecordBrowserSort.UpdatedDescending), Version);

        var first = await service.BrowseAsync(request, default);
        Assert.NotNull(first.NextCursor);
        source.Replace(records[0], records[0] with { Version = records[0].Version + 1, UpdatedAt = records[0].UpdatedAt.AddSeconds(1) });

        var continued = await service.BrowseAsync(request with { Cursor = first.NextCursor }, default);

        Assert.Equal(MemoryRecordBrowserState.Changed, continued.State);
        Assert.Empty(continued.Records);
        Assert.Null(continued.NextCursor);
    }

    [Fact]
    public async Task Browse_DefensivelyExcludesRowsOutsideTheRequestedExactScope()
    {
        var foreign = Scope with { RunId = new ScopeId("other-run") };
        var source = new Source([Record("allowed", "Allowed", []), new(new("foreign"), foreign, MemoryRecordType.Fact, MemoryLifecycleStatus.Active, "Foreign", DateTimeOffset.UtcNow, 1, null, [])]);
        var auth = new FixedAuthorization(); auth.Allow(Actor, MemoryOperation.RecordBrowserRead, Scope);
        var service = new MemoryRecordBrowserQueryService(source, auth, new TestClock(DateTimeOffset.UtcNow), Version);

        var page = await service.BrowseAsync(new(Actor, Scope, null, new(null, null, null, null, MemoryRecordBrowserSort.UpdatedDescending), Version), default);

        Assert.Single(page.Records);
        Assert.Equal("Allowed", page.Records[0].Title);
    }

    private static MemoryRecordBrowserSourceRecord Record(string id, string text, IReadOnlyList<string> entities) => new(new(id), Scope, MemoryRecordType.Fact, MemoryLifecycleStatus.Active, text, DateTimeOffset.UtcNow, 1, null, entities);
    private sealed class Source(IReadOnlyList<MemoryRecordBrowserSourceRecord> records) : IMemoryRecordBrowserSource
    {
        private IReadOnlyList<MemoryRecordBrowserSourceRecord> _records = records;
        public Task<IReadOnlyList<MemoryRecordBrowserSourceRecord>> ReadAsync(MemoryRecordBrowserSourceRequest request, CancellationToken cancellationToken) => Task.FromResult(_records);
        public void Replace(MemoryRecordBrowserSourceRecord previous, MemoryRecordBrowserSourceRecord replacement) => _records = _records.Select(record => record == previous ? replacement : record).ToArray();
    }
}
