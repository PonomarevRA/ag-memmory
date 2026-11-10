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
        Assert.Equal(2, source.MetadataReads);
        Assert.Equal(2, source.LeafReads);
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

    [Fact]
    public async Task Browse_HidesUncuratedEventsAndBlankFallbackRecords()
    {
        var curated = Record("first", MemoryRecordType.Fact, ["shared"]);
        var transportEvent = Record("transport-event", MemoryRecordType.Event, []) with { CanonicalText = string.Empty };
        var blankOutcome = Record("blank-outcome", MemoryRecordType.Outcome, []) with { CanonicalText = "  \r\n" };
        var service = Service(new CatalogSource([curated, transportEvent, blankOutcome]));

        var page = await service.BrowseAsync(new(Actor, Scope, null, MemoryReaderCatalogFilter.Empty, Version), default);

        var document = Assert.Single(page.Documents);
        Assert.Equal("First article", document.Title);
        Assert.DoesNotContain(page.Namespaces, facet => facet.Locator == "inbox");
    }

    /// <summary>A metadata read may race with a mutation; an old one-page generation must never be emitted as available.</summary>
    [Fact]
    public async Task Browse_ReturnsChangedWhenGenerationChangesDuringFacetMetadataRead()
    {
        var source = new CatalogSource([Record("first", MemoryRecordType.Fact, [])])
            { ChangeGenerationAfterMetadataRead = true };
        var page = await Service(source).BrowseAsync(new(Actor, Scope, null, MemoryReaderCatalogFilter.Empty, Version), default);
        Assert.Equal(MemoryReaderCatalogState.Changed, page.State);
        Assert.Empty(page.Documents);
        Assert.Empty(page.Tags);
    }

    /// <summary>Compiled facets remove whole-catalog metadata scans; browsing a large catalog reads only one card page.</summary>
    [Fact]
    public async Task Browse_CompiledFacetsKeepSourceAndMetadataReadsBoundedByPageSize()
    {
        var records = Enumerable.Range(0, 1_000).Select(index => Record($"bulk-{index:D4}", MemoryRecordType.Fact, [])).ToArray();
        var source = new CatalogSource(records);
        var compiled = new CompiledCatalogSource(source);
        var authorization = new FixedAuthorization();
        authorization.Allow(Actor, MemoryOperation.ReaderCatalogRead, Scope);
        var service = new MemoryReaderCatalogQueryService(compiled, source, authorization, new TestClock(Now), Version, source);
        var page = await service.BrowseAsync(new(Actor, Scope, null, MemoryReaderCatalogFilter.Empty, Version), default);
        Assert.Equal(MemoryReaderCatalogState.Available, page.State);
        Assert.Equal(MemoryReaderLimits.DocumentsPerPage, page.Documents.Count);
        Assert.Equal(MemoryReaderLimits.DocumentsPerPage, source.MetadataReads);
        Assert.Equal(1, source.LeafReads);
        Assert.Equal(2, compiled.FacetReads);
        Assert.Equal(1_000, Assert.Single(page.Namespaces).Count);

        Assert.NotNull(page.NextCursor);
        var next = await service.BrowseAsync(new(Actor, Scope, page.NextCursor, MemoryReaderCatalogFilter.Empty, Version), default);
        Assert.Equal(MemoryReaderCatalogState.Available, next.State);
        Assert.Equal(MemoryReaderLimits.DocumentsPerPage, next.Documents.Count);
        Assert.NotEqual(page.Documents[0].RecordHref, next.Documents[0].RecordHref);
        Assert.Equal(2, source.LeafReads);
        Assert.Equal(4, compiled.FacetReads);
    }

    /// <summary>Metadata may invalidate a generation after the compiled facets were read but before cards are emitted.</summary>
    [Fact]
    public async Task Browse_CompiledFacetsReturnChangedAfterCardMetadataInvalidatesGeneration()
    {
        var source = new CatalogSource([Record("first", MemoryRecordType.Fact, [])]);
        var compiled = new CompiledCatalogSource(source) { ChangeAfterMetadataRead = true };
        var authorization = new FixedAuthorization();
        authorization.Allow(Actor, MemoryOperation.ReaderCatalogRead, Scope);
        var service = new MemoryReaderCatalogQueryService(compiled, source, authorization, new TestClock(Now), Version, source);
        var page = await service.BrowseAsync(new(Actor, Scope, null, MemoryReaderCatalogFilter.Empty, Version), default);
        Assert.Equal(MemoryReaderCatalogState.Changed, page.State);
        Assert.Empty(page.Documents);
        Assert.Empty(page.Tags);
        Assert.Equal(1, source.MetadataReads);
        Assert.Equal(2, compiled.FacetReads);
    }

    /// <summary>A rejected or switched compiled snapshot must not cause a fallback full scan or stale count disclosure.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Browse_CompiledFacetsRejectChangedOrMissingGeneration(bool wrongGeneration)
    {
        var source = new CatalogSource([Record("first", MemoryRecordType.Fact, [])]);
        var compiled = new CompiledCatalogSource(source) { Missing = !wrongGeneration, WrongGeneration = wrongGeneration };
        var authorization = new FixedAuthorization();
        authorization.Allow(Actor, MemoryOperation.ReaderCatalogRead, Scope);
        var service = new MemoryReaderCatalogQueryService(compiled, source, authorization, new TestClock(Now), Version, source);
        var page = await service.BrowseAsync(new(Actor, Scope, null, MemoryReaderCatalogFilter.Empty, Version), default);
        Assert.Equal(MemoryReaderCatalogState.Changed, page.State);
        Assert.Empty(page.Documents);
        Assert.Empty(page.Namespaces);
        Assert.Equal(0, source.MetadataReads);
        Assert.Equal(1, source.LeafReads);
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

    private sealed class CompiledCatalogSource(CatalogSource source) : IMemoryReaderCatalogSource, IMemoryReaderCatalogFacetSource
    {
        public bool Missing { get; init; }
        public bool WrongGeneration { get; init; }
        public bool ChangeAfterMetadataRead { get; init; }
        public int FacetReads { get; private set; }
        public Task<MemoryReaderCatalogBuildPortion> ReadBuildPortionAsync(MemoryReaderCatalogBuildRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<MemoryReaderCatalogLeafPage?> ReadReadyLeafPageAsync(MemorySearchEligibility eligibility, MemoryReaderCatalogCursor? cursor, CancellationToken cancellationToken) =>
            source.ReadReadyLeafPageAsync(eligibility, cursor, cancellationToken);
        public Task<MemoryReaderCatalogFacets?> ReadReadyFacetsAsync(MemorySearchEligibility eligibility, string generationKey, CancellationToken cancellationToken)
        {
            FacetReads++;
            return Task.FromResult<MemoryReaderCatalogFacets?>(Missing || ChangeAfterMetadataRead && source.MetadataReads > 0 ? null :
                new(WrongGeneration ? "foreign-generation" : generationKey, [new("inbox", "inbox", 1_000)], []));
        }
    }

    private sealed class CatalogSource(IReadOnlyList<MemoryReaderSourceRecord> records) : IMemoryReaderCatalogSource, IMemoryReaderSource, IMemoryWikiMetadataStore
    {
        public bool ChangeGenerationAfterMetadataRead { get; init; }
        public int MetadataReads { get; private set; }
        public int LeafReads { get; private set; }
        private const string Generation = "catalog-generation";
        private readonly Dictionary<MemoryId, MemoryReaderSourceRecord> _records = records.ToDictionary(record => record.MemoryId);

        public Task<MemoryReaderCatalogBuildPortion> ReadBuildPortionAsync(MemoryReaderCatalogBuildRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MemoryReaderCatalogLeafPage?> ReadReadyLeafPageAsync(
            MemorySearchEligibility eligibility,
            MemoryReaderCatalogCursor? cursor,
            CancellationToken cancellationToken)
        {
            LeafReads++;
            if (cursor is not null && !string.Equals(cursor.GenerationKey, Generation, StringComparison.Ordinal))
                return Task.FromResult<MemoryReaderCatalogLeafPage?>(null);
            var eligible = records.Where(record => record.Status == MemoryLifecycleStatus.Active).ToArray();
            var start = cursor?.NextLeafPosition ?? 0;
            var entries = eligible.Skip(start).Take(MemoryReaderLimits.DocumentsPerPage).Select((record, index) => new MemoryReaderCatalogLeafEntry(
                start + index, record.MemoryId, record.Type, record.CanonicalText, record.UpdatedAt, record.Version)).ToArray();
            var next = start + entries.Length < eligible.Length ? new MemoryReaderCatalogCursor(Generation, start + entries.Length) : null;
            var generation = ChangeGenerationAfterMetadataRead && MetadataReads > 0 ? "changed-generation" : Generation;
            return Task.FromResult<MemoryReaderCatalogLeafPage?>(new(generation, entries, next));
        }

        public Task<MemoryReaderSourceRecord?> ReadByIdAsync(MemorySearchEligibility eligibility, MemoryId memoryId, CancellationToken cancellationToken) =>
            Task.FromResult(_records.GetValueOrDefault(memoryId));

        public Task<MemoryReaderRoute> GetOrCreateRouteAsync(MemoryId memoryId, CancellationToken cancellationToken) =>
            Task.FromResult(new MemoryReaderRoute($"route-{memoryId.Value}", memoryId));

        public Task<MemoryId?> ResolveRouteAsync(string routeKey, CancellationToken cancellationToken) => Task.FromResult<MemoryId?>(null);

        public Task UpsertWikiMetadataAsync(MemoryWikiMetadata metadata, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MemoryWikiMetadata?> ReadWikiMetadataAsync(MemoryScope scope, MemoryId memoryId, long recordVersion, CancellationToken cancellationToken)
        {
            MetadataReads++;
            return Task.FromResult(memoryId.Value switch
            {
                "first" => (MemoryWikiMetadata?)new(scope, memoryId, recordVersion, "First article", "engineering/core", "first-article", ["shared", "guide"]),
                "second" => new(scope, memoryId, recordVersion, null, null, null, ["shared"]),
                _ => null
            });
        }
    }
}
