using AgMemory.Contracts;

namespace AgMemory.McpServer;

public sealed partial class LocalAgMemoryMcpRuntime
{
    /// <summary>Validates query fan-out and removes duplicate facets, preserving client order.</summary>
    private static IReadOnlyList<string> ValidateTaskQueryFacets(string query, string[]? queryFacets)
    {
        if (queryFacets is null || queryFacets.Length == 0) return [];
        if (queryFacets.Length > 3 || string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Facets require a primary query and at most three focused terms.");
        // The alias dictionary distinguishes acronyms such as AFTER from ordinary words.
        var seen = new HashSet<string>(StringComparer.Ordinal) { query.Trim() };
        var facets = new List<string>(queryFacets.Length);
        foreach (var facet in queryFacets)
        {
            ValidateAgentText(facet, 160);
            var normalized = facet.Trim();
            if (seen.Add(normalized)) facets.Add(normalized);
        }
        return facets;
    }

    /// <summary>Alternates ranked hits across facets and the original query before current-source hydration.</summary>
    private async Task<IReadOnlyList<MemoryLexicalRecallItem>> SearchTaskContextFacetsAsync(
        MemorySearchEligibility eligibility, string query, IReadOnlyList<string> facets,
        CancellationToken cancellationToken)
    {
        const int poolSize = 64;
        var channels = new List<IReadOnlyList<MemoryLexicalRecallItem>>(facets.Count + 1);
        foreach (var facet in facets)
            channels.Add((await SearchAgentCandidatesAsync(eligibility, facet, poolSize, cancellationToken)
                .ConfigureAwait(false)).Items);
        channels.Add((await SearchAgentCandidatesAsync(eligibility, query, poolSize, cancellationToken)
            .ConfigureAwait(false)).Items);

        var ranked = new List<MemoryLexicalRecallItem>(poolSize);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var longest = channels.Max(channel => channel.Count);
        for (var rank = 0; rank < longest && ranked.Count < poolSize; rank++)
        {
            foreach (var channel in channels)
            {
                if (rank >= channel.Count) continue;
                var hit = channel[rank];
                if (!ids.Add(hit.Id.Value) || !keys.Add(hit.DeduplicationKey ?? hit.Id.Value)) continue;
                ranked.Add(hit);
                if (ranked.Count == poolSize) break;
            }
        }
        return ranked;
    }
}
