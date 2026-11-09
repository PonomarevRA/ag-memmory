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

    /// <summary>Hydrates complete eligible records; callers bound the predicate or limit when only selected winners are needed.</summary>
    private async Task<IReadOnlyList<MemoryRecord>> ReadRecordsAsync(
        string predicate,
        CancellationToken cancellationToken,
        int? limit = null)
    {
        using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
        var query = table.Query().Where(predicate);
        if (limit is { } maximum)
            query = query.Limit(maximum);
        using var batches = await query.ToArrow().ConfigureAwait(false);
        return ReadMemoryRecords(batches).ToArray();
    }

    /// <summary>Reads a bounded selected-column graph projection without document or embedding materialization.</summary>
    private async Task<IReadOnlyList<MemoryGraphSourceRecord>> ReadGraphSourceRecordsAsync(
        string predicate,
        CancellationToken cancellationToken,
        int limit)
    {
        using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
        // Graph topology does not need document content, provenance or embedding payloads.
        using var batches = await table.Query().Select(GraphSourceColumnNames).Where(predicate).Limit(limit).ToArrow().ConfigureAwait(false);
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
        using var batches = await table.Query().Where(predicate).ToArrow().ConfigureAwait(false);
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
        using var batches = await table.Query().Where(predicate).ToArrow().ConfigureAwait(false);
        return ReadReceiptRows(batches)
            .SingleOrDefault(receipt => receipt.CommandKind == commandKind && receipt.IdempotencyKey == idempotencyKey && receipt.Scope == scope);
    }

    /// <summary>Merges one bounded set of distinct hot-memory scope keys.</summary>
    private async Task PersistHotMemoriesAsync(IReadOnlyCollection<SessionHotMemory> memories, CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(HotMemoryTable, cancellationToken).ConfigureAwait(false);
        using var batch = BuildHotMemoryBatch(memories);
        await table.MergeInsert("scope_key")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(batch)
            .ConfigureAwait(false);
    }

    /// <summary>Merges one bounded set of distinct idempotency receipt keys after the record writes.</summary>
    private async Task PersistReceiptsAsync(IReadOnlyCollection<IdempotencyReceipt> receipts, CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(ReceiptsTable, cancellationToken).ConfigureAwait(false);
        using var batch = BuildReceiptBatch(receipts);
        await table.MergeInsert("receipt_key")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(batch)
            .ConfigureAwait(false);
    }

    /// <summary>Merges a bounded set of distinct outbox message IDs; transaction staging resolves repeated IDs to the last message.</summary>
    private async Task PersistOutboxMessagesAsync(IReadOnlyCollection<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(OutboxTable, cancellationToken).ConfigureAwait(false);
        using var batch = BuildOutboxBatch(messages);
        await table.MergeInsert("message_id")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(batch)
            .ConfigureAwait(false);
    }
}
