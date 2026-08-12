using System.Globalization;
using Apache.Arrow;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
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

            using var table = await OpenTableAsync(ReaderWikiMetadataTable, cancellationToken).ConfigureAwait(false);
            await table.MergeInsert("metadata_key")
                .WhenMatchedUpdateAll()
                .WhenNotMatchedInsertAll()
                .Execute(BuildReaderWikiMetadataBatch([PersistedReaderWikiMetadataRow.From(metadata)]))
                .ConfigureAwait(false);
            await InvalidateReaderCatalogAsync(metadata.Scope, cancellationToken).ConfigureAwait(false);
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
        var batches = await table.Query()
            .Where($"metadata_key = {Literal(WikiMetadataKey(scope, memoryId, recordVersion))}")
            .Limit(2)
            .ToArrow()
            .ConfigureAwait(false);
        var matches = ReadReaderWikiMetadataRows(batches).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("The reader wiki metadata source has duplicate exact-version rows.");
        return matches.SingleOrDefault()?.ToModel(scope);
    }

    private async Task<IReadOnlyList<PersistedReaderWikiMetadataRow>> ReadWikiMetadataRowsAsync(
        MemoryScope scope,
        string @namespace,
        string slug,
        CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(ReaderWikiMetadataTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query()
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
