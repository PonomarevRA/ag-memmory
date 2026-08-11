using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using AgMemory.Contracts;
using lancedb;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    private async Task<MemoryRecord?> ReadRecordAsync(MemoryId id, AuthorizedScopeSet scopes, CancellationToken cancellationToken)
    {
        var predicate = $"({BuildAuthorizedPredicate(scopes)}) AND id = {Literal(id.Value)}";
        var records = await ReadRecordsAsync(predicate, cancellationToken).ConfigureAwait(false);
        return records.SingleOrDefault(record => record.Id == id && scopes.Contains(record.Scope));
    }

    private async Task<MemoryRecord?> ReadRecordUnscopedAsync(MemoryId id, CancellationToken cancellationToken)
    {
        var records = await ReadRecordsAsync($"id = {Literal(id.Value)}", cancellationToken).ConfigureAwait(false);
        return records.SingleOrDefault(record => record.Id == id);
    }

    private async Task<IReadOnlyList<MemoryRecord>> ReadRecordsAsync(
        string predicate,
        CancellationToken cancellationToken,
        int? limit = null)
    {
        using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
        var query = table.Query().Where(predicate);
        if (limit is { } maximum)
            query = query.Limit(maximum);
        var batches = await query.ToArrow().ConfigureAwait(false);
        return ReadMemoryRecords(batches).ToArray();
    }

    private async Task<IReadOnlyList<MemoryGraphSourceRecord>> ReadGraphSourceRecordsAsync(
        string predicate,
        CancellationToken cancellationToken,
        int limit)
    {
        using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query().Where(predicate).Limit(limit).ToArrow().ConfigureAwait(false);
        return ReadMemoryGraphSourceRecords(batches).ToArray();
    }

    private async Task<SessionHotMemory?> ReadHotMemoryAsync(
        MemoryScope exactScope,
        AuthorizedScopeSet scopes,
        CancellationToken cancellationToken)
    {
        if (!scopes.Contains(exactScope)) return null;
        var predicate = $"({BuildSelectorPredicate(new ScopeSelector(exactScope))}) AND scope_key = {Literal(ScopeKey(exactScope))}";
        using var table = await OpenTableAsync(HotMemoryTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query().Where(predicate).ToArrow().ConfigureAwait(false);
        return ReadHotMemoryRows(batches)
            .SingleOrDefault(memory => scopes.Contains(memory.Scope) && memory.Scope == exactScope);
    }

    private async Task<IdempotencyReceipt?> ReadReceiptAsync(
        AuthorizedScopeSet scopes,
        string commandKind,
        ScopeSelector scope,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!scopes.Contains(scope.Scope)) return null;
        var predicate = $"receipt_key = {Literal(ReceiptKey(commandKind, scope, idempotencyKey))}";
        using var table = await OpenTableAsync(ReceiptsTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query().Where(predicate).ToArrow().ConfigureAwait(false);
        return ReadReceiptRows(batches)
            .SingleOrDefault(receipt => receipt.CommandKind == commandKind && receipt.IdempotencyKey == idempotencyKey && receipt.Scope == scope);
    }

    private async Task PersistRecordAsync(MemoryRecord record, MemoryRecord? previous, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (previous?.Embedding is { } previousEmbedding &&
            (!EmbeddingKey(previousEmbedding)!.Equals(record.Embedding is null ? null : EmbeddingKey(record.Embedding), StringComparison.Ordinal) ||
             record.EmbeddingVector is null))
            await DeleteVectorRowAsync(previousEmbedding, record.Id, cancellationToken).ConfigureAwait(false);

        using (var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false))
        {
            await table.MergeInsert("id")
                .WhenMatchedUpdateAll()
                .WhenNotMatchedInsertAll()
                .Execute(BuildMemoryBatch([record]))
                .ConfigureAwait(false);
        }

        await InvalidateReaderCatalogAsync(record.Scope, cancellationToken).ConfigureAwait(false);

        if (record.Embedding is null || record.EmbeddingVector is null) return;
        var vectorTableName = VectorTableName(record.Embedding);
        var vectorSchema = VectorTableDefinition(record.Embedding);
        await EnsureTableSchemaAsync(vectorSchema, cancellationToken).ConfigureAwait(false);
        using var vectorTable = await OpenTableAsync(vectorTableName, cancellationToken).ConfigureAwait(false);
        await vectorTable.MergeInsert("id")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(BuildMemoryBatch([record], record.Embedding.Dimension))
            .ConfigureAwait(false);
    }

    private async Task DeleteRecordAsync(MemoryRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (record.Embedding is not null)
            await DeleteVectorRowAsync(record.Embedding, record.Id, cancellationToken).ConfigureAwait(false);
        using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
        await table.Delete($"id = {Literal(record.Id.Value)}").ConfigureAwait(false);
        await InvalidateReaderCatalogAsync(record.Scope, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteVectorRowAsync(EmbeddingReference embedding, MemoryId id, CancellationToken cancellationToken)
    {
        var tableName = VectorTableName(embedding);
        if (!await TableExistsAsync(tableName, cancellationToken).ConfigureAwait(false)) return;
        using var table = await OpenTableAsync(tableName, cancellationToken).ConfigureAwait(false);
        await table.Delete($"id = {Literal(id.Value)}").ConfigureAwait(false);
    }

    private async Task PersistHotMemoryAsync(SessionHotMemory memory, CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(HotMemoryTable, cancellationToken).ConfigureAwait(false);
        await table.MergeInsert("scope_key")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(BuildHotMemoryBatch([memory]))
            .ConfigureAwait(false);
    }

    private async Task PersistReceiptAsync(IdempotencyReceipt receipt, CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(ReceiptsTable, cancellationToken).ConfigureAwait(false);
        await table.MergeInsert("receipt_key")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(BuildReceiptBatch([receipt]))
            .ConfigureAwait(false);
    }

    private async Task PersistOutboxMessageAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(OutboxTable, cancellationToken).ConfigureAwait(false);
        await table.MergeInsert("message_id")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(BuildOutboxBatch([message]))
            .ConfigureAwait(false);
    }
}
