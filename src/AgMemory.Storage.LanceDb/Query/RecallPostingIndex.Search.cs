using System.Globalization;
using AgMemory.Contracts;
using Microsoft.Data.Sqlite;

namespace AgMemory.Storage.LanceDb;

internal sealed partial class RecallPostingIndex
{
    /// <summary>Ranks matching postings only; BM25 statistics exclude every ineligible type, lifecycle and expired row.</summary>
    public MemoryLexicalRecallPage Search(MemorySearchEligibility eligibility, IReadOnlyList<string> terms, int limit,
        bool bm25, CancellationToken token)
    {
        var predicate = EligiblePredicate(eligibility);
        var placeholders = string.Join(",", Enumerable.Range(0, terms.Count).Select(index => "$t" + index));
        var best = new List<(string Id, double Score)>(limit);
        long documents = 0, tokens = 0;
        var frequencies = new Dictionary<string, long>(StringComparer.Ordinal);
        if (bm25)
        {
            using (var statistics = Command("SELECT count(*),coalesce(sum(d.length),0) FROM documents d WHERE " + predicate))
            {
                using var reader = statistics.ExecuteReader(); reader.Read();
                documents = reader.GetInt64(0); tokens = reader.GetInt64(1);
            }
            using var df = QueryCommand("SELECT p.term,count(*) FROM postings p CROSS JOIN documents d ON d.id=p.id WHERE " +
                predicate + " AND p.term IN (" + placeholders + ") GROUP BY p.term", terms);
            using var counts = df.ExecuteReader();
            while (counts.Read()) frequencies.Add(counts.GetString(0), counts.GetInt64(1));
        }
        long postingRows = 0;
        // CROSS JOIN keeps term-seek postings outermost: without statistics SQLite can otherwise probe every eligible document for rare multi-term queries.
        using (var command = QueryCommand("SELECT p.id,d.length,p.term,p.frequency FROM postings p CROSS JOIN documents d ON d.id=p.id WHERE " +
            predicate + " AND p.term IN (" + placeholders + ") ORDER BY p.id,p.ordinal", terms))
        using (var reader = command.ExecuteReader())
        {
            string? id = null;
            var score = 0d;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested(); postingRows++;
                var next = reader.GetString(0);
                if (id is not null && id != next) { Retain(best, id, bm25 ? score : score / terms.Count, limit); score = 0; }
                id = next;
                if (!bm25) { score++; continue; }
                if (documents == 0 || tokens == 0) continue;
                var length = reader.GetInt32(1);
                var frequency = reader.GetInt32(3);
                var df = frequencies[reader.GetString(2)];
                var normalization = 1.2d * (.25d + .75d * length / ((double)tokens / documents));
                score += Math.Log(1d + (documents - df + .5d) / (df + .5d)) * frequency * 2.2d / (frequency + normalization);
            }
            if (id is not null) Retain(best, id, bm25 ? score : score / terms.Count, limit);
        }
        var items = new List<MemoryLexicalRecallItem>(best.Count);
        var deduplicated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in best)
        {
            token.ThrowIfCancellationRequested();
            using var command = Command("SELECT type,content,importance,confidence,updated,dedup FROM documents WHERE id=$id");
            Bind(command, "$id", candidate.Id);
            using var reader = command.ExecuteReader(); reader.Read();
            if (!deduplicated.Add(reader.GetString(5))) continue;
            items.Add(new(new(candidate.Id), Enum.Parse<MemoryRecordType>(reader.GetString(0)), reader.GetString(1),
                reader.GetDouble(2), reader.GetDouble(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), candidate.Score)
                { DeduplicationKey = reader.GetString(5) });
        }
        return new(items, new(postingRows, 0, best.Count, bm25 ? 3 : 1, 4)
        { Provider = "sqlite-postings-v1", RowCountMeaning = "native_build_rows_plus_matching_postings_excludes_sqlite_internal_metadata_reads" });
    }

    /// <summary>Keeps score-descending, ordinal-ID order while retaining only the requested pre-dedup provider limit.</summary>
    private static void Retain(List<(string Id, double Score)> best, string id, double score, int limit)
    {
        var position = 0;
        while (position < best.Count && (best[position].Score > score || best[position].Score == score && string.CompareOrdinal(best[position].Id, id) < 0)) position++;
        if (position >= limit) return;
        if (best.Count == limit) best.RemoveAt(best.Count - 1);
        best.Insert(position, (id, score));
    }

    /// <summary>Quotes fixed enum/time values only; query terms are always parameters.</summary>
    private static string EligiblePredicate(MemorySearchEligibility eligibility)
    {
        var status = eligibility.RequiredLifecycleStatus.ToString().Replace("'", "''", StringComparison.Ordinal);
        var now = eligibility.AsOfUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var predicate = $"d.status='{status}' AND (d.expires IS NULL OR d.expires>'{now}')";
        if (eligibility.Types is { Count: > 0 })
            predicate += " AND d.type IN (" + string.Join(",", eligibility.Types.Select(type => "'" + type.ToString().Replace("'", "''", StringComparison.Ordinal) + "'")) + ")";
        return predicate;
    }

    private SqliteCommand QueryCommand(string sql, IReadOnlyList<string> terms)
    {
        var command = Command(sql);
        for (var index = 0; index < terms.Count; index++) Bind(command, "$t" + index, terms[index]);
        return command;
    }
}
