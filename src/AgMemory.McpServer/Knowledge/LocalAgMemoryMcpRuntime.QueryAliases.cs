using AgMemory.Contracts;
using AgMemory.Core;

namespace AgMemory.McpServer;

public sealed partial class LocalAgMemoryMcpRuntime
{
    /// <summary>Fuses at most four exact-scoped lexical queries (256 provider candidates) into a bounded final pool of at most64.</summary>
    private async Task<MemoryLexicalRecallPage> SearchWithAliasesAsync(MemorySearchEligibility eligibility,
        string query, int limit, CancellationToken cancellationToken)
    {
        var expansions = _configuration.UseQueryAliases ? RecallQueryAliases.Expand(query) : [];
        var original = await SearchRecallChannelAsync(eligibility, query, limit, cancellationToken).ConfigureAwait(false);
        if (expansions.Count == 0) return original;

        var candidates = new Dictionary<string, (MemoryLexicalRecallItem Item, double Score)>(StringComparer.Ordinal);
        AddRanks(original.Items, 1d, candidates);
        var work = original.Work;
        foreach (var expansion in expansions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await SearchRecallChannelAsync(eligibility, expansion.Query, limit, cancellationToken).ConfigureAwait(false);
            AddRanks(page.Items, expansion.Weight, candidates);
            work = new(work.RowsScanned + page.Work.RowsScanned, Math.Max(work.MaximumBatchRows, page.Work.MaximumBatchRows),
                candidates.Count, work.QueryExecutions + page.Work.QueryExecutions, Math.Max(work.SelectedColumnCount, page.Work.SelectedColumnCount))
            { Provider = "bounded_lexical_alias_fusion", RowCountMeaning = "sum_provider_materialized_rows_excludes_sqlite_internal_metadata_reads",
              IndexRebuilt = work.IndexRebuilt || page.Work.IndexRebuilt };
        }
        var ranked = candidates.Values.ToList();
        ranked.Sort((left, right) =>
        {
            var scoreOrder = right.Score.CompareTo(left.Score);
            return scoreOrder != 0 ? scoreOrder : string.CompareOrdinal(left.Item.Id.Value, right.Item.Id.Value);
        });
        var selected = new List<MemoryLexicalRecallItem>(limit);
        var deduplicated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in ranked)
        {
            // Distinct IDs can describe the same logical record across separate query channels.
            if (!deduplicated.Add(candidate.Item.DeduplicationKey ?? candidate.Item.Id.Value)) continue;
            selected.Add(candidate.Item with { Score = candidate.Score });
            if (selected.Count == limit) break;
        }
        return new(selected, work);
    }

    /// <summary>Retains an explicit scan mode for parity/load checks and operator rollback; both providers enforce the same eligibility.</summary>
    private Task<MemoryLexicalRecallPage> SearchRecallChannelAsync(MemorySearchEligibility eligibility, string query,
        int limit, CancellationToken token) => _configuration.UseRecallIndex
            ? _store.SearchIndexedRecallCandidatesAsync(eligibility, query, limit, _configuration.UseBm25Recall, token)
            : _store.SearchRecallCandidatesAsync(eligibility, query, limit, _configuration.UseBm25Recall, token);

    /// <summary>Reciprocal rank fusion compares provider ranks, avoiding incompatible overlap/BM25 score scales.</summary>
    private static void AddRanks(IReadOnlyList<MemoryLexicalRecallItem> items, double weight,
        Dictionary<string, (MemoryLexicalRecallItem Item, double Score)> candidates)
    {
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var contribution = weight / (60d + index + 1);
            if (candidates.TryGetValue(item.Id.Value, out var previous))
                candidates[item.Id.Value] = (item.UpdatedAt >= previous.Item.UpdatedAt ? item : previous.Item, previous.Score + contribution);
            else
                candidates.Add(item.Id.Value, (item, contribution));
        }
    }
}
