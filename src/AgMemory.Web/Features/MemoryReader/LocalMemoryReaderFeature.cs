using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;
using Microsoft.AspNetCore.DataProtection;

namespace AgMemory.Web.Features.MemoryReader;

/// <summary>Server-only reader composition with one configured actor, home record and exact scope.</summary>
public sealed class LocalMemoryReaderFeature : IAsyncDisposable
{
    private static readonly ContractVersion ContractVersion = new("memory-reader-v1");
    private const string NavigationSchemaVersion = "memory-reader-navigation-v1";
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

    public Task<MemoryReaderDocumentPage> ReadHomeAsync(CancellationToken cancellationToken)
    {
        var configuration = _configuration ?? throw new InvalidOperationException("The local memory reader is unavailable.");
        if (configuration.HomeMemoryId is null) throw new InvalidOperationException("No local home document is configured.");
        return GetOrCreateQuery(configuration).ReadHomeAsync(new(
            configuration.Actor, configuration.Scope, configuration.HomeMemoryId.Value, ContractVersion), cancellationToken);
    }

    public Task<MemoryReaderCatalogPage> BrowseAsync(MemoryReaderCatalogCursor? cursor, CancellationToken cancellationToken)
    {
        var configuration = _configuration ?? throw new InvalidOperationException("The local memory reader is unavailable.");
        return GetOrCreateCatalogQuery(configuration).BrowseAsync(new(
            configuration.Actor, configuration.Scope, cursor, ContractVersion), cancellationToken);
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

    public string ProtectBlock(string routeKey, string blockKey) => Protect(new(NavigationSchemaVersion, "block", routeKey, blockKey, null, null));

    public string? ProtectContinuation(string routeKey, MemoryReaderBlockCursor? cursor) => cursor is null
        ? null
        : Protect(new(NavigationSchemaVersion, "cursor", routeKey, null, cursor.Version, cursor.NextBlockIndex));

    public string? ProtectCatalogContinuation(MemoryReaderCatalogCursor? cursor) => cursor is null
        ? null
        : Protect(new(NavigationSchemaVersion, "catalog", null, null, null, null, cursor.GenerationKey, cursor.NextLeafPosition));

    public bool TryUnprotectCatalogContinuation(string token, out MemoryReaderCatalogCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(token)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<ReaderNavigationToken>(_navigationProtector.Unprotect(token), JsonOptions);
            if (payload is null || !string.Equals(payload.SchemaVersion, NavigationSchemaVersion, StringComparison.Ordinal) ||
                payload.Kind != "catalog" || string.IsNullOrWhiteSpace(payload.GenerationKey) || payload.NextLeafPosition is null || payload.NextLeafPosition < 0) return false;
            cursor = new(payload.GenerationKey, payload.NextLeafPosition.Value);
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
                new ExactLocalReaderAuthorization(configuration), new SystemClock(), ContractVersion);
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
        int? NextLeafPosition = null);
}
