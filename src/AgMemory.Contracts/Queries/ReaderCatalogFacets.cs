namespace AgMemory.Contracts;

/// <summary>Exact eligible facet counts compiled for one immutable catalog generation; never grants scope authority.</summary>
public sealed record MemoryReaderCatalogFacets(
    string GenerationKey,
    IReadOnlyList<MemoryReaderCatalogFacet> Namespaces,
    IReadOnlyList<MemoryReaderCatalogFacet> Tags);

/// <summary>Optional compiled-facet capability. A null result means the supplied ready generation is no longer valid.</summary>
public interface IMemoryReaderCatalogFacetSource
{
    Task<MemoryReaderCatalogFacets?> ReadReadyFacetsAsync(
        MemorySearchEligibility eligibility,
        string generationKey,
        CancellationToken cancellationToken);
}
