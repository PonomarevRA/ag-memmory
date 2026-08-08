using AgMemory.Contracts;

namespace AgMemory.Core;

internal sealed class HybridSearchExecutor(
    ILexicalSearch lexical,
    IVectorSearch vector,
    IMemoryGraph? graph,
    RetrievalRequestFactory requestFactory,
    IClock clock,
    MemoryCoreOptions options)
{
    public async Task<MemorySearchResult> ExecuteAsync(
        MemorySearchRequest request,
        AuthorizedScopeSet authorizedScopes,
        CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var prepared = await requestFactory.CreateAsync(request, authorizedScopes, now, cancellationToken).ConfigureAwait(false);

        // Start both ports before observing either result so an optional semantic source cannot block lexical recall.
        var lexicalTask = StartSearch(() => lexical.SearchAsync(prepared.PortRequest, cancellationToken));
        var vectorTask = StartSearch(() => vector.SearchAsync(prepared.PortRequest, cancellationToken));
        await ObserveBothAsync(lexicalTask, vectorTask, cancellationToken).ConfigureAwait(false);

        var lexicalOutcome = await ReadOutcomeAsync(lexicalTask, cancellationToken).ConfigureAwait(false);
        var vectorOutcome = await ReadOutcomeAsync(vectorTask, cancellationToken).ConfigureAwait(false);
        var vectorStatus = prepared.VectorStatus == RetrievalSourceStatus.Completed
            ? vectorOutcome.Status
            : prepared.VectorStatus;
        var execution = new RetrievalExecution(lexicalOutcome.Status, vectorStatus, RetrievalSourceStatus.NotRequested);
        var vectorCandidates = prepared.VectorStatus == RetrievalSourceStatus.Completed
            ? vectorOutcome.Candidates
            : [];

        if (lexicalOutcome.Status != RetrievalSourceStatus.Completed && vectorStatus != RetrievalSourceStatus.Completed)
            return Failure(request, execution);

        IReadOnlyList<MemorySearchHit> fused;
        try
        {
            fused = Fuse(lexicalOutcome.Candidates, vectorCandidates, authorizedScopes, now, request);
        }
        catch (ArgumentException)
        {
            return Failure(request, execution);
        }

        if (graph is null || fused.Count == 0)
            return Success(fused, request, execution);

        try
        {
            var graphResults = await graph.RerankAsync(
                new(prepared.Eligibility, fused.Select(hit => hit.Record).ToArray()), cancellationToken).ConfigureAwait(false);
            var graphScores = graphResults
                .Where(result => double.IsFinite(result.Score) && fused.Any(hit => hit.Record.Id == result.MemoryId))
                .GroupBy(result => result.MemoryId)
                .ToDictionary(group => group.Key, group => group.Max(result => result.Score));
            var reranked = Fuse(lexicalOutcome.Candidates, vectorCandidates, authorizedScopes, now, request, graphScores);
            return Success(reranked, request, execution with { Graph = RetrievalSourceStatus.Completed });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Success(fused, request, execution with { Graph = RetrievalSourceStatus.Unavailable });
        }
    }

    private IReadOnlyList<MemorySearchHit> Fuse(
        IReadOnlyList<SearchPortCandidate> lexicalCandidates,
        IReadOnlyList<SearchPortCandidate> vectorCandidates,
        AuthorizedScopeSet authorizedScopes,
        DateTimeOffset now,
        MemorySearchRequest request,
        IReadOnlyDictionary<MemoryId, double>? graphScores = null) =>
        DeterministicRetrieval.Fuse(
            lexicalCandidates,
            vectorCandidates,
            authorizedScopes,
            now,
            request.Types,
            request.Statuses,
            request.Limit,
            options.ReciprocalRankConstant,
            request.RetrievalConfigurationVersion,
            options.RerankerConfigurationVersion,
            graphScores);

    private MemorySearchResult Success(
        IReadOnlyList<MemorySearchHit> hits,
        MemorySearchRequest request,
        RetrievalExecution execution) =>
        new(hits, null, options.SupportedContractVersion, request.RetrievalConfigurationVersion, execution);

    private MemorySearchResult Failure(MemorySearchRequest request, RetrievalExecution execution) =>
        new([], new(MemoryErrorCode.DependencyFailure, null), options.SupportedContractVersion,
            request.RetrievalConfigurationVersion, execution);

    private DateTimeOffset UtcNow()
    {
        var now = clock.UtcNow;
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }

    private static Task<IReadOnlyList<SearchPortCandidate>> StartSearch(
        Func<Task<IReadOnlyList<SearchPortCandidate>>> start)
    {
        try
        {
            return start();
        }
        catch (Exception error)
        {
            return Task.FromException<IReadOnlyList<SearchPortCandidate>>(error);
        }
    }

    private static async Task ObserveBothAsync(
        Task<IReadOnlyList<SearchPortCandidate>> lexicalTask,
        Task<IReadOnlyList<SearchPortCandidate>> vectorTask,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(lexicalTask, vectorTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Outcomes below retain successful candidates and mark only the failed source unavailable.
        }
    }

    private static async Task<SearchPortOutcome> ReadOutcomeAsync(
        Task<IReadOnlyList<SearchPortCandidate>> task,
        CancellationToken cancellationToken)
    {
        try
        {
            return new(await task.ConfigureAwait(false), RetrievalSourceStatus.Completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new([], RetrievalSourceStatus.Unavailable);
        }
    }

    private sealed record SearchPortOutcome(
        IReadOnlyList<SearchPortCandidate> Candidates,
        RetrievalSourceStatus Status);
}
