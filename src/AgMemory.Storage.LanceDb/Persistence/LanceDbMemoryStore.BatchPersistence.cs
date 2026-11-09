using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    private const int PersistenceBatchRows = 256;

    /// <summary>Loads existing snapshots with bounded IN predicates; absent IDs are cached too, preserving repeated-ID CAS semantics.</summary>
    private async Task PrefetchRecordSnapshotsAsync(
        IReadOnlyList<ConditionalRecordWrite> writes, Dictionary<MemoryId, MemoryRecord?> snapshots,
        Dictionary<MemoryId, MemoryRecord?> changes, CancellationToken cancellationToken)
    {
        var pending = new HashSet<MemoryId>();
        foreach (var write in writes)
            if (write.Record is not null && !snapshots.ContainsKey(write.Record.Id) && !changes.ContainsKey(write.Record.Id)) pending.Add(write.Record.Id);
        foreach (var portion in pending.Chunk(PersistenceBatchRows))
        {
            var ids = string.Join(", ", portion.Select(id => Literal(id.Value)));
            Interlocked.Increment(ref _snapshotQueries);
            var records = await ReadRecordsAsync($"id IN ({ids})", cancellationToken).ConfigureAwait(false);
            foreach (var id in portion) snapshots[id] = null;
            foreach (var record in records)
            {
                if (snapshots[record.Id] is not null) throw new InvalidDataException("Duplicate record IDs prevent conditional writes.");
                snapshots[record.Id] = record;
            }
        }
    }

    /// <summary>
    /// Persists final staged record changes in bounded native batches and invalidates each affected scope once.
    /// Records, vectors and generation pointers remain separate native commits; failures do not imply rollback.
    /// </summary>
    private async Task PersistRecordChangesAsync(
        Dictionary<MemoryId, MemoryRecord?> changes, Dictionary<MemoryId, MemoryRecord?> snapshots,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0) return;
        AdvanceSourceEpoch();
        var scopes = new HashSet<MemoryScope>();
        foreach (var change in changes)
        {
            if (change.Value is { } current) scopes.Add(current.Scope);
            if (snapshots.GetValueOrDefault(change.Key) is { } previous) scopes.Add(previous.Scope);
        }
        // Invalidate before mutating source tables. Cancellation or a native failure may leave a partial commit,
        // and readers must rebuild rather than reuse a projection of the preceding record versions.
        foreach (var scope in scopes) await InvalidateReaderCatalogAsync(scope, cancellationToken).ConfigureAwait(false);
        foreach (var portion in changes.Chunk(PersistenceBatchRows))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var upserts = new List<MemoryRecord>(portion.Length);
            var deletes = new List<MemoryId>();
            var obsoleteVectors = new Dictionary<string, (EmbeddingReference Embedding, List<MemoryId> Ids)>(StringComparer.Ordinal);
            foreach (var change in portion)
            {
                var previous = snapshots.GetValueOrDefault(change.Key);
                var current = change.Value;
                if (previous?.Embedding is { } embedding &&
                    (current?.Embedding is null || current.EmbeddingVector is null || EmbeddingKey(embedding) != EmbeddingKey(current.Embedding)))
                {
                    var key = VectorTableName(embedding);
                    if (!obsoleteVectors.TryGetValue(key, out var group)) group = (embedding, []);
                    group.Ids.Add(change.Key);
                    obsoleteVectors[key] = group;
                }
                if (current is null) deletes.Add(change.Key);
                else upserts.Add(current);
            }
            foreach (var group in obsoleteVectors.Values)
            {
                var name = VectorTableName(group.Embedding);
                if (!await TableExistsAsync(name, cancellationToken).ConfigureAwait(false)) continue;
                using var vectors = await OpenTableAsync(name, cancellationToken).ConfigureAwait(false);
                await vectors.Delete(RecordIdsPredicate(group.Ids)).ConfigureAwait(false);
            }
            using (var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false))
            {
                if (deletes.Count > 0) await table.Delete(RecordIdsPredicate(deletes)).ConfigureAwait(false);
                if (upserts.Count > 0)
                {
                    using var batch = BuildMemoryBatch(upserts);
                    Interlocked.Increment(ref _recordMergeBatches);
                    await table.MergeInsert("id").WhenMatchedUpdateAll().WhenNotMatchedInsertAll().Execute(batch).ConfigureAwait(false);
                }
            }
            foreach (var group in upserts.Where(record => record.Embedding is not null && record.EmbeddingVector is not null)
                         .GroupBy(record => VectorTableName(record.Embedding!), StringComparer.Ordinal))
            {
                var records = group.ToArray();
                var embedding = records[0].Embedding!;
                await EnsureTableSchemaAsync(VectorTableDefinition(embedding), cancellationToken).ConfigureAwait(false);
                using var table = await OpenTableAsync(group.Key, cancellationToken).ConfigureAwait(false);
                using var batch = BuildMemoryBatch(records, embedding.Dimension);
                await table.MergeInsert("id").WhenMatchedUpdateAll().WhenNotMatchedInsertAll().Execute(batch).ConfigureAwait(false);
            }
        }

    }

    /// <summary>Builds a bounded exact-ID predicate; all IDs are escaped through the adapter's SQL literal policy.</summary>
    private static string RecordIdsPredicate(IEnumerable<MemoryId> ids) =>
        $"id IN ({string.Join(", ", ids.Select(id => Literal(id.Value)))})";
}
