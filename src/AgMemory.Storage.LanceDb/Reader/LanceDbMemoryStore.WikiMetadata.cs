using System.Globalization;
using Apache.Arrow;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    /// <summary>Checks slug uniqueness, retires the matching projection, then writes metadata; cancellation/failure may require an extra rebuild but cannot retain a stale Ready pointer.</summary>
    public async Task UpsertWikiMetadataAsync(MemoryWikiMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.Validate();
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            if (metadata.Namespace is not null && metadata.Slug is not null)
            {
                var matches = await ReadWikiMetadataRowsAsync(metadata.Scope, metadata.Namespace, metadata.Slug, cancellationToken).ConfigureAwait(false);
                if (matches.Any(match => !string.Equals(match.MemoryId, metadata.MemoryId.Value, StringComparison.Ordinal)))
                    throw new InvalidOperationException("A wiki slug must be unique within its exact scope and namespace.");
            }

            // Retire the projection before mutating metadata. Cancellation after a successful native merge
            // must never leave a previously Ready tree/facet generation current indefinitely.
            await InvalidateReaderCatalogAsync(metadata.Scope, cancellationToken).ConfigureAwait(false);
            using var table = await OpenTableAsync(ReaderWikiMetadataTable, cancellationToken).ConfigureAwait(false);
            await table.MergeInsert("metadata_key")
                .WhenMatchedUpdateAll()
                .WhenNotMatchedInsertAll()
                .Execute(BuildReaderWikiMetadataBatch([PersistedReaderWikiMetadataRow.From(metadata)]))
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MemoryWikiMetadata?> ReadWikiMetadataAsync(
        MemoryScope scope,
        MemoryId memoryId,
        long recordVersion,
        CancellationToken cancellationToken)
    {
        scope.Validate();
        memoryId.Validate(nameof(memoryId));
        if (recordVersion <= 0) throw new ArgumentOutOfRangeException(nameof(recordVersion));
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return await ReadWikiMetadataCoreAsync(scope, memoryId, recordVersion, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MemoryWikiMetadata?> ReadWikiMetadataCoreAsync(
        MemoryScope scope,
        MemoryId memoryId,
        long recordVersion,
        CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(ReaderWikiMetadataTable, cancellationToken).ConfigureAwait(false);
        using var batches = await table.Query()
            .Where($"metadata_key = {Literal(WikiMetadataKey(scope, memoryId, recordVersion))}")
            .Limit(2)
            .ToArrow()
            .ConfigureAwait(false);
        var matches = ReadReaderWikiMetadataRows(batches).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("The reader wiki metadata source has duplicate exact-version rows.");
        return matches.SingleOrDefault()?.ToModel(scope);
    }

    /// <summary>Reads exact-version metadata for one bounded source portion in a single native query.</summary>
    private async Task<Dictionary<string, MemoryWikiMetadata>> ReadWikiMetadataBatchCoreAsync(
        MemoryScope scope, IReadOnlyList<MemoryReaderCatalogSourceRecord> records, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, MemoryWikiMetadata>(StringComparer.Ordinal);
        if (records.Count == 0) return result;
        var keys = new List<string>(records.Count);
        foreach (var record in records) keys.Add(Literal(WikiMetadataKey(scope, record.MemoryId, record.Version)));
        using var table = await OpenTableAsync(ReaderWikiMetadataTable, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _catalogMetadataQueries);
        using var batch = await table.Query().Where($"scope_key = {Literal(ScopeKey(scope))} AND metadata_key IN ({string.Join(", ", keys)})")
            .Limit(records.Count + 1).ToArrow().ConfigureAwait(false);
        foreach (var row in ReadReaderWikiMetadataRows(batch))
        {
            if (!result.TryAdd(row.MemoryId, row.ToModel(scope)))
                throw new InvalidDataException("The reader wiki metadata source has duplicate exact-version rows.");
        }
        return result;
    }

    private async Task<IReadOnlyList<PersistedReaderWikiMetadataRow>> ReadWikiMetadataRowsAsync(
        MemoryScope scope,
        string @namespace,
        string slug,
        CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(ReaderWikiMetadataTable, cancellationToken).ConfigureAwait(false);
        using var batches = await table.Query()
            .Where($"scope_key = {Literal(ScopeKey(scope))} AND namespace = {Literal(@namespace)} AND slug = {Literal(slug)}")
            .Limit(3)
            .ToArrow()
            .ConfigureAwait(false);
        return ReadReaderWikiMetadataRows(batches).ToArray();
    }

    private static IEnumerable<PersistedReaderWikiMetadataRow> ReadReaderWikiMetadataRows(RecordBatch batch)
    {
        for (var index = 0; index < batch.Length; index++) yield return PersistedReaderWikiMetadataRow.Read(batch, index);
    }

    private static string WikiMetadataKey(MemoryScope scope, MemoryId memoryId, long recordVersion) =>
        string.Concat(ScopeKey(scope), "|", memoryId.Value, "|", recordVersion.ToString(CultureInfo.InvariantCulture));
}
