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
    private static RecordBatch BuildMemoryBatch(IReadOnlyCollection<MemoryRecord> records, int? vectorDimension = null)
    {
        if (records.Count == 0) throw new ArgumentException("A LanceDB batch cannot be empty.", nameof(records));
        var rows = records.Select(PersistedMemoryRow.From).ToArray();
        var columns = MemoryColumnNames.Select(column => Strings(rows.Select(row => row[column]))).Cast<IArrowArray>().ToList();
        if (vectorDimension is { } dimension)
        {
            var vectorItem = new Field("item", FloatType.Default, nullable: false);
            var vectorBuilder = new FixedSizeListArray.Builder(vectorItem, dimension);
            var values = (FloatArray.Builder)vectorBuilder.ValueBuilder;
            foreach (var row in rows)
            {
                if (row.Vector is null || row.Vector.Length != dimension)
                    throw new InvalidOperationException("The physical vector table requires a compatible embedding vector.");
                vectorBuilder.Append();
                values.AppendRange(row.Vector);
            }
            columns.Add(vectorBuilder.Build());
        }
        return new RecordBatch(CreateMemorySchema(vectorDimension), columns, rows.Length);
    }

    private static RecordBatch BuildHotMemoryBatch(IReadOnlyCollection<SessionHotMemory> memories)
    {
        var rows = memories.Select(PersistedHotMemoryRow.From).ToArray();
        return new RecordBatch(CreateHotMemorySchema(),
        [
            Strings(rows.Select(row => row.ScopeKey)), Strings(rows.Select(row => row.Id)), Strings(rows.Select(row => row.TenantId)),
            Strings(rows.Select(row => row.ProjectId)), Strings(rows.Select(row => row.WorkspaceId)), Strings(rows.Select(row => row.ChatId)),
            Strings(rows.Select(row => row.RunId)), Strings(rows.Select(row => row.Content)), Strings(rows.Select(row => row.ProvenanceJson)),
            Strings(rows.Select(row => row.Version)), Strings(rows.Select(row => row.CreatedAtUtc)), Strings(rows.Select(row => row.UpdatedAtUtc)),
            Strings(rows.Select(row => row.ExpiresAtUtc))
        ], rows.Length);
    }

    private static RecordBatch BuildReceiptBatch(IReadOnlyCollection<IdempotencyReceipt> receipts)
    {
        var rows = receipts.Select(PersistedReceiptRow.From).ToArray();
        return new RecordBatch(CreateReceiptSchema(),
        [
            Strings(rows.Select(row => row.ReceiptKey)), Strings(rows.Select(row => row.TenantId)), Strings(rows.Select(row => row.ProjectId)),
            Strings(rows.Select(row => row.WorkspaceId)), Strings(rows.Select(row => row.ChatId)), Strings(rows.Select(row => row.RunId)),
            Strings(rows.Select(row => row.CommandKind)), Strings(rows.Select(row => row.IdempotencyKey)), Strings(rows.Select(row => row.PayloadFingerprint)),
            Strings(rows.Select(row => row.MemoryId)), Strings(rows.Select(row => row.Outcome)), Strings(rows.Select(row => row.ContractVersion))
        ], rows.Length);
    }

    private static RecordBatch BuildOutboxBatch(IReadOnlyCollection<OutboxMessage> messages)
    {
        var rows = messages.Select(PersistedOutboxRow.From).ToArray();
        return new RecordBatch(CreateOutboxSchema(),
        [
            Strings(rows.Select(row => row.MessageId)), Strings(rows.Select(row => row.TenantId)), Strings(rows.Select(row => row.ProjectId)),
            Strings(rows.Select(row => row.WorkspaceId)), Strings(rows.Select(row => row.ChatId)), Strings(rows.Select(row => row.RunId)),
            Strings(rows.Select(row => row.Kind)), Strings(rows.Select(row => row.CommandId)), Strings(rows.Select(row => row.CorrelationId)),
            Strings(rows.Select(row => row.MemoryId)), Strings(rows.Select(row => row.ContractVersion))
        ], rows.Length);
    }

    private static RecordBatch BuildSchemaManifestBatch(IReadOnlyCollection<PersistedSchemaManifestRow> entries)
    {
        if (entries.Count == 0) throw new ArgumentException("A schema manifest batch cannot be empty.", nameof(entries));
        var rows = entries.ToArray();
        return new RecordBatch(CreateSchemaManifestSchema(),
        [
            Strings(rows.Select(row => row.TableName)), Strings(rows.Select(row => row.SchemaVersion)),
            Strings(rows.Select(row => row.SchemaFingerprint)), Strings(rows.Select(row => row.EmbeddingProvider)),
            Strings(rows.Select(row => row.EmbeddingModel)), Strings(rows.Select(row => row.EmbeddingModelVersion)),
            Strings(rows.Select(row => row.EmbeddingDimension)), Strings(rows.Select(row => row.EmbeddingNormalization))
        ], rows.Length);
    }

    private static StringArray Strings(IEnumerable<string?> values)
    {
        var builder = new StringArray.Builder();
        foreach (var value in values)
        {
            if (value is null) builder.AppendNull();
            else builder.Append(value);
        }
        return builder.Build();
    }

    private static IEnumerable<MemoryRecord> ReadMemoryRecords(RecordBatch batch)
    {
        for (var index = 0; index < batch.Length; index++) yield return PersistedMemoryRow.Read(batch, index).ToMemoryRecord();
    }

    private static IEnumerable<SessionHotMemory> ReadHotMemoryRows(RecordBatch batch)
    {
        for (var index = 0; index < batch.Length; index++) yield return PersistedHotMemoryRow.Read(batch, index).ToMemory();
    }

    private static IEnumerable<IdempotencyReceipt> ReadReceiptRows(RecordBatch batch)
    {
        for (var index = 0; index < batch.Length; index++) yield return PersistedReceiptRow.Read(batch, index).ToReceipt();
    }

    private async Task<PersistedSchemaManifestRow?> ReadSchemaManifestRowAsync(string tableName, CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(SchemaManifestTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query().Where($"table_name = {Literal(tableName)}").ToArrow().ConfigureAwait(false);
        var entries = ReadSchemaManifestRows(batches).ToArray();
        if (entries.Length > 1)
        {
            throw new LanceDbSchemaMismatchException(
                SchemaManifestTable,
                "one manifest entry per table name",
                $"{entries.Length} entries for {tableName}",
                "manifest uniqueness was violated");
        }
        return entries.SingleOrDefault();
    }

    private async Task<IReadOnlyList<PersistedSchemaManifestRow>> ReadSchemaManifestRowsAsync(CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(SchemaManifestTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query().ToArrow().ConfigureAwait(false);
        return ReadSchemaManifestRows(batches).ToArray();
    }

    private static IEnumerable<PersistedSchemaManifestRow> ReadSchemaManifestRows(RecordBatch batch)
    {
        for (var index = 0; index < batch.Length; index++) yield return PersistedSchemaManifestRow.Read(batch, index);
    }
}
