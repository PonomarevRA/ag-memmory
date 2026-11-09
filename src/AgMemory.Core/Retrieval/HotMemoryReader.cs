using AgMemory.Contracts;

namespace AgMemory.Core;

internal sealed class HotMemoryReader(IMemoryStore store, IClock clock)
{
    public async Task<SessionHotMemory?> ReadAsync(
        AuthorizedScopeSet authorizedScopes,
        MemoryScope requestedScope,
        CancellationToken cancellationToken)
    {
        if (!authorizedScopes.Contains(requestedScope)) return null;
        var memory = await store.GetHotMemoryAsync(authorizedScopes, requestedScope, cancellationToken).ConfigureAwait(false);
        return memory is not null && memory.Scope == requestedScope && memory.ExpiresAt > UtcNow() ? memory : null;
    }

    private DateTimeOffset UtcNow()
    {
        var now = clock.UtcNow;
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }
}
