using AgMemory.Contracts;
using lancedb;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    /// <summary>Counts only eligible documents and query-term frequencies; memory is bounded by the query, not corpus text.</summary>
    private static async Task<Bm25RecallStatistics> ReadRecallStatisticsAsync(Table table,
        MemorySearchEligibility eligibility, IReadOnlyList<string> terms, CancellationToken cancellationToken)
    {
        var statistics = new Bm25RecallStatistics(terms);
        using var scan = table.Query().Select(["canonical_text"]).Where(BuildEligibilityPredicate(eligibility));
        using var reader = await scan.ToBatches(maxBatchLength: LexicalRecallBatchRows).ConfigureAwait(false);
        await foreach (var batch in reader.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            using (batch)
            {
                for (var row = 0; row < batch.Length; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    statistics.Observe(Required(batch, "canonical_text", row));
                }
            }
        }
        return statistics;
    }

    /// <summary>Standard BM25 (k1=1.2, b=.75) with the existing whole-token normalization and positive IDF.</summary>
    private sealed class Bm25RecallStatistics(IReadOnlyList<string> terms)
    {
        private readonly Dictionary<string, long> _documentFrequency = terms.ToDictionary(term => term, _ => 0L, StringComparer.Ordinal);
        private long _tokens;
        public long Documents { get; private set; }

        /// <summary>Collects document length and one document-frequency observation per matching query term.</summary>
        public void Observe(string content)
        {
            var frequencies = Frequencies(content, out var length);
            Documents++;
            _tokens += length;
            foreach (var term in frequencies.Keys) _documentFrequency[term]++;
        }

        /// <summary>Scores a document using statistics from its pinned authorized corpus; no match returns zero.</summary>
        public double Score(string content)
        {
            if (Documents == 0 || _tokens == 0) return 0d;
            var frequencies = Frequencies(content, out var length);
            var averageLength = (double)_tokens / Documents;
            var normalization = 1.2d * (.25d + .75d * length / averageLength);
            var score = 0d;
            foreach (var (term, frequency) in frequencies)
            {
                var documents = _documentFrequency[term];
                var inverseFrequency = Math.Log(1d + (Documents - documents + .5d) / (documents + .5d));
                score += inverseFrequency * frequency * 2.2d / (frequency + normalization);
            }
            return score;
        }

        /// <summary>Counts query terms only while retaining the complete normalized document length.</summary>
        private Dictionary<string, int> Frequencies(string content, out int length)
        {
            var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            length = 0;
            foreach (var range in content.AsSpan().SplitAny(TokenDelimiters))
            {
                var token = content.AsSpan()[range].Trim();
                if (token.IsEmpty) continue;
                length++;
                var normalized = token.ToString().ToUpperInvariant();
                if (_documentFrequency.ContainsKey(normalized))
                    frequencies[normalized] = frequencies.GetValueOrDefault(normalized) + 1;
            }
            return frequencies;
        }
    }
}
