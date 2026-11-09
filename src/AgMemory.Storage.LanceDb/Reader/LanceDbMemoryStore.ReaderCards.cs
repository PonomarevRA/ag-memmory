using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore : IMemoryReaderCardSource
{
    /// <summary>Resolves one route batch, then reads current eligible text with one selected-column query under the same store gate.</summary>
    public async Task<IReadOnlyList<MemoryReaderCardSource>> ReadCardsAsync(
        MemorySearchEligibility eligibility, IReadOnlyList<string> routeKeys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(routeKeys);
        if (eligibility.AuthorizedScopes.Selectors.Count != 1 || routeKeys.Count is < 1 or > MemoryReaderCardLimits.MaximumCards ||
            routeKeys.Any(key => !MemoryReaderCardLimits.IsRouteKey(key))) return [];
        var keys = routeKeys.Distinct(StringComparer.Ordinal).ToArray();
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            using var routes = await OpenTableAsync(ReaderRoutesTable, cancellationToken).ConfigureAwait(false);
            var predicate = $"route_key IN ({string.Join(", ", keys.Select(Literal))})";
            using var batches = await routes.Query().Where(predicate).Limit(keys.Length + 1).ToArrow().ConfigureAwait(false);
            var mappings = ReadReaderRouteRows(batches).ToArray();
            var requested = keys.ToHashSet(StringComparer.Ordinal);
            var seenRoutes = new HashSet<string>(StringComparer.Ordinal);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapping in mappings)
            {
                if (!requested.Contains(mapping.RouteKey) || !seenRoutes.Add(mapping.RouteKey) || !seenIds.Add(mapping.MemoryId))
                    throw new InvalidDataException("The reader route batch is ambiguous.");
            }
            var sources = await ReadEligibleWikiSourcesAsync(eligibility, seenIds, cancellationToken, includePreview: true).ConfigureAwait(false);
            var result = new List<MemoryReaderCardSource>(mappings.Length);
            foreach (var mapping in mappings)
            {
                if (sources.TryGetValue(mapping.MemoryId, out var source))
                    result.Add(new(mapping.RouteKey, source.Type, source.Version, source.CanonicalText));
            }
            return result;
        }
        finally { _gate.Release(); }
    }
}
