using AgMemory.Contracts;

namespace AgMemory.Core;

internal sealed class SummaryFallbackReader(
    IMemoryStore store,
    IAuthorizationScopeValidator authorization,
    IClock clock)
{
    public async Task<SummaryFallbackResult> ReadAsync(
        MemoryContextRequest request,
        CancellationToken cancellationToken)
    {
        var scopeAuthorization = await authorization.AuthorizeAsync(
            request.Actor, MemoryOperation.BuildContext, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (!scopeAuthorization.IsAllowed) return new(null, scopeAuthorization.Error);

        var now = UtcNow();
        var summary = (await store.ListAsync(scopeAuthorization.AuthorizedScopes!, cancellationToken).ConfigureAwait(false))
            .Where(record => record.Type == MemoryRecordType.Summary &&
                DeterministicRetrieval.IsEligible(record, scopeAuthorization.AuthorizedScopes!, now, request.Types))
            .OrderBy(record => record.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        return new(summary, null);
    }

    private DateTimeOffset UtcNow()
    {
        var now = clock.UtcNow;
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }
}

internal sealed record SummaryFallbackResult(MemoryRecord? Summary, MemoryError? Error);
