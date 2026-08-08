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
    private sealed record PersistedMemoryRow(IReadOnlyDictionary<string, string?> Values, float[]? Vector)
    {
        public string? this[string column] => Values[column];

        public static PersistedMemoryRow From(MemoryRecord record)
        {
            ValidateMemoryRecordForSchema(record);
            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["id"] = record.Id.Value,
                ["tenant_id"] = record.Scope.TenantId.Value,
                ["project_id"] = record.Scope.ProjectId?.Value,
                ["workspace_id"] = record.Scope.WorkspaceId?.Value,
                ["chat_id"] = record.Scope.ChatId?.Value,
                ["run_id"] = record.Scope.RunId?.Value,
                ["record_type"] = record.Type.ToString(),
                ["status"] = record.Status.ToString(),
                ["canonical_text"] = record.CanonicalText,
                ["reason"] = record.Reason,
                ["importance"] = record.Importance.ToString("R", CultureInfo.InvariantCulture),
                ["confidence"] = record.Confidence.ToString("R", CultureInfo.InvariantCulture),
                ["estimated_token_cost"] = record.EstimatedTokenCost.ToString(CultureInfo.InvariantCulture),
                ["created_at_utc"] = Utc(record.CreatedAt),
                ["updated_at_utc"] = Utc(record.UpdatedAt),
                ["version"] = record.Version.ToString(CultureInfo.InvariantCulture),
                ["entities_json"] = JsonSerializer.Serialize(record.Entities, JsonOptions),
                ["provenance_json"] = JsonSerializer.Serialize(PersistedProvenance.From(record.Provenance), JsonOptions),
                ["embedding_json"] = record.Embedding is null ? null : JsonSerializer.Serialize(PersistedEmbedding.From(record.Embedding), JsonOptions),
                ["embedding_vector_json"] = record.EmbeddingVector is null ? null : JsonSerializer.Serialize(record.EmbeddingVector.Value.ToArray(), JsonOptions),
                ["expires_at_utc"] = record.ExpiresAt is null ? null : Utc(record.ExpiresAt.Value),
                ["deduplication_key"] = record.DeduplicationKey,
                ["decision_details_json"] = record.DecisionDetails is null ? null : JsonSerializer.Serialize(record.DecisionDetails, JsonOptions)
            };
            return new(values, record.EmbeddingVector?.ToArray());
        }

        public static PersistedMemoryRow Read(RecordBatch batch, int index)
        {
            var values = MemoryColumnNames.ToDictionary(column => column, column => Value(batch, column, index), StringComparer.Ordinal);
            var vector = TryReadVector(batch, index);
            return new(values, vector);
        }

        public MemoryRecord ToMemoryRecord()
        {
            var embedding = Optional("embedding_json") is { } embeddingJson
                ? PersistedEmbedding.ToModel(Deserialize<PersistedEmbedding>(embeddingJson))
                : null;
            var vector = Optional("embedding_vector_json") is { } vectorJson
                ? Deserialize<float[]>(vectorJson)
                : Vector;
            var record = new MemoryRecord(
                new MemoryId(Required("id")),
                Scope(Values),
                ParseEnum<MemoryRecordType>(Required("record_type")),
                ParseEnum<MemoryLifecycleStatus>(Required("status")),
                Required("canonical_text"),
                Optional("reason"),
                double.Parse(Required("importance"), CultureInfo.InvariantCulture),
                double.Parse(Required("confidence"), CultureInfo.InvariantCulture),
                int.Parse(Required("estimated_token_cost"), CultureInfo.InvariantCulture),
                ParseUtc(Required("created_at_utc")),
                ParseUtc(Required("updated_at_utc")),
                long.Parse(Required("version"), CultureInfo.InvariantCulture),
                Deserialize<string[]>(Required("entities_json")),
                PersistedProvenance.ToModel(Deserialize<PersistedProvenance>(Required("provenance_json"))),
                embedding,
                Optional("expires_at_utc") is { } expires ? ParseUtc(expires) : null,
                Required("deduplication_key"),
                Optional("decision_details_json") is { } decision ? Deserialize<DecisionDetails>(decision) : null,
                vector);
            ValidateMemoryRecordForSchema(record);
            return record;
        }

        public MemoryGraphSourceRecord ToGraphSourceRecord() => new(
            new MemoryId(Required("id")),
            Scope(Values),
            ParseEnum<MemoryRecordType>(Required("record_type")),
            ParseEnum<MemoryLifecycleStatus>(Required("status")),
            double.Parse(Required("importance"), CultureInfo.InvariantCulture),
            double.Parse(Required("confidence"), CultureInfo.InvariantCulture),
            Deserialize<string[]>(Required("entities_json")),
            Optional("expires_at_utc") is { } expires ? ParseUtc(expires) : null);

        private string Required(string key) => Optional(key) ?? throw new InvalidDataException($"Required LanceDB column '{key}' is null.");
        private string? Optional(string key) => Values[key];
    }

    private sealed record PersistedHotMemoryRow(
        string ScopeKey,
        string Id,
        string TenantId,
        string? ProjectId,
        string? WorkspaceId,
        string? ChatId,
        string? RunId,
        string Content,
        string ProvenanceJson,
        string Version,
        string CreatedAtUtc,
        string UpdatedAtUtc,
        string ExpiresAtUtc)
    {
        public static PersistedHotMemoryRow From(SessionHotMemory memory) => new(
            LanceDbMemoryStore.ScopeKey(memory.Scope), memory.Id.Value, memory.Scope.TenantId.Value, memory.Scope.ProjectId?.Value,
            memory.Scope.WorkspaceId?.Value, memory.Scope.ChatId?.Value, memory.Scope.RunId?.Value, memory.Content,
            JsonSerializer.Serialize(PersistedProvenance.From(memory.Provenance), JsonOptions), memory.Version.ToString(CultureInfo.InvariantCulture),
            Utc(memory.CreatedAt), Utc(memory.UpdatedAt), Utc(memory.ExpiresAt));

        public static PersistedHotMemoryRow Read(RecordBatch batch, int index) => new(
            Required(batch, "scope_key", index), Required(batch, "id", index), Required(batch, "tenant_id", index), Value(batch, "project_id", index),
            Value(batch, "workspace_id", index), Value(batch, "chat_id", index), Value(batch, "run_id", index), Required(batch, "content", index),
            Required(batch, "provenance_json", index), Required(batch, "version", index), Required(batch, "created_at_utc", index),
            Required(batch, "updated_at_utc", index), Required(batch, "expires_at_utc", index));

        public SessionHotMemory ToMemory() => new(new MemoryId(Id), new(new ScopeId(TenantId), ToScopeId(ProjectId), ToScopeId(WorkspaceId), ToScopeId(ChatId), ToScopeId(RunId)),
            Content, PersistedProvenance.ToModel(Deserialize<PersistedProvenance>(ProvenanceJson)), long.Parse(Version, CultureInfo.InvariantCulture),
            ParseUtc(CreatedAtUtc), ParseUtc(UpdatedAtUtc), ParseUtc(ExpiresAtUtc));
    }

    private sealed record PersistedReceiptRow(
        string ReceiptKey,
        string TenantId,
        string? ProjectId,
        string? WorkspaceId,
        string? ChatId,
        string? RunId,
        string CommandKind,
        string IdempotencyKey,
        string PayloadFingerprint,
        string? MemoryId,
        string Outcome,
        string ContractVersion)
    {
        public static PersistedReceiptRow From(IdempotencyReceipt receipt) => new(
            LanceDbMemoryStore.ReceiptKey(receipt.CommandKind, receipt.Scope, receipt.IdempotencyKey), receipt.Scope.Scope.TenantId.Value,
            receipt.Scope.Scope.ProjectId?.Value, receipt.Scope.Scope.WorkspaceId?.Value, receipt.Scope.Scope.ChatId?.Value, receipt.Scope.Scope.RunId?.Value,
            receipt.CommandKind, receipt.IdempotencyKey, receipt.PayloadFingerprint, receipt.MemoryId, receipt.Outcome, receipt.ContractVersion.Value);

        public static PersistedReceiptRow Read(RecordBatch batch, int index) => new(
            Required(batch, "receipt_key", index), Required(batch, "tenant_id", index), Value(batch, "project_id", index), Value(batch, "workspace_id", index),
            Value(batch, "chat_id", index), Value(batch, "run_id", index), Required(batch, "command_kind", index), Required(batch, "idempotency_key", index),
            Required(batch, "payload_fingerprint", index), Value(batch, "memory_id", index), Required(batch, "outcome", index), Required(batch, "contract_version", index));

        public IdempotencyReceipt ToReceipt() => new(CommandKind,
            new ScopeSelector(new MemoryScope(new ScopeId(TenantId), ToScopeId(ProjectId), ToScopeId(WorkspaceId), ToScopeId(ChatId), ToScopeId(RunId))),
            IdempotencyKey, PayloadFingerprint, MemoryId, Outcome, new ContractVersion(ContractVersion));
    }

    private sealed record PersistedOutboxRow(
        string MessageId,
        string TenantId,
        string? ProjectId,
        string? WorkspaceId,
        string? ChatId,
        string? RunId,
        string Kind,
        string CommandId,
        string CorrelationId,
        string? MemoryId,
        string ContractVersion)
    {
        public static PersistedOutboxRow From(OutboxMessage message) => new(
            message.MessageId, message.Scope.Scope.TenantId.Value, message.Scope.Scope.ProjectId?.Value, message.Scope.Scope.WorkspaceId?.Value,
            message.Scope.Scope.ChatId?.Value, message.Scope.Scope.RunId?.Value, message.Kind, message.CommandId.Value, message.CorrelationId.Value,
            message.MemoryId, message.ContractVersion.Value);
    }

    private sealed record PersistedSchemaManifestRow(
        string TableName,
        string SchemaVersion,
        string SchemaFingerprint,
        string? EmbeddingProvider,
        string? EmbeddingModel,
        string? EmbeddingModelVersion,
        string? EmbeddingDimension,
        string? EmbeddingNormalization)
    {
        public LanceDbEmbeddingSchemaMetadata? Embedding
        {
            get
            {
                var supplied = new[] { EmbeddingProvider, EmbeddingModel, EmbeddingModelVersion, EmbeddingDimension, EmbeddingNormalization };
                if (supplied.All(value => value is null)) return null;
                if (supplied.Any(string.IsNullOrWhiteSpace))
                {
                    throw new LanceDbSchemaMismatchException(
                        TableName,
                        "a complete embedding identity or no embedding identity",
                        "a partially populated embedding identity",
                        "manifest embedding metadata is ambiguous");
                }
                if (!int.TryParse(EmbeddingDimension, CultureInfo.InvariantCulture, out var dimension) || dimension <= 0)
                {
                    throw new LanceDbSchemaMismatchException(
                        TableName,
                        "a positive embedding dimension",
                        EmbeddingDimension ?? "null",
                        "manifest embedding dimension is invalid");
                }
                return new LanceDbEmbeddingSchemaMetadata(
                    EmbeddingProvider!, EmbeddingModel!, EmbeddingModelVersion!, dimension, EmbeddingNormalization!);
            }
        }

        public static PersistedSchemaManifestRow From(LanceDbTableSchemaDefinition definition) => new(
            definition.TableName,
            definition.Version,
            definition.Fingerprint,
            definition.Embedding?.Provider,
            definition.Embedding?.Model,
            definition.Embedding?.ModelVersion,
            definition.Embedding?.Dimension.ToString(CultureInfo.InvariantCulture),
            definition.Embedding?.Normalization);

        public static PersistedSchemaManifestRow Read(RecordBatch batch, int index) => new(
            Required(batch, "table_name", index),
            Required(batch, "schema_version", index),
            Required(batch, "schema_fingerprint", index),
            Value(batch, "embedding_provider", index),
            Value(batch, "embedding_model", index),
            Value(batch, "embedding_model_version", index),
            Value(batch, "embedding_dimension", index),
            Value(batch, "embedding_normalization", index));
    }

    private sealed record PersistedEvidence(string Identity, string? SourceSystem, string? FragmentIdentity, string? ApprovedMetadata)
    {
        public static PersistedEvidence From(SourceEvidenceRef evidence) => new(evidence.Identity, evidence.SourceSystem, evidence.FragmentIdentity, evidence.ApprovedMetadata);
        public SourceEvidenceRef ToModel() => new(Identity, SourceSystem, FragmentIdentity, ApprovedMetadata);
    }

    private sealed record PersistedProvenance(
        string SourceSystem,
        string? LegacyRecordId,
        string? WorkspaceId,
        string? ChatId,
        string? RunId,
        string? MessageId,
        string? ExecutionId,
        IReadOnlyList<PersistedEvidence> Evidence)
    {
        public static PersistedProvenance From(MemoryProvenance provenance) => new(provenance.SourceSystem, provenance.LegacyRecordId,
            provenance.WorkspaceId?.Value, provenance.ChatId?.Value, provenance.RunId?.Value, provenance.MessageId, provenance.ExecutionId,
            provenance.Evidence.Select(PersistedEvidence.From).ToArray());
        public static MemoryProvenance ToModel(PersistedProvenance value) => new(value.SourceSystem, value.LegacyRecordId, ToScopeId(value.WorkspaceId),
            ToScopeId(value.ChatId), ToScopeId(value.RunId), value.MessageId, value.ExecutionId, value.Evidence.Select(item => item.ToModel()).ToArray());
    }

    private sealed record PersistedEmbedding(string Provider, string Model, string ModelVersion, int Dimension, string Normalization, string ContentHash)
    {
        public static PersistedEmbedding From(EmbeddingReference embedding) => new(embedding.Provider, embedding.Model, embedding.ModelVersion,
            embedding.Dimension, embedding.Normalization, embedding.ContentHash);
        public static EmbeddingReference ToModel(PersistedEmbedding value) => new(value.Provider, value.Model, value.ModelVersion,
            value.Dimension, value.Normalization, value.ContentHash);
    }

    private static string? Value(RecordBatch batch, string column, int index) => ((StringArray)batch.Column(column)).GetString(index);
    private static string Required(RecordBatch batch, string column, int index) => Value(batch, column, index)
        ?? throw new InvalidDataException($"Required LanceDB column '{column}' is null.");
    private static ScopeId? ToScopeId(string? value) => value is null ? null : new ScopeId(value);
    private static MemoryScope Scope(IReadOnlyDictionary<string, string?> values) => new(new ScopeId(values["tenant_id"] ?? throw new InvalidDataException("tenant_id is required.")),
        ToScopeId(values["project_id"]), ToScopeId(values["workspace_id"]), ToScopeId(values["chat_id"]), ToScopeId(values["run_id"]));
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonOptions)
        ?? throw new InvalidDataException($"LanceDB JSON could not be converted to {typeof(T).Name}.");
    private static TEnum ParseEnum<TEnum>(string value) where TEnum : struct, Enum => Enum.TryParse<TEnum>(value, out var parsed)
        ? parsed : throw new InvalidDataException($"'{value}' is not a valid {typeof(TEnum).Name}.");

    private static float[]? TryReadVector(RecordBatch batch, int index)
    {
        if (!batch.Schema.FieldsLookup.Contains("vector")) return null;
        var vectors = batch.Column("vector") as FixedSizeListArray
            ?? throw new InvalidDataException("LanceDB vector column is not a fixed-size float list.");
        if (vectors.IsNull(index)) return null;
        var values = vectors.Values as FloatArray
            ?? throw new InvalidDataException("LanceDB vector values are not floats.");
        var length = ((FixedSizeListType)vectors.Data.DataType).ListSize;
        var offset = index * length;
        var result = new float[length];
        for (var i = 0; i < length; i++) result[i] = values.GetValue(offset + i)
            ?? throw new InvalidDataException("LanceDB vector has a null component.");
        return result;
    }
}
