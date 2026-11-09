using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    private const string ReaderCatalogFacetsTable = "memory_reader_catalog_facets";
    private static readonly string[] ReaderCatalogFacetColumns =
        ["generation_key", "scope_key", "as_of_utc", "valid_until_utc", "namespaces_json", "tags_json"];

    /// <summary>Reads precompiled facets only for the authorized current generation and its unexpired validity interval.</summary>
    public async Task<MemoryReaderCatalogFacets?> ReadReadyFacetsAsync(
        MemorySearchEligibility eligibility, string generationKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        if (string.IsNullOrWhiteSpace(generationKey) || generationKey.Length > 128 || eligibility.Types is not null) return null;
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var ready = eligibility.AuthorizedScopes.Selectors.Count == 1
                ? await ReadCatalogGenerationAsync(eligibility.AuthorizedScopes.Selectors[0].Scope, cancellationToken).ConfigureAwait(false)
                : null;
            if (ready is null || !IsReadyCatalogGeneration(ready) || !string.Equals(ready.GenerationKey, generationKey, StringComparison.Ordinal)) return null;
            var snapshot = await ReadCompiledFacetsCoreAsync(generationKey, cancellationToken).ConfigureAwait(false);
            if (snapshot is null || !snapshot.IsValid(eligibility)) return null;
            return new(generationKey, snapshot.Namespaces, snapshot.Tags);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Gets a type-independent ready generation, rebuilding when an expiry boundary or changed projection makes it stale.</summary>
    private async Task<PersistedReaderCatalogGenerationRow?> GetReadyCatalogGenerationCoreAsync(
        MemorySearchEligibility eligibility, CancellationToken cancellationToken)
    {
        if (eligibility.AuthorizedScopes.Selectors.Count != 1) return null;
        var scope = eligibility.AuthorizedScopes.Selectors[0].Scope;
        while (true)
        {
            var ready = await ReadCatalogGenerationAsync(scope, cancellationToken).ConfigureAwait(false);
            if (ready is not null && IsReadyCatalogGeneration(ready))
            {
                var snapshot = await ReadCompiledFacetsCoreAsync(ready.GenerationKey, cancellationToken, includeFacetData: false).ConfigureAwait(false);
                if (snapshot is not null && snapshot.IsValid(eligibility)) return ready;
            }
            var builder = new StorageLeaseGate(_options.StoragePath, $".agmemory-catalog-{Hash(ScopeKey(scope))}.lock");
            if (!builder.TryWait())
            {
                await WaitForCatalogBuilderAsync(builder, cancellationToken).ConfigureAwait(false);
                continue;
            }
            try
            {
                // A scope's immutable generation contains all types; restrictions apply only on reading.
                var unrestricted = new MemorySearchEligibility(eligibility.AuthorizedScopes, null, eligibility.AsOfUtc);
                return await BuildReadyCatalogGenerationAsync(scope, unrestricted, cancellationToken).ConfigureAwait(false);
            }
            catch (StaleCatalogBuildException)
            {
                // Any source mutation invalidates physical Offset traversal. Leave staged rows for grace-based GC;
                // the caller receives a changed/unavailable generation and can retry against the new source epoch.
                return null;
            }
            finally { builder.Release(); }
        }

    }

    /// <summary>Reads one summary row; freshness checks select only validity columns so tree pages never reload facet payloads.</summary>
    private async Task<CompiledFacetSnapshot?> ReadCompiledFacetsCoreAsync(
        string generationKey, CancellationToken cancellationToken, bool includeFacetData = true)
    {
        using var table = await OpenTableAsync(ReaderCatalogFacetsTable, cancellationToken).ConfigureAwait(false);
        string[] columns = includeFacetData ? ReaderCatalogFacetColumns : ["scope_key", "as_of_utc", "valid_until_utc"];
        using var batch = await table.Query().Select(columns).Where($"generation_key = {Literal(generationKey)}")
            .Limit(2).ToArrow().ConfigureAwait(false);
        if (batch.Length > 1) throw new InvalidDataException("The reader catalog has duplicate facet summaries.");
        if (batch.Length == 0) return null;
        var validUntil = Value(batch, "valid_until_utc", 0);
        return new(Value(batch, "scope_key", 0)!, ParseUtc(Value(batch, "as_of_utc", 0)!),
            validUntil is null ? null : ParseUtc(validUntil),
            includeFacetData ? Deserialize<MemoryReaderCatalogFacet[]>(Value(batch, "namespaces_json", 0)!) : [],
            includeFacetData ? Deserialize<MemoryReaderCatalogFacet[]>(Value(batch, "tags_json", 0)!) : []);
    }

    /// <summary>Publishes exact namespace and bounded tag counts before the generation pointer becomes ready.</summary>
    private async Task PersistCompiledFacetsCoreAsync(
        MemoryScope scope, string generationKey, MemorySearchEligibility eligibility,
        DateTimeOffset? validUntil, FacetCompiler facets, CancellationToken cancellationToken)
    {
        var values = new[]
        {
            generationKey, ScopeKey(scope), Utc(eligibility.AsOfUtc), validUntil is null ? null : Utc(validUntil.Value),
            JsonSerializer.Serialize(facets.Namespaces(), JsonOptions), JsonSerializer.Serialize(facets.Tags(), JsonOptions)
        };
        var arrays = new List<IArrowArray>(values.Length);
        foreach (var value in values) arrays.Add(new StringArray.Builder().Append(value).Build());
        using var table = await OpenTableAsync(ReaderCatalogFacetsTable, cancellationToken).ConfigureAwait(false);
        using var batch = new RecordBatch(CreateReaderCatalogFacetsSchema(), arrays, 1);
        await table.MergeInsert("generation_key").WhenMatchedUpdateAll().WhenNotMatchedInsertAll()
            .Execute(batch).ConfigureAwait(false);
    }

    private static LanceDbTableSchemaDefinition ReaderCatalogFacetsDefinition() =>
        new(ReaderCatalogFacetsTable, CurrentStorageSchemaVersion, CreateReaderCatalogFacetsSchema());

    private static Schema CreateReaderCatalogFacetsSchema()
    {
        var builder = new Schema.Builder();
        foreach (var column in ReaderCatalogFacetColumns)
            builder.Field(new Field(column, StringType.Default, column == "valid_until_utc"));
        return builder.Build();
    }

    private sealed record CompiledFacetSnapshot(
        string ScopeKeyValue, DateTimeOffset AsOfUtc, DateTimeOffset? ValidUntilUtc,
        IReadOnlyList<MemoryReaderCatalogFacet> Namespaces, IReadOnlyList<MemoryReaderCatalogFacet> Tags)
    {
        /// <summary>The source view is stable only until its earliest expiry; caller-provided scope values never select another summary.</summary>
        public bool IsValid(MemorySearchEligibility eligibility) =>
            eligibility.AuthorizedScopes.Selectors.Count == 1 &&
            string.Equals(ScopeKeyValue, ScopeKey(eligibility.AuthorizedScopes.Selectors[0].Scope), StringComparison.Ordinal) &&
            eligibility.AsOfUtc >= AsOfUtc && (ValidUntilUtc is null || eligibility.AsOfUtc < ValidUntilUtc);
    }

    /// <summary>Counts visible pages once during compilation, preserving deterministic facet order and exact top-tag counts.</summary>
    private sealed class FacetCompiler
    {
        private readonly SortedDictionary<string, int> _namespaces = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, (string Label, int Count)> _tags = new(StringComparer.Ordinal);

        /// <summary>Adds one visible document to ancestor counts and retains the bounded canonical tag prefix.</summary>
        public void Add(string @namespace, IReadOnlyList<string> tags)
        {
            var segments = @namespace.Split('/');
            for (var depth = 1; depth <= segments.Length; depth++)
            {
                var ancestor = string.Join('/', segments.Take(depth));
                _namespaces[ancestor] = _namespaces.GetValueOrDefault(ancestor) + 1;
            }
            foreach (var tag in tags)
            {
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(tag))).ToLowerInvariant();
                var locator = $"tag/{hash}";
                if (_tags.TryGetValue(locator, out var current))
                {
                    _tags[locator] = (tag, current.Count + 1);
                    continue;
                }
                // The maximum retained locator only decreases, so a rejected tag can never enter the final prefix.
                if (_tags.Count == MemoryReaderLimits.MaximumCatalogTagFacets)
                {
                    var largest = _tags.Last().Key;
                    if (string.CompareOrdinal(locator, largest) >= 0) continue;
                    _tags.Remove(largest);
                }
                _tags.Add(locator, (tag, 1));
            }
        }

        public IReadOnlyList<MemoryReaderCatalogFacet> Namespaces() =>
            _namespaces.Select(pair => new MemoryReaderCatalogFacet(pair.Key, pair.Key, pair.Value)).ToArray();

        public IReadOnlyList<MemoryReaderCatalogFacet> Tags() => _tags
            .Select(pair => new MemoryReaderCatalogFacet(pair.Key, pair.Value.Label, pair.Value.Count)).ToArray();
    }
}
