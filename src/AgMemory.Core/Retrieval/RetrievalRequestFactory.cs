using AgMemory.Contracts;

namespace AgMemory.Core;

internal sealed class RetrievalRequestFactory(IEmbeddingPolicy embeddingPolicy)
{
    public async Task<PreparedSearchRequest> CreateAsync(
        MemorySearchRequest request,
        AuthorizedScopeSet authorizedScopes,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var eligibility = new MemorySearchEligibility(authorizedScopes, request.Types, asOfUtc);
        var semantic = await ResolveSemanticQueryAsync(request, cancellationToken).ConfigureAwait(false);
        return new(eligibility, new(
            eligibility,
            request.QueryText,
            semantic.Vector,
            semantic.Embedding,
            semantic.Contract,
            request.Limit), semantic.Status);
    }

    private async Task<SemanticQuery> ResolveSemanticQueryAsync(
        MemorySearchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.QueryVector is not { } vector) return SemanticQuery.NotRequested;
        if (request.QueryEmbedding is null || !IsFinite(vector)) return SemanticQuery.Unavailable;

        try
        {
            request.QueryEmbedding.Validate();
            var policy = await embeddingPolicy.GetAsync(request.RequestedScope, cancellationToken).ConfigureAwait(false);
            if (!policy.IsConfigured || policy.Contract is null) return SemanticQuery.Unavailable;
            policy.Contract.Validate();
            if (!policy.Contract.Matches(request.QueryEmbedding) || vector.Length != policy.Contract.Dimension)
                return SemanticQuery.Unavailable;
            return new(vector, request.QueryEmbedding, policy.Contract, RetrievalSourceStatus.Completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return SemanticQuery.Unavailable;
        }
        catch
        {
            return SemanticQuery.Unavailable;
        }
    }

    private static bool IsFinite(ReadOnlyMemory<float> vector) =>
        vector.Span.ToArray().All(float.IsFinite);

    private sealed record SemanticQuery(
        ReadOnlyMemory<float>? Vector,
        EmbeddingReference? Embedding,
        EmbeddingContract? Contract,
        RetrievalSourceStatus Status)
    {
        public static SemanticQuery NotRequested { get; } = new(null, null, null, RetrievalSourceStatus.NotRequested);
        public static SemanticQuery Unavailable { get; } = new(null, null, null, RetrievalSourceStatus.Unavailable);
    }
}

internal sealed record PreparedSearchRequest(
    MemorySearchEligibility Eligibility,
    SearchPortRequest PortRequest,
    RetrievalSourceStatus VectorStatus);
