using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    /// <summary>Exposes cumulative wait/hold measurements without making gate ownership externally controllable.</summary>
    public StorageGateStatistics GateStatistics => _gate.Statistics;

    /// <summary>Retains the highest score then lowest ordinal IDs while scanning only id/text; hydrates at most the requested limit.</summary>
    private async Task<IReadOnlyList<SearchPortCandidate>> SearchGenericLexicalCoreAsync(
        SearchPortRequest request, IReadOnlyList<string> terms, CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
        using var scan = table.Query().Select(["id", "canonical_text"]).Where(BuildEligibilityPredicate(request.Eligibility));
        using var reader = await scan.ToBatches(maxBatchLength: LexicalRecallBatchRows).ConfigureAwait(false);
        var best = new SortedSet<LexicalRank>(LexicalRankComparer.Instance);
        await foreach (var batch in reader.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            using (batch)
            {
                for (var row = 0; row < batch.Length; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref _lexicalRowsScanned);
                    var score = LexicalScore(Required(batch, "canonical_text", row), terms);
                    if (score <= 0) continue;
                    var candidate = new LexicalRank(Required(batch, "id", row), score);
                    if (best.Count == request.Limit && LexicalRankComparer.Instance.Compare(candidate, best.Min!) <= 0) continue;
                    best.Add(candidate);
                    if (best.Count > request.Limit) best.Remove(best.Min!);
                }
            }
        }
        if (best.Count == 0) return [];
        var records = new Dictionary<string, MemoryRecord>(StringComparer.Ordinal);
        foreach (var portion in best.Chunk(LexicalRecallBatchRows))
        {
            var ids = string.Join(", ", portion.Select(candidate => Literal(candidate.Id)));
            var predicate = $"({BuildEligibilityPredicate(request.Eligibility)}) AND id IN ({ids})";
            foreach (var record in await ReadRecordsAsync(predicate, cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _lexicalRecordsHydrated);
                if (request.Eligibility.IsEligible(record)) records.Add(record.Id.Value, record);
            }
        }
        var result = new List<SearchPortCandidate>(best.Count);
        foreach (var candidate in best.Reverse())
        {
            if (!records.TryGetValue(candidate.Id, out var record)) continue;
            result.Add(new(record, result.Count + 1, candidate.Score, record.Embedding));
        }
        return result;
    }

    private sealed record LexicalRank(string Id, double Score);

    /// <summary>Orders the worst retained rank first; ordinal ID breaks equal lexical scores deterministically.</summary>
    private sealed class LexicalRankComparer : IComparer<LexicalRank>
    {
        public static readonly LexicalRankComparer Instance = new();
        public int Compare(LexicalRank? left, LexicalRank? right)
        {
            var score = left!.Score.CompareTo(right!.Score);
            return score != 0 ? score : string.CompareOrdinal(right.Id, left.Id);
        }
    }
}
