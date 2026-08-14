using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;
using AgMemory.Web.Features.MemoryGraph;
using Microsoft.AspNetCore.DataProtection;

namespace AgMemory.Web.Features.MemoryReader;

/// <summary>Server-only reader composition with one configured actor, home record and exact scope.</summary>
public sealed class LocalMemoryReaderFeature : IAsyncDisposable
{
    private static readonly ContractVersion ContractVersion = new("memory-reader-v1");
    private const string NavigationSchemaVersion = "memory-reader-navigation-v2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly MemoryReaderHostConfiguration? _configuration;
    private readonly IDataProtector _navigationProtector;
    private readonly object _sync = new();
    private LanceDbMemoryStore? _store;
    private MemoryReaderQueryService? _query;
    private MemoryReaderCatalogQueryService? _catalogQuery;

    public LocalMemoryReaderFeature(MemoryReaderHostOptions options, string dataDirectory, IDataProtectionProvider dataProtection)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataProtection);
        _configuration = options.TryCreate(dataDirectory);
        _navigationProtector = dataProtection.CreateProtector("AgMemory.Web.MemoryReader.Navigation.v1");
    }

    public bool IsConfigured => _configuration is not null;
    public bool HasHomeDocument => _configuration?.HomeMemoryId is not null;

    internal bool AlignsWith(LocalMemoryGraphFeature graph) =>
        _configuration is not null && graph.MatchesExactStore(_configuration.Actor, _configuration.Scope, _configuration.StoragePath);

    public Task<MemoryReaderDocumentPage> ReadHomeAsync(CancellationToken cancellationToken)
    {
        var configuration = _configuration ?? throw new InvalidOperationException("The local memory reader is unavailable.");
        if (configuration.HomeMemoryId is null) throw new InvalidOperationException("No local home document is configured.");
        return GetOrCreateQuery(configuration).ReadHomeAsync(new(
            configuration.Actor, configuration.Scope, configuration.HomeMemoryId.Value, ContractVersion), cancellationToken);
    }

    public Task<MemoryReaderCatalogPage> BrowseAsync(
        MemoryReaderCatalogCursor? cursor,
        MemoryReaderCatalogFilter filter,
        CancellationToken cancellationToken)
    {
        var configuration = _configuration ?? throw new InvalidOperationException("The local memory reader is unavailable.");
        return GetOrCreateCatalogQuery(configuration).BrowseAsync(new(
            configuration.Actor, configuration.Scope, cursor, filter, ContractVersion), cancellationToken);
    }

    public Task<MemoryReaderDocumentPage> ReadDocumentAsync(
        string routeKey,
        string? requestedBlockKey,
        MemoryReaderBlockCursor? cursor,
        CancellationToken cancellationToken)
    {
        var configuration = _configuration ?? throw new InvalidOperationException("The local memory reader is unavailable.");
        return GetOrCreateQuery(configuration).ReadDocumentAsync(new(
            configuration.Actor, configuration.Scope, routeKey, requestedBlockKey, cursor, ContractVersion), cancellationToken);
    }

    public async Task<MemoryReaderWikiSnapshot?> ReadDocumentWikiSnapshotAsync(
        string routeKey,
        long recordVersion,
        CancellationToken cancellationToken)
    {
        var configuration = _configuration ?? throw new InvalidOperationException("The local memory reader is unavailable.");
        if (string.IsNullOrWhiteSpace(routeKey) || routeKey.Length > 128 || recordVersion <= 0) return null;
        LanceDbMemoryStore store;
        lock (_sync)
        {
            _store ??= new LanceDbMemoryStore(new(configuration.StoragePath));
            store = _store;
        }
        var eligibility = new MemorySearchEligibility(new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]), null, DateTimeOffset.UtcNow);
        var snapshot = await store.ReadWikiDocumentSnapshotAsync(eligibility, routeKey, recordVersion, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return null;
        return new(snapshot.Value.Metadata,
            await ToRelationsAsync(snapshot.Value.Children, store, cancellationToken, MemoryWikiRelationKind.Child).ConfigureAwait(false),
            await ToRelationsAsync(snapshot.Value.Related, store, cancellationToken, MemoryWikiRelationKind.Related).ConfigureAwait(false),
            await ToRelationsAsync(snapshot.Value.Backlinks, store, cancellationToken, MemoryWikiRelationKind.Backlink).ConfigureAwait(false));
    }

    public async Task<MemoryReaderTreePage> ReadTreeAsync(CancellationToken cancellationToken)
    {
        var configuration = _configuration ?? throw new InvalidOperationException("The local memory reader is unavailable.");
        LanceDbMemoryStore store;
        lock (_sync)
        {
            _store ??= new LanceDbMemoryStore(new(configuration.StoragePath));
            store = _store;
        }
        var eligibility = new MemorySearchEligibility(new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]), null, DateTimeOffset.UtcNow);
        var firstLeaf = await store.ReadReadyLeafPageAsync(eligibility, null, cancellationToken).ConfigureAwait(false);
        if (firstLeaf is null) return MemoryReaderTreePage.NotReady;
        var types = await ReadCompiledLeafTypesAsync(store, eligibility, firstLeaf, cancellationToken).ConfigureAwait(false);
        if (types is null) return MemoryReaderTreePage.NotReady;
        var documents = await store.ReadWikiTreeDocumentsAsync(eligibility, firstLeaf.GenerationKey, cancellationToken).ConfigureAwait(false);
        if (documents.Count == 0) return MemoryReaderTreePage.Empty;
        var childEdges = await store.ReadWikiTreeChildEdgesAsync(firstLeaf.GenerationKey, cancellationToken).ConfigureAwait(false);
        var entries = new List<MemoryReaderTreeDocumentEntry>(documents.Count);
        foreach (var document in documents)
        {
            var route = await store.GetOrCreateRouteAsync(document.MemoryId, cancellationToken).ConfigureAwait(false);
            var type = types.GetValueOrDefault(document.MemoryId.Value, MemoryRecordType.Event);
            entries.Add(new(document.MemoryId.Value, $"/memory-reader/{Uri.EscapeDataString(route.RouteKey)}", document.Title, document.Namespace, type));
        }
        return MemoryReaderTreeBuilder.Build(entries, childEdges);
    }

    public sealed record MemoryReaderWikiRelation(string Href, string Kind, string Label, string Title, string Namespace, int SharedEntityCount);
    public sealed record MemoryReaderWikiSnapshot(MemoryWikiMetadata? Metadata, IReadOnlyList<MemoryReaderWikiRelation> Children,
        IReadOnlyList<MemoryReaderWikiRelation> Related, IReadOnlyList<MemoryReaderWikiRelation> Backlinks);

    private static async Task<IReadOnlyDictionary<string, MemoryRecordType>?> ReadCompiledLeafTypesAsync(
        LanceDbMemoryStore store,
        MemorySearchEligibility eligibility,
        MemoryReaderCatalogLeafPage firstLeaf,
        CancellationToken cancellationToken)
    {
        var types = new Dictionary<string, MemoryRecordType>(StringComparer.Ordinal);
        var page = firstLeaf;
        while (true)
        {
            if (!string.Equals(page.GenerationKey, firstLeaf.GenerationKey, StringComparison.Ordinal)) return null;
            foreach (var entry in page.Entries) types[entry.MemoryId.Value] = entry.Type;
            if (page.NextCursor is null) return types;
            var next = await store.ReadReadyLeafPageAsync(eligibility, page.NextCursor, cancellationToken).ConfigureAwait(false);
            if (next is null) return null;
            page = next;
        }
    }

    private static async Task<IReadOnlyList<MemoryReaderWikiRelation>> ToRelationsAsync(
        IReadOnlyList<MemoryWikiRelationRecord> relations,
        LanceDbMemoryStore store,
        CancellationToken cancellationToken,
        MemoryWikiRelationKind kind)
    {
        var kindLabel = kind switch
        {
            MemoryWikiRelationKind.Child => "child",
            MemoryWikiRelationKind.Related => "related",
            _ => "backlink"
        };
        var result = new List<MemoryReaderWikiRelation>(relations.Count);
        foreach (var relation in relations)
        {
            var route = await store.GetOrCreateRouteAsync(relation.TargetMemoryId, cancellationToken).ConfigureAwait(false);
            result.Add(new($"/memory-reader/{Uri.EscapeDataString(route.RouteKey)}", kindLabel, relation.Label, relation.Title, relation.Namespace, relation.SharedEntityCount));
        }
        return result;
    }

    public string ProtectBlock(string routeKey, string blockKey) => Protect(new(NavigationSchemaVersion, "block", routeKey, blockKey, null, null));

    public string? ProtectContinuation(string routeKey, MemoryReaderBlockCursor? cursor) => cursor is null
        ? null
        : Protect(new(NavigationSchemaVersion, "cursor", routeKey, null, cursor.Version, cursor.NextBlockIndex));

    public string? ProtectCatalogContinuation(MemoryReaderCatalogCursor? cursor, MemoryReaderCatalogFilter filter) => cursor is null
        ? null
        : Protect(new(NavigationSchemaVersion, "catalog", null, null, null, null, cursor.GenerationKey, cursor.NextLeafPosition,
            filter.Namespace, filter.Tag));

    public bool TryUnprotectCatalogContinuation(
        string token,
        string? @namespace,
        string? tag,
        out MemoryReaderCatalogCursor? cursor,
        out MemoryReaderCatalogFilter filter)
    {
        cursor = null;
        filter = MemoryReaderCatalogFilter.Empty;
        if (string.IsNullOrWhiteSpace(token) || !MemoryReaderCatalogQueryService.TryNormalizeFilter(@namespace, tag, out var supplied)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<ReaderNavigationToken>(_navigationProtector.Unprotect(token), JsonOptions);
            if (payload is null || !string.Equals(payload.SchemaVersion, NavigationSchemaVersion, StringComparison.Ordinal) ||
                payload.Kind != "catalog" || string.IsNullOrWhiteSpace(payload.GenerationKey) || payload.NextLeafPosition is null || payload.NextLeafPosition < 0 ||
                !string.Equals(payload.Namespace, supplied.Namespace, StringComparison.Ordinal) ||
                !string.Equals(payload.Tag, supplied.Tag, StringComparison.Ordinal)) return false;
            cursor = new(payload.GenerationKey, payload.NextLeafPosition.Value);
            filter = supplied;
            return true;
        }
        catch { return false; }
    }

    public bool TryUnprotectNavigation(string routeKey, string token, out string? blockKey, out MemoryReaderBlockCursor? cursor)
    {
        blockKey = null;
        cursor = null;
        if (string.IsNullOrWhiteSpace(routeKey) || string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<ReaderNavigationToken>(_navigationProtector.Unprotect(token), JsonOptions);
            if (payload is null || !string.Equals(payload.SchemaVersion, NavigationSchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(payload.RouteKey, routeKey, StringComparison.Ordinal)) return false;
            if (payload.Kind == "block" && !string.IsNullOrWhiteSpace(payload.BlockKey))
            {
                blockKey = payload.BlockKey;
                return true;
            }
            if (payload.Kind == "cursor" && payload.Version is > 0 && payload.NextBlockIndex is >= 0)
            {
                cursor = new(payload.Version.Value, payload.NextBlockIndex.Value);
                return true;
            }
        }
        catch
        {
            // A stale, corrupted or old-key token is intentionally indistinguishable to the browser.
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        LanceDbMemoryStore? store;
        lock (_sync)
        {
            store = _store;
            _store = null;
            _query = null;
            _catalogQuery = null;
        }
        if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
    }

    private MemoryReaderQueryService GetOrCreateQuery(MemoryReaderHostConfiguration configuration)
    {
        lock (_sync)
        {
            if (_query is not null) return _query;
            _store ??= new LanceDbMemoryStore(new(configuration.StoragePath));
            _query = new MemoryReaderQueryService(_store, new ExactLocalReaderAuthorization(configuration), new SystemClock(), ContractVersion);
            return _query;
        }
    }

    private MemoryReaderCatalogQueryService GetOrCreateCatalogQuery(MemoryReaderHostConfiguration configuration)
    {
        lock (_sync)
        {
            if (_catalogQuery is not null) return _catalogQuery;
            _store ??= new LanceDbMemoryStore(new(configuration.StoragePath));
            _catalogQuery = new MemoryReaderCatalogQueryService(_store, _store,
                new ExactLocalReaderAuthorization(configuration), new SystemClock(), ContractVersion, _store);
            return _catalogQuery;
        }
    }

    private string Protect(ReaderNavigationToken token) =>
        _navigationProtector.Protect(JsonSerializer.Serialize(token, JsonOptions));

    private sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

    private sealed class ExactLocalReaderAuthorization(MemoryReaderHostConfiguration configuration) : IAuthorizationScopeValidator
    {
        public Task<ScopeAuthorizationResult> AuthorizeAsync(
            ActorId actor,
            MemoryOperation operation,
            MemoryScope requestedScope,
            CancellationToken cancellationToken) =>
            Task.FromResult((operation == MemoryOperation.ReaderRead || operation == MemoryOperation.ReaderCatalogRead) && actor == configuration.Actor && requestedScope == configuration.Scope
                ? ScopeAuthorizationResult.Allowed(new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]), "local-reader-v1")
                : ScopeAuthorizationResult.Denied(MemoryErrorCode.Unauthorized, "local-reader-v1"));
    }

    private sealed record ReaderNavigationToken(
        string SchemaVersion,
        string Kind,
        string? RouteKey,
        string? BlockKey,
        long? Version,
        int? NextBlockIndex,
        string? GenerationKey = null,
        int? NextLeafPosition = null,
        string? Namespace = null,
        string? Tag = null);
}
