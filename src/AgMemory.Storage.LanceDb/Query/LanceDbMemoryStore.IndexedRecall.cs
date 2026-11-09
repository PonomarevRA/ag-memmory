using System.Globalization;
using AgMemory.Contracts;
using lancedb;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    /// <summary>
    /// Uses a derived exact-scope posting index, rebuilt from a pinned native generation. Warm queries tokenize only the query;
    /// expiry/types/lifecycle remain query-time filters. At cache bounds or cache I/O failure, the existing bounded scan remains available.
    /// </summary>
    public async Task<MemoryLexicalRecallPage> SearchIndexedRecallCandidatesAsync(MemorySearchEligibility eligibility,
        string query, int limit, bool useBm25, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        if (eligibility.AuthorizedScopes.Selectors.Count != 1 || string.IsNullOrWhiteSpace(query) || limit is < 1 or > 64)
            throw new ArgumentException("Indexed recall requires one exact scope, a query and a bounded limit.");
        var scope = eligibility.AuthorizedScopes.Selectors[0].Scope;
        scope.Validate();
        var terms = Tokenize(query);
        if (terms.Count == 0 || eligibility.Types is { Count: 0 }) return new([], new(0, 0, 0, 0, 0));
        if (terms.Count > 128) return await SearchRecallCandidatesAsync(eligibility, query, limit, useBm25, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
            var version = await table.Version().ConfigureAwait(false);
            await table.Checkout(version).ConfigureAwait(false);
            using var index = new RecallPostingIndex(_options.StoragePath, Hash(ScopeKey(scope)));
            long rows = 0;
            var maximumBatch = 0;
            var rebuilt = index.BeginRebuild(version.ToString(CultureInfo.InvariantCulture));
            if (rebuilt)
            {
                if (await table.CountRows(BuildAuthorizedPredicate(eligibility.AuthorizedScopes)).ConfigureAwait(false) > 100_000)
                    throw new IOException("Recall index row capacity reached.");
                string[] columns = [..LexicalRecallColumns, "status", "expires_at_utc"];
                using var scan = table.Query().Select(columns).Where(BuildAuthorizedPredicate(eligibility.AuthorizedScopes));
                using var reader = await scan.ToBatches(maxBatchLength: LexicalRecallBatchRows).ConfigureAwait(false);
                await foreach (var batch in reader.WithCancellation(cancellationToken).ConfigureAwait(false))
                using (batch)
                {
                    maximumBatch = Math.Max(maximumBatch, batch.Length);
                    for (var row = 0; row < batch.Length; row++)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); rows++;
                        index.Add(Required(batch, "id", row), Required(batch, "record_type", row), Required(batch, "canonical_text", row),
                            double.Parse(Required(batch, "importance", row), CultureInfo.InvariantCulture),
                            double.Parse(Required(batch, "confidence", row), CultureInfo.InvariantCulture), Required(batch, "updated_at_utc", row),
                            Required(batch, "deduplication_key", row), Required(batch, "status", row), Value(batch, "expires_at_utc", row), TokenDelimiters);
                    }
                }
                index.CompleteRebuild(version.ToString(CultureInfo.InvariantCulture));
            }
            var result = index.Search(eligibility, terms, limit, useBm25, cancellationToken);
            result = await VerifyIndexedCandidatesAsync(table, eligibility, result, cancellationToken).ConfigureAwait(false);
            return result with { Work = result.Work with { RowsScanned = result.Work.RowsScanned + rows,
                MaximumBatchRows = Math.Max(maximumBatch, result.Work.MaximumBatchRows),
                QueryExecutions = result.Work.QueryExecutions + (rebuilt ? 1 : 0), IndexRebuilt = rebuilt } };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Cache availability cannot disable canonical search. The fallback runs after releasing the native lease below.
        }
        finally { _gate.Release(); }
        return await SearchRecallCandidatesAsync(eligibility, query, limit, useBm25, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A derived cache is never an authority boundary: re-read at most64 allowlisted canonical rows under the exact eligibility
    /// and pinned native generation. Wrong-scope, missing or changed cache candidates cannot reach even legacy recall.
    /// </summary>
    private static async Task<MemoryLexicalRecallPage> VerifyIndexedCandidatesAsync(Table table,
        MemorySearchEligibility eligibility, MemoryLexicalRecallPage page, CancellationToken token)
    {
        if (page.Items.Count == 0) return page;
        var candidates = new Dictionary<string, MemoryLexicalRecallItem>(StringComparer.Ordinal);
        foreach (var candidate in page.Items) candidates[candidate.Id.Value] = candidate;
        var ids = string.Join(",", candidates.Keys.Select(Literal));
        using var scan = table.Query().Select(LexicalRecallColumns).Where($"({BuildEligibilityPredicate(eligibility)}) AND id IN ({ids})");
        using var reader = await scan.ToBatches(maxBatchLength: 64).ConfigureAwait(false);
        var current = new Dictionary<string, MemoryLexicalRecallItem>(StringComparer.Ordinal);
        long rows = 0;
        var maximumBatch = 0;
        await foreach (var batch in reader.WithCancellation(token).ConfigureAwait(false))
        using (batch)
        {
            maximumBatch = Math.Max(maximumBatch, batch.Length);
            for (var row = 0; row < batch.Length; row++)
            {
                token.ThrowIfCancellationRequested(); rows++;
                var id = Required(batch, "id", row);
                if (!candidates.TryGetValue(id, out var candidate) || candidate.Content != Required(batch, "canonical_text", row)) continue;
                current[id] = ReadRecallProjection(batch, row, candidate.Score);
            }
        }
        var items = new List<MemoryLexicalRecallItem>(current.Count);
        foreach (var candidate in page.Items) if (current.TryGetValue(candidate.Id.Value, out var source)) items.Add(source);
        return new(items, page.Work with { RowsScanned = page.Work.RowsScanned + rows, MaximumBatchRows = maximumBatch,
            QueryExecutions = page.Work.QueryExecutions + 1, SelectedColumnCount = LexicalRecallColumns.Length });
    }
}
