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
    private static Schema CreateMemorySchema(int? vectorDimension = null)
    {
        var builder = new Schema.Builder();
        foreach (var column in MemoryColumnNames)
            builder.Field(new Field(column, StringType.Default, IsNullableMemoryColumn(column)));
        if (vectorDimension is { } dimension)
        {
            var vectorItem = new Field("item", FloatType.Default, nullable: false);
            builder.Field(new Field("vector", new FixedSizeListType(vectorItem, dimension), nullable: false));
        }
        return builder.Build();
    }

    private static LanceDbTableSchemaDefinition SchemaManifestDefinition() => new(
        SchemaManifestTable,
        CurrentStorageSchemaVersion,
        CreateSchemaManifestSchema());

    private static IEnumerable<LanceDbTableSchemaDefinition> CoreTableDefinitions()
    {
        yield return new LanceDbTableSchemaDefinition(RecordsTable, CurrentStorageSchemaVersion, CreateMemorySchema());
        yield return new LanceDbTableSchemaDefinition(HotMemoryTable, CurrentStorageSchemaVersion, CreateHotMemorySchema());
        yield return new LanceDbTableSchemaDefinition(ReceiptsTable, CurrentStorageSchemaVersion, CreateReceiptSchema());
        yield return new LanceDbTableSchemaDefinition(OutboxTable, CurrentStorageSchemaVersion, CreateOutboxSchema());
        yield return new LanceDbTableSchemaDefinition(ReaderRoutesTable, CurrentStorageSchemaVersion, CreateReaderRouteSchema());
        yield return new LanceDbTableSchemaDefinition(ReaderCatalogGenerationsTable, CurrentStorageSchemaVersion, CreateReaderCatalogGenerationSchema());
        yield return new LanceDbTableSchemaDefinition(ReaderCatalogLeavesTable, CurrentStorageSchemaVersion, CreateReaderCatalogLeafSchema());
        yield return new LanceDbTableSchemaDefinition(ReaderCatalogBuildRunsTable, CurrentStorageSchemaVersion, CreateReaderCatalogBuildRunSchema());
        yield return new LanceDbTableSchemaDefinition(ReaderWikiMetadataTable, CurrentStorageSchemaVersion, CreateReaderWikiMetadataSchema());
        yield return new LanceDbTableSchemaDefinition(ReaderWikiDocumentsTable, CurrentStorageSchemaVersion, CreateReaderWikiDocumentsSchema());
        yield return new LanceDbTableSchemaDefinition(ReaderWikiRelationsTable, CurrentStorageSchemaVersion, CreateReaderWikiRelationsSchema());
        yield return new LanceDbTableSchemaDefinition(UsageAuditEventsTable, CurrentStorageSchemaVersion, CreateUsageAuditEventsSchema());
    }

    private static LanceDbTableSchemaDefinition VectorTableDefinition(EmbeddingReference embedding)
    {
        embedding.Validate();
        return new LanceDbTableSchemaDefinition(
            VectorTableName(embedding),
            CurrentStorageSchemaVersion,
            CreateMemorySchema(embedding.Dimension),
            new LanceDbEmbeddingSchemaMetadata(
                embedding.Provider,
                embedding.Model,
                embedding.ModelVersion,
                embedding.Dimension,
                embedding.Normalization));
    }

    private static LanceDbTableSchemaDefinition DefinitionFromManifest(PersistedSchemaManifestRow entry)
    {
        if (string.Equals(entry.TableName, SchemaManifestTable, StringComparison.Ordinal)) return SchemaManifestDefinition();
        if (string.Equals(entry.TableName, RecordsTable, StringComparison.Ordinal))
            return new LanceDbTableSchemaDefinition(RecordsTable, CurrentStorageSchemaVersion, CreateMemorySchema());
        if (string.Equals(entry.TableName, HotMemoryTable, StringComparison.Ordinal))
            return new LanceDbTableSchemaDefinition(HotMemoryTable, CurrentStorageSchemaVersion, CreateHotMemorySchema());
        if (string.Equals(entry.TableName, ReceiptsTable, StringComparison.Ordinal))
            return new LanceDbTableSchemaDefinition(ReceiptsTable, CurrentStorageSchemaVersion, CreateReceiptSchema());
        if (string.Equals(entry.TableName, OutboxTable, StringComparison.Ordinal))
            return new LanceDbTableSchemaDefinition(OutboxTable, CurrentStorageSchemaVersion, CreateOutboxSchema());
        if (string.Equals(entry.TableName, ReaderRoutesTable, StringComparison.Ordinal))
            return new LanceDbTableSchemaDefinition(ReaderRoutesTable, CurrentStorageSchemaVersion, CreateReaderRouteSchema());
        if (string.Equals(entry.TableName, ReaderCatalogGenerationsTable, StringComparison.Ordinal))
            return new LanceDbTableSchemaDefinition(ReaderCatalogGenerationsTable, CurrentStorageSchemaVersion, CreateReaderCatalogGenerationSchema());
        if (string.Equals(entry.TableName, ReaderCatalogLeavesTable, StringComparison.Ordinal))
            return new LanceDbTableSchemaDefinition(ReaderCatalogLeavesTable, CurrentStorageSchemaVersion, CreateReaderCatalogLeafSchema());
        if (string.Equals(entry.TableName, ReaderCatalogBuildRunsTable, StringComparison.Ordinal))
            return new LanceDbTableSchemaDefinition(ReaderCatalogBuildRunsTable, CurrentStorageSchemaVersion, CreateReaderCatalogBuildRunSchema());
        if (string.Equals(entry.TableName, ReaderWikiMetadataTable, StringComparison.Ordinal))
            return new LanceDbTableSchemaDefinition(ReaderWikiMetadataTable, CurrentStorageSchemaVersion, CreateReaderWikiMetadataSchema());
        if (string.Equals(entry.TableName, ReaderWikiDocumentsTable, StringComparison.Ordinal))
            return new LanceDbTableSchemaDefinition(ReaderWikiDocumentsTable, CurrentStorageSchemaVersion, CreateReaderWikiDocumentsSchema());
        if (string.Equals(entry.TableName, ReaderWikiRelationsTable, StringComparison.Ordinal))
            return new LanceDbTableSchemaDefinition(ReaderWikiRelationsTable, CurrentStorageSchemaVersion, CreateReaderWikiRelationsSchema());
        if (string.Equals(entry.TableName, UsageAuditEventsTable, StringComparison.Ordinal))
            return new LanceDbTableSchemaDefinition(UsageAuditEventsTable, CurrentStorageSchemaVersion, CreateUsageAuditEventsSchema());

        if (entry.Embedding is null || !entry.TableName.StartsWith("memory_vectors_", StringComparison.Ordinal))
        {
            throw new LanceDbSchemaMismatchException(
                entry.TableName,
                "an adapter-owned core table or a versioned vector table",
                $"version={entry.SchemaVersion}; fingerprint={entry.SchemaFingerprint}",
                "manifest contains an unknown table identity");
        }

        var embedding = new EmbeddingReference(
            entry.Embedding.Provider,
            entry.Embedding.Model,
            entry.Embedding.ModelVersion,
            entry.Embedding.Dimension,
            entry.Embedding.Normalization,
            "manifest");
        var definition = VectorTableDefinition(embedding);
        if (!string.Equals(definition.TableName, entry.TableName, StringComparison.Ordinal))
        {
            throw new LanceDbSchemaMismatchException(
                entry.TableName,
                definition.TableName,
                entry.TableName,
                "vector table name does not match its persisted embedding identity");
        }
        return definition;
    }

    private static Schema CreateSchemaManifestSchema()
    {
        var builder = new Schema.Builder();
        foreach (var column in SchemaManifestColumnNames)
            builder.Field(new Field(column, StringType.Default, column is not "table_name" and not "schema_version" and not "schema_fingerprint"));
        return builder.Build();
    }

    private static Schema CreateHotMemorySchema() => new Schema.Builder()
        .Field(new Field("scope_key", StringType.Default, nullable: false))
        .Field(new Field("id", StringType.Default, nullable: false))
        .Field(new Field("tenant_id", StringType.Default, nullable: false))
        .Field(new Field("project_id", StringType.Default, nullable: true))
        .Field(new Field("workspace_id", StringType.Default, nullable: true))
        .Field(new Field("chat_id", StringType.Default, nullable: true))
        .Field(new Field("run_id", StringType.Default, nullable: true))
        .Field(new Field("content", StringType.Default, nullable: false))
        .Field(new Field("provenance_json", StringType.Default, nullable: false))
        .Field(new Field("version", StringType.Default, nullable: false))
        .Field(new Field("created_at_utc", StringType.Default, nullable: false))
        .Field(new Field("updated_at_utc", StringType.Default, nullable: false))
        .Field(new Field("expires_at_utc", StringType.Default, nullable: false))
        .Build();

    private static Schema CreateReceiptSchema() => new Schema.Builder()
        .Field(new Field("receipt_key", StringType.Default, nullable: false))
        .Field(new Field("tenant_id", StringType.Default, nullable: false))
        .Field(new Field("project_id", StringType.Default, nullable: true))
        .Field(new Field("workspace_id", StringType.Default, nullable: true))
        .Field(new Field("chat_id", StringType.Default, nullable: true))
        .Field(new Field("run_id", StringType.Default, nullable: true))
        .Field(new Field("command_kind", StringType.Default, nullable: false))
        .Field(new Field("idempotency_key", StringType.Default, nullable: false))
        .Field(new Field("payload_fingerprint", StringType.Default, nullable: false))
        .Field(new Field("memory_id", StringType.Default, nullable: true))
        .Field(new Field("outcome", StringType.Default, nullable: false))
        .Field(new Field("contract_version", StringType.Default, nullable: false))
        .Build();

    private static Schema CreateOutboxSchema() => new Schema.Builder()
        .Field(new Field("message_id", StringType.Default, nullable: false))
        .Field(new Field("tenant_id", StringType.Default, nullable: false))
        .Field(new Field("project_id", StringType.Default, nullable: true))
        .Field(new Field("workspace_id", StringType.Default, nullable: true))
        .Field(new Field("chat_id", StringType.Default, nullable: true))
        .Field(new Field("run_id", StringType.Default, nullable: true))
        .Field(new Field("kind", StringType.Default, nullable: false))
        .Field(new Field("command_id", StringType.Default, nullable: false))
        .Field(new Field("correlation_id", StringType.Default, nullable: false))
        .Field(new Field("memory_id", StringType.Default, nullable: true))
        .Field(new Field("contract_version", StringType.Default, nullable: false))
        .Build();

    private static Schema CreateReaderRouteSchema() => new Schema.Builder()
        .Field(new Field("route_key", StringType.Default, nullable: false))
        .Field(new Field("memory_id", StringType.Default, nullable: false))
        .Build();

    private static Schema CreateReaderCatalogGenerationSchema() => new Schema.Builder()
        .Field(new Field("scope_key", StringType.Default, nullable: false))
        .Field(new Field("generation_key", StringType.Default, nullable: false))
        .Field(new Field("state", StringType.Default, nullable: false))
        .Field(new Field("created_at_utc", StringType.Default, nullable: false))
        .Field(new Field("ready_at_utc", StringType.Default, nullable: true))
        .Build();

    private static Schema CreateReaderCatalogLeafSchema() => new Schema.Builder()
        .Field(new Field("leaf_key", StringType.Default, nullable: false))
        .Field(new Field("scope_key", StringType.Default, nullable: false))
        .Field(new Field("generation_key", StringType.Default, nullable: false))
        .Field(new Field("leaf_position", StringType.Default, nullable: false))
        .Field(new Field("memory_id", StringType.Default, nullable: false))
        .Field(new Field("record_type", StringType.Default, nullable: false))
        .Field(new Field("updated_at_utc", StringType.Default, nullable: false))
        .Field(new Field("version", StringType.Default, nullable: false))
        .Build();

    private static Schema CreateReaderCatalogBuildRunSchema() => new Schema.Builder()
        .Field(new Field("row_key", StringType.Default, nullable: false))
        .Field(new Field("generation_key", StringType.Default, nullable: false))
        .Field(new Field("run_key", StringType.Default, nullable: false))
        .Field(new Field("row_position", StringType.Default, nullable: false))
        .Field(new Field("sort_key", StringType.Default, nullable: false))
        .Field(new Field("memory_id", StringType.Default, nullable: false))
        .Field(new Field("record_type", StringType.Default, nullable: false))
        .Field(new Field("updated_at_utc", StringType.Default, nullable: false))
        .Field(new Field("version", StringType.Default, nullable: false))
        .Build();

    private static Schema CreateReaderWikiMetadataSchema() => new Schema.Builder()
        .Field(new Field("metadata_key", StringType.Default, nullable: false))
        .Field(new Field("scope_key", StringType.Default, nullable: false))
        .Field(new Field("memory_id", StringType.Default, nullable: false))
        .Field(new Field("record_version", StringType.Default, nullable: false))
        .Field(new Field("title", StringType.Default, nullable: true))
        .Field(new Field("namespace", StringType.Default, nullable: true))
        .Field(new Field("slug", StringType.Default, nullable: true))
        .Field(new Field("tags_json", StringType.Default, nullable: false))
        .Build();

    private static Schema CreateReaderWikiDocumentsSchema() => new Schema.Builder()
        .Field(new Field("document_key", StringType.Default, nullable: false))
        .Field(new Field("generation_key", StringType.Default, nullable: false))
        .Field(new Field("memory_id", StringType.Default, nullable: false))
        .Field(new Field("record_version", StringType.Default, nullable: false))
        .Field(new Field("title", StringType.Default, nullable: false))
        .Field(new Field("namespace", StringType.Default, nullable: false))
        .Field(new Field("slug", StringType.Default, nullable: true))
        .Build();

    private static Schema CreateReaderWikiRelationsSchema() => new Schema.Builder()
        .Field(new Field("relation_key", StringType.Default, nullable: false))
        .Field(new Field("generation_key", StringType.Default, nullable: false))
        .Field(new Field("source_memory_id", StringType.Default, nullable: false))
        .Field(new Field("target_memory_id", StringType.Default, nullable: false))
        .Field(new Field("kind", StringType.Default, nullable: false))
        .Field(new Field("label", StringType.Default, nullable: false))
        .Field(new Field("shared_entity_count", StringType.Default, nullable: false))
        .Build();

    private static Schema CreateUsageAuditEventsSchema() => new Schema.Builder()
        .Field(new Field("event_id", StringType.Default, nullable: false))
        .Field(new Field("occurred_at_utc", StringType.Default, nullable: false))
        .Field(new Field("schema_version", StringType.Default, nullable: false))
        .Field(new Field("source", StringType.Default, nullable: false))
        .Field(new Field("operation", StringType.Default, nullable: false))
        .Field(new Field("client_label", StringType.Default, nullable: false))
        .Field(new Field("area_id", StringType.Default, nullable: true))
        .Field(new Field("query_hash", StringType.Default, nullable: false))
        .Field(new Field("query_length_chars", StringType.Default, nullable: false))
        .Field(new Field("result_count", StringType.Default, nullable: false))
        .Field(new Field("selected_context_chars", StringType.Default, nullable: false))
        .Field(new Field("delivered_tokens_estimate", StringType.Default, nullable: false))
        .Field(new Field("estimation_method_version", StringType.Default, nullable: false))
        .Field(new Field("outcome", StringType.Default, nullable: false))
        .Field(new Field("failure_class", StringType.Default, nullable: true))
        .Build();

    private static bool IsNullableMemoryColumn(string column) => column is
        "project_id" or "workspace_id" or "chat_id" or "run_id" or "reason" or "embedding_json" or
        "embedding_vector_json" or "expires_at_utc" or "decision_details_json";
}
