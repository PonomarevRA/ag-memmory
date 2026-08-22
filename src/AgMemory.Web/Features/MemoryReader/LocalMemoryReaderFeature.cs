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
    private const string NavigationSchemaVersion = "memory-reader-navigation-v3";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, MemoryReaderAreaConfiguration> _areas;
    private readonly IDataProtector _navigationProtector;
    private readonly object _sync = new();
    private readonly Dictionary<string, AreaRuntime> _runtimes = new(StringComparer.Ordinal);

    public LocalMemoryReaderFeature(MemoryReaderHostOptions options, string dataDirectory, IDataProtectionProvider dataProtection)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dataProtection);
        _areas = options.TryCreateAreas(dataDirectory).ToDictionary(area => area.Id, StringComparer.Ordinal);
        _navigationProtector = dataProtection.CreateProtector("AgMemory.Web.MemoryReader.Navigation.v1");
    }

    public bool IsConfigured => _areas.Count > 0;
    public bool HasHomeDocument => _areas.Values.Any(area => area.Configuration.HomeMemoryId is not null);
    public IReadOnlyList<MemoryReaderAreaDto> Areas => _areas.Values.Select(area => new MemoryReaderAreaDto(area.Id, area.Label, string.Equals(area.Id, "default", StringComparison.Ordinal))).ToArray();

    public bool TryResolveArea(string? areaId, out string area)
    {
        area = string.IsNullOrWhiteSpace(areaId) ? (_areas.ContainsKey("default") ? "default" : _areas.Keys.FirstOrDefault() ?? string.Empty) : areaId;
        return _areas.ContainsKey(area);
    }

    internal bool AlignsWith(LocalMemoryGraphFeature graph) =>
        _areas.Values.Any(area => graph.MatchesExactStore(area.Configuration.Actor, area.Configuration.Scope, area.Configuration.StoragePath));

    public Task<MemoryReaderDocumentPage> ReadHomeAsync(string area, CancellationToken cancellationToken)
    {
        var configuration = GetArea(area).Configuration;
        if (configuration.HomeMemoryId is null) throw new InvalidOperationException("No local home document is configured.");
        return GetOrCreateQuery(area).ReadHomeAsync(new(
            configuration.Actor, configuration.Scope, configuration.HomeMemoryId.Value, ContractVersion), cancellationToken);
    }

    public Task<MemoryReaderDocumentPage> ReadHomeAsync(CancellationToken cancellationToken) => ReadHomeAsync("default", cancellationToken);

    public Task<MemoryReaderCatalogPage> BrowseAsync(
        MemoryReaderCatalogCursor? cursor,
        MemoryReaderCatalogFilter filter, string area,
        CancellationToken cancellationToken)
    {
        var configuration = GetArea(area).Configuration;
        return GetOrCreateCatalogQuery(area).BrowseAsync(new(
            configuration.Actor, configuration.Scope, cursor, filter, ContractVersion), cancellationToken);
    }

    public Task<MemoryRecordBrowserPage> BrowseRecordsAsync(MemoryRecordBrowserCursor? cursor, MemoryRecordBrowserFilter filter, string area, CancellationToken cancellationToken)
    {
        var configuration = GetArea(area).Configuration;
        return GetOrCreateRecordsQuery(area).BrowseAsync(new(configuration.Actor, configuration.Scope, cursor, filter, new ContractVersion("memory-record-browser-v1")), cancellationToken);
    }

    public string? ProtectRecordContinuation(MemoryRecordBrowserCursor? cursor, MemoryRecordBrowserFilter filter, string area) => cursor is null ? null :
        Protect(new(NavigationSchemaVersion, "records", null, filter.Sort.ToString(), null, cursor.Offset, cursor.GenerationKey, Namespace: filter.Namespace, Tag: filter.Tag, Search: filter.Search, AreaId: area, RecordType: filter.Type?.ToString()));

    public bool TryUnprotectRecordContinuation(string token, MemoryRecordBrowserFilter filter, string area, out MemoryRecordBrowserCursor? cursor)
    {
        cursor = null;
        try
        {
            var payload = JsonSerializer.Deserialize<ReaderNavigationToken>(_navigationProtector.Unprotect(token), JsonOptions);
            if (payload?.SchemaVersion != NavigationSchemaVersion || payload.Kind != "records" || payload.NextBlockIndex is not >= 0 || string.IsNullOrWhiteSpace(payload.GenerationKey) || payload.AreaId != area ||
                payload.BlockKey != filter.Sort.ToString() || payload.Namespace != filter.Namespace || payload.Tag != filter.Tag || payload.Search != filter.Search || payload.RecordType != filter.Type?.ToString()) return false;
            cursor = new(payload.GenerationKey, payload.NextBlockIndex.Value); return true;
        }
        catch { return false; }
    }

    public Task<MemoryReaderDocumentPage> ReadDocumentAsync(
        string routeKey,
        string? requestedBlockKey,
        MemoryReaderBlockCursor? cursor, string area,
        CancellationToken cancellationToken)
    {
        var configuration = GetArea(area).Configuration;
        return GetOrCreateQuery(area).ReadDocumentAsync(new(
            configuration.Actor, configuration.Scope, routeKey, requestedBlockKey, cursor, ContractVersion), cancellationToken);
    }

    public async Task<MemoryReaderWikiSnapshot?> ReadDocumentWikiSnapshotAsync(
        string routeKey,
        long recordVersion,
        string area,
        CancellationToken cancellationToken)
    {
        var runtime = GetArea(area);
        var configuration = runtime.Configuration;
        if (string.IsNullOrWhiteSpace(routeKey) || routeKey.Length > 128 || recordVersion <= 0) return null;
        LanceDbMemoryStore store;
        lock (_sync)
        {
            runtime.Store ??= new LanceDbMemoryStore(new(configuration.StoragePath));
            store = runtime.Store;
        }
        var eligibility = new MemorySearchEligibility(new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]), null, DateTimeOffset.UtcNow);
        var snapshot = await store.ReadWikiDocumentSnapshotAsync(eligibility, routeKey, recordVersion, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return null;
        return new(snapshot.Value.Metadata,
            await ToRelationsAsync(snapshot.Value.Children, store, cancellationToken, MemoryWikiRelationKind.Child).ConfigureAwait(false),
            await ToRelationsAsync(snapshot.Value.Related, store, cancellationToken, MemoryWikiRelationKind.Related).ConfigureAwait(false),
            await ToRelationsAsync(snapshot.Value.Backlinks, store, cancellationToken, MemoryWikiRelationKind.Backlink).ConfigureAwait(false));
    }

    public async Task<MemoryReaderTreePage> ReadTreeAsync(string area, CancellationToken cancellationToken)
    {
        var runtime = GetArea(area);
        var configuration = runtime.Configuration;
        LanceDbMemoryStore store;
        lock (_sync)
        {
            runtime.Store ??= new LanceDbMemoryStore(new(configuration.StoragePath));
            store = runtime.Store;
        }
        var eligibility = new MemorySearchEligibility(new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]), null, DateTimeOffset.UtcNow);
        var firstLeaf = await store.ReadReadyLeafPageAsync(eligibility, null, cancellationToken).ConfigureAwait(false);
        if (firstLeaf is null) return MemoryReaderTreePage.NotReady;
        var types = await ReadCompiledLeafTypesAsync(store, eligibility, firstLeaf, cancellationToken).ConfigureAwait(false);
        if (types is null) return MemoryReaderTreePage.NotReady;
        var documents = await store.ReadWikiTreeDocumentsAsync(eligibility, firstLeaf.GenerationKey, cancellationToken).ConfigureAwait(false);
        if (documents.Count == 0) return new("available", [], firstLeaf.GenerationKey);
        var childEdges = await store.ReadWikiTreeChildEdgesAsync(firstLeaf.GenerationKey, cancellationToken).ConfigureAwait(false);
        var entries = new List<MemoryReaderTreeDocumentEntry>(documents.Count);
        foreach (var document in documents)
        {
            var route = await store.GetOrCreateRouteAsync(document.MemoryId, cancellationToken).ConfigureAwait(false);
            var type = types.GetValueOrDefault(document.MemoryId.Value, MemoryRecordType.Event);
            entries.Add(new(document.MemoryId.Value, $"/memory-reader/{Uri.EscapeDataString(route.RouteKey)}", document.Title, document.Namespace, type));
        }
        return MemoryReaderTreeBuilder.Build(entries, childEdges) with { GenerationKey = firstLeaf.GenerationKey };
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

    public string ProtectBlock(string routeKey, string blockKey, string area) => Protect(new(NavigationSchemaVersion, "block", routeKey, blockKey, null, null, AreaId: area));
    public string ProtectBlock(string routeKey, string blockKey) => ProtectBlock(routeKey, blockKey, "default");

    public string? ProtectContinuation(string routeKey, MemoryReaderBlockCursor? cursor, string area) => cursor is null
        ? null
        : Protect(new(NavigationSchemaVersion, "cursor", routeKey, null, cursor.Version, cursor.NextBlockIndex, AreaId: area));

    public string? ProtectCatalogContinuation(MemoryReaderCatalogCursor? cursor, MemoryReaderCatalogFilter filter, string area) => cursor is null
        ? null
        : Protect(new(NavigationSchemaVersion, "catalog", null, null, null, null, cursor.GenerationKey, cursor.NextLeafPosition,
            filter.Namespace, filter.Tag, filter.Search, area));
    public string? ProtectCatalogContinuation(MemoryReaderCatalogCursor? cursor, MemoryReaderCatalogFilter filter) => ProtectCatalogContinuation(cursor, filter, "default");

    public bool TryUnprotectCatalogContinuation(
        string token,
        string? @namespace,
        string? tag,
        string? search, string area,
        out MemoryReaderCatalogCursor? cursor,
        out MemoryReaderCatalogFilter filter)
    {
        cursor = null;
        filter = MemoryReaderCatalogFilter.Empty;
        if (string.IsNullOrWhiteSpace(token) || !MemoryReaderCatalogQueryService.TryNormalizeFilter(@namespace, tag, search, out var supplied)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<ReaderNavigationToken>(_navigationProtector.Unprotect(token), JsonOptions);
            if (payload is null || !string.Equals(payload.SchemaVersion, NavigationSchemaVersion, StringComparison.Ordinal) ||
                payload.Kind != "catalog" || string.IsNullOrWhiteSpace(payload.GenerationKey) || payload.NextLeafPosition is null || payload.NextLeafPosition < 0 ||
                !string.Equals(payload.Namespace, supplied.Namespace, StringComparison.Ordinal) ||
                !string.Equals(payload.Tag, supplied.Tag, StringComparison.Ordinal) ||
                !string.Equals(payload.Search, supplied.Search, StringComparison.Ordinal) || !string.Equals(payload.AreaId, area, StringComparison.Ordinal)) return false;
            cursor = new(payload.GenerationKey, payload.NextLeafPosition.Value);
            filter = supplied;
            return true;
        }
        catch { return false; }
    }

    public string? ProtectTreeContinuation(string generationKey, int nextPosition, string area) => nextPosition <= 0 || string.IsNullOrWhiteSpace(generationKey)
        ? null
        : Protect(new(NavigationSchemaVersion, "tree", null, null, null, nextPosition, generationKey, AreaId: area));

    public bool TryUnprotectTreeContinuation(string token, string area, out string? generationKey, out int position)
    {
        generationKey = null;
        position = 0;
        try
        {
            var payload = JsonSerializer.Deserialize<ReaderNavigationToken>(_navigationProtector.Unprotect(token), JsonOptions);
            if (payload?.SchemaVersion != NavigationSchemaVersion || payload.Kind != "tree" || payload.NextBlockIndex is not > 0 ||
                string.IsNullOrWhiteSpace(payload.GenerationKey) || payload.AreaId != area) return false;
            generationKey = payload.GenerationKey;
            position = payload.NextBlockIndex.Value;
            return true;
        }
        catch { return false; }
    }

    public bool TryUnprotectTreeContinuation(string token, string area, out int position) =>
        TryUnprotectTreeContinuation(token, area, out _, out position);

    public string? ProtectTagContinuation(string generationKey, int nextPosition, string area) => nextPosition <= 0 || string.IsNullOrWhiteSpace(generationKey)
        ? null : Protect(new(NavigationSchemaVersion, "tag", null, null, null, nextPosition, generationKey, AreaId: area));

    public bool TryUnprotectTagContinuation(string token, string area, out string? generationKey, out int position)
    {
        generationKey = null;
        position = 0;
        try
        {
            var payload = JsonSerializer.Deserialize<ReaderNavigationToken>(_navigationProtector.Unprotect(token), JsonOptions);
            if (payload?.SchemaVersion != NavigationSchemaVersion || payload.Kind != "tag" || payload.NextBlockIndex is not > 0 ||
                string.IsNullOrWhiteSpace(payload.GenerationKey) || payload.AreaId != area) return false;
            generationKey = payload.GenerationKey;
            position = payload.NextBlockIndex.Value;
            return true;
        }
        catch { return false; }
    }

    public bool TryUnprotectTagContinuation(string token, string area, out int position) =>
        TryUnprotectTagContinuation(token, area, out _, out position);

    public bool TryUnprotectNavigation(string routeKey, string token, string area, out string? blockKey, out MemoryReaderBlockCursor? cursor)
    {
        blockKey = null;
        cursor = null;
        if (string.IsNullOrWhiteSpace(routeKey) || string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<ReaderNavigationToken>(_navigationProtector.Unprotect(token), JsonOptions);
            if (payload is null || !string.Equals(payload.SchemaVersion, NavigationSchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(payload.RouteKey, routeKey, StringComparison.Ordinal) || !string.Equals(payload.AreaId, area, StringComparison.Ordinal)) return false;
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
    public bool TryUnprotectNavigation(string routeKey, string token, out string? blockKey, out MemoryReaderBlockCursor? cursor) =>
        TryUnprotectNavigation(routeKey, token, "default", out blockKey, out cursor);

    public async ValueTask DisposeAsync()
    {
        LanceDbMemoryStore[] stores;
        lock (_sync)
        {
            stores = _runtimes.Values.Select(runtime => runtime.Store).OfType<LanceDbMemoryStore>().ToArray();
            _runtimes.Clear();
        }
        foreach (var store in stores) await store.DisposeAsync().ConfigureAwait(false);
    }

    private AreaRuntime GetArea(string area) => _areas.TryGetValue(area, out var configuration)
        ? GetOrCreateRuntime(configuration)
        : throw new InvalidOperationException("The local memory reader area is unavailable.");

    private AreaRuntime GetOrCreateRuntime(MemoryReaderAreaConfiguration area)
    {
        lock (_sync)
        {
            if (_runtimes.TryGetValue(area.Id, out var runtime)) return runtime;
            runtime = new(area.Configuration);
            _runtimes.Add(area.Id, runtime);
            return runtime;
        }
    }

    private MemoryReaderQueryService GetOrCreateQuery(string area)
    {
        lock (_sync)
        {
            var runtime = GetArea(area);
            if (runtime.Query is not null) return runtime.Query;
            runtime.Store ??= new LanceDbMemoryStore(new(runtime.Configuration.StoragePath));
            runtime.Query = new MemoryReaderQueryService(runtime.Store, new ExactLocalReaderAuthorization(runtime.Configuration), new SystemClock(), ContractVersion);
            return runtime.Query;
        }
    }

    private MemoryReaderCatalogQueryService GetOrCreateCatalogQuery(string area)
    {
        lock (_sync)
        {
            var runtime = GetArea(area);
            if (runtime.CatalogQuery is not null) return runtime.CatalogQuery;
            runtime.Store ??= new LanceDbMemoryStore(new(runtime.Configuration.StoragePath));
            runtime.CatalogQuery = new MemoryReaderCatalogQueryService(runtime.Store, runtime.Store,
                new ExactLocalReaderAuthorization(runtime.Configuration), new SystemClock(), ContractVersion, runtime.Store);
            return runtime.CatalogQuery;
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
            Task.FromResult((operation == MemoryOperation.ReaderRead || operation == MemoryOperation.ReaderCatalogRead || operation == MemoryOperation.RecordBrowserRead) && actor == configuration.Actor && requestedScope == configuration.Scope
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
        string? Tag = null,
        string? Search = null,
        string? AreaId = null,
        string? RecordType = null);

    private sealed class AreaRuntime(MemoryReaderHostConfiguration configuration)
    {
        public MemoryReaderHostConfiguration Configuration { get; } = configuration;
        public LanceDbMemoryStore? Store { get; set; }
        public MemoryReaderQueryService? Query { get; set; }
        public MemoryReaderCatalogQueryService? CatalogQuery { get; set; }
        public MemoryRecordBrowserQueryService? RecordsQuery { get; set; }
    }

    private MemoryRecordBrowserQueryService GetOrCreateRecordsQuery(string area)
    {
        lock (_sync)
        {
            var runtime = GetArea(area);
            if (runtime.RecordsQuery is not null) return runtime.RecordsQuery;
            runtime.Store ??= new LanceDbMemoryStore(new(runtime.Configuration.StoragePath));
            runtime.RecordsQuery = new MemoryRecordBrowserQueryService(runtime.Store, new ExactLocalReaderAuthorization(runtime.Configuration), new SystemClock(), new ContractVersion("memory-record-browser-v1"));
            return runtime.RecordsQuery;
        }
    }
}

/// <summary>Only safe area metadata is sent to the browser.</summary>
public sealed record MemoryReaderAreaDto(string Id, string Label, bool IsDefault);
