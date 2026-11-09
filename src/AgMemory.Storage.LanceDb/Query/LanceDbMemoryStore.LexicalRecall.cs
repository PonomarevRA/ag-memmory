using System.Globalization;
using AgMemory.Contracts;
using Apache.Arrow;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    private const int LexicalRecallBatchRows = 256;
    private static readonly string[] LexicalRecallColumns =
        ["id", "record_type", "canonical_text", "importance", "confidence", "updated_at_utc", "deduplication_key"];

    /// <summary>
    /// Streams the exact active, unexpired scope through the existing lexical scorer, retaining at most ten candidates.
    /// Work remains one O(N) eligible-row scan; memory is one capped Arrow batch, tokenization of one row and the top limit.
    /// Lexical score then ordinal ID determine rank; importance, confidence and time remain returned metadata, not boosts.
    /// </summary>
    public async Task<MemoryLexicalRecallPage> SearchLexicalRecallAsync(
        MemorySearchEligibility eligibility,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(limit));
        return await SearchRecallCandidatesAsync(eligibility, query, limit, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Selects up to 64 internal candidates before response packing. Legacy overlap ranking remains one scan;
    /// opt-in BM25 uses two bounded scans of the same pinned table version and exact eligible corpus.
    /// No embeddings, source JSON or foreign-scope text are materialized.
    /// </summary>
    public async Task<MemoryLexicalRecallPage> SearchRecallCandidatesAsync(
        MemorySearchEligibility eligibility, string query, int limit, bool useBm25,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        if (eligibility.AuthorizedScopes.Selectors.Count != 1)
            throw new ArgumentException("Local lexical recall requires one exact authorized scope.", nameof(eligibility));
        eligibility.AuthorizedScopes.Selectors[0].Scope.Validate();
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("A recall query is required.", nameof(query));
        if (limit is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var terms = Tokenize(query);
        if (terms.Count == 0 || eligibility.Types is { Count: 0 })
            return new([], new(0, 0, 0, 0, LexicalRecallColumns.Length));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
            // A second pass must describe the same corpus even if another client writes meanwhile.
            Bm25RecallStatistics? statistics = null;
            if (useBm25)
            {
                await table.Checkout(await table.Version().ConfigureAwait(false)).ConfigureAwait(false);
                statistics = await ReadRecallStatisticsAsync(table, eligibility, terms, cancellationToken).ConfigureAwait(false);
            }
            using var scan = table.Query().Select(LexicalRecallColumns).Where(BuildEligibilityPredicate(eligibility));
            using var reader = await scan.ToBatches(maxBatchLength: LexicalRecallBatchRows).ConfigureAwait(false);
            var best = new List<LexicalRecallCandidate>(limit);
            long rowsScanned = 0;
            var maximumBatchRows = 0;
            await foreach (var batch in reader.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                using (batch)
                {
                    maximumBatchRows = Math.Max(maximumBatchRows, batch.Length);
                    for (var row = 0; row < batch.Length; row++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        rowsScanned++;
                        RetainLexicalRecallCandidate(batch, row, terms, limit, best, statistics);
                    }
                }
            }

            // Core's lexical-only RRF is strictly monotonic in provider rank. It deduplicates only after the
            // lexical provider has applied its limit; keeping that order preserves even duplicate-key behavior.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var items = new List<MemoryLexicalRecallItem>(best.Count);
            foreach (var candidate in best)
            {
                if (seen.Add(candidate.DeduplicationKey)) items.Add(candidate.Item);
            }
            return new(items, new(rowsScanned + (statistics?.Documents ?? 0), maximumBatchRows, best.Count,
                useBm25 ? 2 : 1, LexicalRecallColumns.Length));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Inserts a matching row in score/ID order without constructing metadata for candidates below the limit.</summary>
    private static void RetainLexicalRecallCandidate(
        RecordBatch batch,
        int row,
        IReadOnlyList<string> terms,
        int limit,
        List<LexicalRecallCandidate> best,
        Bm25RecallStatistics? statistics)
    {
        var content = Required(batch, "canonical_text", row);
        var score = statistics is null ? LexicalScore(content, terms) : statistics.Score(content);
        if (score <= 0) return;
        var id = Required(batch, "id", row);
        var position = 0;
        while (position < best.Count)
        {
            var current = best[position].Item;
            if (score > current.Score || score == current.Score && string.CompareOrdinal(id, current.Id.Value) < 0) break;
            position++;
        }
        if (position >= limit) return;
        var item = ReadRecallProjection(batch, row, score);
        var candidate = new LexicalRecallCandidate(item, item.DeduplicationKey!);
        // Remove before inserting so even the temporary candidate collection never exceeds the requested limit.
        if (best.Count == limit) best.RemoveAt(best.Count - 1);
        best.Insert(position, candidate);
    }

    /// <summary>Validates the allowlisted canonical projection shared by streaming recall and index candidate verification.</summary>
    private static MemoryLexicalRecallItem ReadRecallProjection(RecordBatch batch, int row, double score)
    {
        var importance = double.Parse(Required(batch, "importance", row), CultureInfo.InvariantCulture);
        var confidence = double.Parse(Required(batch, "confidence", row), CultureInfo.InvariantCulture);
        var type = ParseEnum<MemoryRecordType>(Required(batch, "record_type", row));
        if (!Enum.IsDefined(type) || !double.IsFinite(importance) || importance is < 0 or > 1 ||
            !double.IsFinite(confidence) || confidence is < 0 or > 1)
            throw new InvalidDataException("The lexical recall projection contains invalid metadata.");
        var memoryId = new MemoryId(Required(batch, "id", row));
        memoryId.Validate(nameof(memoryId));
        return new MemoryLexicalRecallItem(memoryId, type, Required(batch, "canonical_text", row), importance, confidence,
            ParseUtc(Required(batch, "updated_at_utc", row)), score)
        { DeduplicationKey = Required(batch, "deduplication_key", row) };
    }

    private sealed record LexicalRecallCandidate(MemoryLexicalRecallItem Item, string DeduplicationKey);
}
