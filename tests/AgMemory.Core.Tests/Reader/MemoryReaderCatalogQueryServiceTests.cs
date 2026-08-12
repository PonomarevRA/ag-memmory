using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests.Reader;

public sealed class MemoryReaderCatalogQueryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly ActorId Actor = new("catalog-reader");
    private static readonly MemoryScope Scope = new(new("tenant-a"), new("project-a"), new("workspace-a"), new("chat-a"), new("run-a"));
    private static readonly ContractVersion Version = new("catalog-reader-test-v1");

    [Fact]
    public async Task Browse_DerivesExactGenerationFacetsAndAppliesNamespaceAndTagIntersection()
    {
        var first = Record("first", MemoryRecordType.Fact, ["raw entity is not a tag"]);
        var second = Record("second", MemoryRecordType.Outcome, ["also not visible"]);
        var stale = Record("stale", MemoryRecordType.Fact, ["hidden"]) with { Status = MemoryLifecycleStatus.Invalid };
        var source = new CatalogSource([first, second, stale]);
        var service = Service(source);

        var all = await service.BrowseAsync(new(Actor, Scope, null, MemoryReaderCatalogFilter.Empty, Version), default);

        Assert.Equal(MemoryReaderCatalogState.Available, all.State);
        Assert.Equal(2, all.Documents.Count);
        Assert.Equal(new[] { "engineering", "engineering/core", "inbox" }, all.Namespaces.Select(facet => facet.Locator));
        Assert.Equal(new[] { 1, 1, 1 }, all.Namespaces.Select(facet => facet.Count));
        Assert.DoesNotContain(all.Tags, facet => facet.Label == "hidden");
        Assert.DoesNotContain(all.Tags, facet => facet.Label.Contains("raw entity", StringComparison.Ordinal));
        var shared = Assert.Single(all.Tags, facet => facet.Label == "shared");
        Assert.Equal(2, shared.Count);

        var filtered = await service.BrowseAsync(new(Actor, Scope, null,
            new("engineering/core", shared.Locator), Version), default);

        Assert.Equal(MemoryReaderCatalogState.Available, filtered.State);
        var document = Assert.Single(filtered.Documents);
        Assert.Equal("/memory-reader/route-first", document.RecordHref);
    }

    [Theory]
    [InlineData("engineering/core", null, true)]
    [InlineData("", "", true)]
    [InlineData("Engineering/Core", null, false)]
    [InlineData(null, "tag/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData(null, "tag/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    public void FilterNormalization_AcceptsOnlyCanonicalLocators(string? @namespace, string? tag, bool expected)
    {
        Assert.Equal(expected, MemoryReaderCatalogQueryService.TryNormalizeFilter(@namespace, tag, out _));
    }

    private static MemoryReaderCatalogQueryService Service(CatalogSource source)
    {
        var authorization = new FixedAuthorization();
        authorization.Allow(Actor, MemoryOperation.ReaderCatalogRead, Scope);
        return new(source, source, authorization, new TestClock(Now), Version, source);
    }

    private static MemoryReaderSourceRecord Record(string id, MemoryRecordType type, IReadOnlyList<string> entities) => new(
        new(id), Scope, type, MemoryLifecycleStatus.Active, $"text {id}", Now, Now, 1, null, entities);

    private sealed class CatalogSource(IReadOnlyList<MemoryReaderSourceRecord> records) : IMemoryReaderCatalogSource, IMemoryReaderSource, IMemoryWikiMetadataStore
    {
        private const string Generation = "catalog-generation";
        private readonly Dictionary<MemoryId, MemoryReaderSourceRecord> _records = records.ToDictionary(record => record.MemoryId);

        public Task<MemoryReaderCatalogBuildPortion> ReadBuildPortionAsync(MemoryReaderCatalogBuildRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MemoryReaderCatalogLeafPage?> ReadReadyLeafPageAsync(
            MemorySearchEligibility eligibility,
            MemoryReaderCatalogCursor? cursor,
            CancellationToken cancellationToken)
        {
            if (cursor is not null && !string.Equals(cursor.GenerationKey, Generation, StringComparison.Ordinal))
                return Task.FromResult<MemoryReaderCatalogLeafPage?>(null);
            var start = cursor?.NextLeafPosition ?? 0;
            var entries = records.Skip(start).Take(MemoryReaderLimits.DocumentsPerPage).Select((record, index) => new MemoryReaderCatalogLeafEntry(
                start + index, record.MemoryId, record.Type, string.Empty, record.UpdatedAt, record.Version)).ToArray();
            var next = start + entries.Length < records.Count ? new MemoryReaderCatalogCursor(Generation, start + entries.Length) : null;
            return Task.FromResult<MemoryReaderCatalogLeafPage?>(new(Generation, entries, next));
        }

        public Task<MemoryReaderSourceRecord?> ReadByIdAsync(MemorySearchEligibility eligibility, MemoryId memoryId, CancellationToken cancellationToken) =>
            Task.FromResult(_records.GetValueOrDefault(memoryId));

        public Task<MemoryReaderRoute> GetOrCreateRouteAsync(MemoryId memoryId, CancellationToken cancellationToken) =>
            Task.FromResult(new MemoryReaderRoute($"route-{memoryId.Value}", memoryId));

        public Task<MemoryId?> ResolveRouteAsync(string routeKey, CancellationToken cancellationToken) => Task.FromResult<MemoryId?>(null);

        public Task UpsertWikiMetadataAsync(MemoryWikiMetadata metadata, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MemoryWikiMetadata?> ReadWikiMetadataAsync(MemoryScope scope, MemoryId memoryId, long recordVersion, CancellationToken cancellationToken) =>
            Task.FromResult(memoryId.Value switch
            {
                "first" => (MemoryWikiMetadata?)new(scope, memoryId, recordVersion, "First article", "engineering/core", "first-article", ["shared", "guide"]),
                "second" => new(scope, memoryId, recordVersion, null, null, null, ["shared"]),
                _ => null
            });
    }
}
