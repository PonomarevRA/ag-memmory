using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;

namespace AgMemory.Web.Features.MemoryGraph;

/// <summary>
/// Server-only local composition root. It constructs the LanceDB adapter lazily, after the HTTP endpoint has
/// admitted a Development loopback request, so unavailable callers cannot even initialise the store.
/// </summary>
public sealed class LocalMemoryGraphFeature : IAsyncDisposable
{
    private static readonly ContractVersion ContractVersion = new("memory-graph-v1");
    private readonly MemoryGraphHostConfiguration? _configuration;
    private readonly object _sync = new();
    private LanceDbMemoryStore? _store;
    private MemoryGraphQueryService? _query;

    public LocalMemoryGraphFeature(MemoryGraphHostOptions options, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        _configuration = options.TryCreate(contentRootPath);
    }

    public bool IsConfigured => _configuration is not null;

    public Task<MemoryGraphSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var configuration = _configuration ?? throw new InvalidOperationException("The local memory graph is unavailable.");
        var query = GetOrCreateQuery(configuration);
        return query.ReadAsync(new(
            configuration.Actor,
            configuration.Scope,
            MemoryGraphLimits.MaximumSourceRecords,
            MemoryGraphLimits.MaximumVisibleNodes,
            MemoryGraphLimits.MaximumEdges,
            ContractVersion), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        LanceDbMemoryStore? store;
        lock (_sync)
        {
            store = _store;
            _store = null;
            _query = null;
        }

        if (store is not null)
            await store.DisposeAsync().ConfigureAwait(false);
    }

    private MemoryGraphQueryService GetOrCreateQuery(MemoryGraphHostConfiguration configuration)
    {
        lock (_sync)
        {
            if (_query is not null)
                return _query;

            _store = new LanceDbMemoryStore(new(configuration.StoragePath));
            _query = new MemoryGraphQueryService(
                _store,
                new ExactLocalGraphAuthorization(configuration),
                new SystemClock(),
                ContractVersion);
            return _query;
        }
    }

    private sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class ExactLocalGraphAuthorization(MemoryGraphHostConfiguration configuration) : IAuthorizationScopeValidator
    {
        public Task<ScopeAuthorizationResult> AuthorizeAsync(
            ActorId actor,
            MemoryOperation operation,
            MemoryScope requestedScope,
            CancellationToken cancellationToken) =>
            Task.FromResult(operation == MemoryOperation.GraphRead && actor == configuration.Actor && requestedScope == configuration.Scope
                ? ScopeAuthorizationResult.Allowed(
                    new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]),
                    "local-graph-v1")
                : ScopeAuthorizationResult.Denied(MemoryErrorCode.Unauthorized, "local-graph-v1"));
    }
}
