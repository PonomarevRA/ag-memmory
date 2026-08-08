using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Provider-neutral query facade; it authorizes before dispatching any retrieval work.</summary>
public sealed class MemoryQueryService : IMemoryQueryService
{
    private readonly IAuthorizationScopeValidator _authorization;
    private readonly HybridSearchExecutor _search;
    private readonly ContextBuilder _contextBuilder;
    private readonly HotMemoryReader _hotMemoryReader;
    private readonly MemoryCoreOptions _options;

    public MemoryQueryService(
        IMemoryStore store,
        IAuthorizationScopeValidator authorization,
        ILexicalSearch lexical,
        IVectorSearch vector,
        IMemoryGraph? graph,
        IEmbeddingPolicy embeddingPolicy,
        IClock clock,
        MemoryCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(embeddingPolicy);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();

        _hotMemoryReader = new(store, clock);
        _contextBuilder = new(new SummaryFallbackReader(store, _authorization, clock), _options);
        _search = new(lexical, vector, graph, new RetrievalRequestFactory(embeddingPolicy), clock, _options);
    }

    public async Task<MemorySearchResult> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request, out var error)) return SearchFailure(request, error!);

        var authorization = await _authorization.AuthorizeAsync(
            request.Actor, MemoryOperation.Search, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (!authorization.IsAllowed) return SearchFailure(request, authorization.Error!);
        return await _search.ExecuteAsync(request, authorization.AuthorizedScopes!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MemoryContext> BuildContextAsync(MemoryContextRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request, out var error)) return ContextFailure(request, error!);

        var contextAuthorization = await _authorization.AuthorizeAsync(
            request.Actor, MemoryOperation.BuildContext, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (!contextAuthorization.IsAllowed) return ContextFailure(request, contextAuthorization.Error!);

        var hot = await _hotMemoryReader.ReadAsync(
            contextAuthorization.AuthorizedScopes!, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        var search = await SearchAsync(new(
            request.Actor,
            request.RequestedScope,
            request.QueryText,
            request.QueryEmbedding,
            request.Types,
            null,
            request.SearchLimit,
            request.RetrievalConfigurationVersion,
            request.ContractVersion,
            request.QueryVector), cancellationToken).ConfigureAwait(false);
        if (search.Error is not null) return ContextFailure(request, search.Error);
        return await _contextBuilder.BuildAsync(request, hot, search.Hits, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionHotMemory?> ReadHotMemoryAsync(HotMemoryReadRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.ContractVersion != _options.SupportedContractVersion) return null;
        var authorization = await _authorization.AuthorizeAsync(
            request.Actor, MemoryOperation.ReadHotMemory, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        return !authorization.IsAllowed
            ? null
            : await _hotMemoryReader.ReadAsync(authorization.AuthorizedScopes!, request.RequestedScope, cancellationToken)
                .ConfigureAwait(false);
    }

    private bool IsValid(MemorySearchRequest? request, out MemoryError? error)
    {
        error = null;
        if (request is null || request.Limit <= 0)
        {
            error = new(MemoryErrorCode.InvalidArgument, nameof(request));
            return false;
        }
        if (request.ContractVersion != _options.SupportedContractVersion)
        {
            error = new(MemoryErrorCode.UnsupportedContractVersion, nameof(MemorySearchRequest.ContractVersion));
            return false;
        }
        try
        {
            request.RequestedScope.Validate();
            return true;
        }
        catch (ArgumentException)
        {
            error = new(MemoryErrorCode.InvalidArgument, nameof(request.RequestedScope));
            return false;
        }
    }

    private bool IsValid(MemoryContextRequest? request, out MemoryError? error)
    {
        error = null;
        if (request is null || request.TokenBudget <= 0 || request.SearchLimit <= 0)
        {
            error = new(MemoryErrorCode.InvalidArgument, nameof(request));
            return false;
        }
        if (request.ContractVersion != _options.SupportedContractVersion)
        {
            error = new(MemoryErrorCode.UnsupportedContractVersion, nameof(MemoryContextRequest.ContractVersion));
            return false;
        }
        try
        {
            request.RequestedScope.Validate();
            return true;
        }
        catch (ArgumentException)
        {
            error = new(MemoryErrorCode.InvalidArgument, nameof(request.RequestedScope));
            return false;
        }
    }

    private MemorySearchResult SearchFailure(MemorySearchRequest? request, MemoryError error) => new(
        [], error, _options.SupportedContractVersion,
        request?.RetrievalConfigurationVersion ?? _options.SupportedContractVersion);

    private MemoryContext ContextFailure(MemoryContextRequest? request, MemoryError error) => new(
        string.Empty, [], 0, 0, error, _options.SupportedContractVersion,
        request?.RetrievalConfigurationVersion ?? _options.SupportedContractVersion,
        request?.ContextConfigurationVersion ?? _options.SupportedContractVersion);
}
