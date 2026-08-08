using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using AgMemory.Contracts;
using lancedb;

namespace AgMemory.Storage.LanceDb;

/// <summary>
/// Local LanceDB implementation of the provider-neutral durable-memory and search ports.
/// LanceDB, Arrow schemas and SQL-like predicates are deliberately implementation details.
/// </summary>
public sealed class LanceDbMemoryStore : IMemoryStore, IVectorSearch, ILexicalSearch, IAsyncDisposable
{
    /// <summary>The initial, fail-closed schema policy for tables owned by this adapter.</summary>
    public const string CurrentStorageSchemaVersion = "1.0";

    private const string RecordsTable = "memory_records";
    private const string HotMemoryTable = "session_hot_memory";
    private const string ReceiptsTable = "idempotency_receipts";
    private const string OutboxTable = "outbox_messages";
    private const string SchemaManifestTable = "agmemory_schema_manifest";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] MemoryColumnNames =
    [
        "id", "tenant_id", "project_id", "workspace_id", "chat_id", "run_id", "record_type", "status",
        "canonical_text", "reason", "importance", "confidence", "estimated_token_cost", "created_at_utc",
        "updated_at_utc", "version", "entities_json", "provenance_json", "embedding_json", "embedding_vector_json",
        "expires_at_utc", "deduplication_key", "decision_details_json"
    ];
    private static readonly string[] SchemaManifestColumnNames =
    [
        "table_name", "schema_version", "schema_fingerprint", "embedding_provider", "embedding_model",
        "embedding_model_version", "embedding_dimension", "embedding_normalization"
    ];

    private readonly LanceDbMemoryStoreOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Connection? _connection;
    private bool _initialized;
    private bool _disposed;

    public LanceDbMemoryStore(LanceDbMemoryStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<IMemoryStoreTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return new Transaction(this);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public async Task<MemoryRecord?> GetAsync(AuthorizedScopeSet scopes, MemoryId id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return await ReadRecordAsync(id, scopes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListAsync(AuthorizedScopeSet scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadRecordsAsync(BuildAuthorizedPredicate(scopes), cancellationToken).ConfigureAwait(false);
            return records.OrderBy(record => record.Id.Value, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionHotMemory?> GetHotMemoryAsync(
        AuthorizedScopeSet scopes,
        MemoryScope exactScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(exactScope);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return await ReadHotMemoryAsync(exactScope, scopes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the validated physical-schema and embedding identities for migration manifests
    /// and benchmarks. This method exposes no LanceDB or Arrow types.
    /// </summary>
    public async Task<LanceDbSchemaManifest> GetSchemaManifestAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var entries = await ReadSchemaManifestRowsAsync(cancellationToken).ConfigureAwait(false);
            var tables = new List<LanceDbTableSchemaMetadata>(entries.Count);
            foreach (var entry in entries.OrderBy(entry => entry.TableName, StringComparer.Ordinal))
            {
                var definition = DefinitionFromManifest(entry);
                using var table = await OpenTableAsync(definition.TableName, cancellationToken).ConfigureAwait(false);
                LanceDbSchemaFingerprint.RequireMatch(definition, await table.Schema().ConfigureAwait(false));
                tables.Add(definition.ToMetadata());
            }
            return new LanceDbSchemaManifest(CurrentStorageSchemaVersion, tables);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SearchPortCandidate>> SearchAsync(SearchPortRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Eligibility);
        if (request.Limit <= 0 || string.IsNullOrWhiteSpace(request.QueryText)) return [];

        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadRecordsAsync(BuildEligibilityPredicate(request.Eligibility), cancellationToken).ConfigureAwait(false);
            var terms = Tokenize(request.QueryText);
            if (terms.Count == 0) return [];

            // This deterministic local scorer is intentionally the lexical fallback until FTS/index policy is accepted.
            // The physical LanceDB predicate has already bounded rows by exact scope/lifecycle/expiry before scoring.
            var scored = records
                .Where(request.Eligibility.IsEligible)
                .Select(record => new { Record = record, Score = LexicalScore(record.CanonicalText, terms) })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Record.Id.Value, StringComparer.Ordinal)
                .Take(request.Limit)
                .Select((item, index) => new SearchPortCandidate(item.Record, index + 1, item.Score, item.Record.Embedding))
                .ToArray();
            return scored;
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<IReadOnlyList<SearchPortCandidate>> IVectorSearch.SearchAsync(
        SearchPortRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Eligibility);
        if (request.Limit <= 0 || request.QueryVector is null) return [];
        if (request.EmbeddingContract is null || request.QueryEmbedding is null)
            throw new ArgumentException("Vector search requires a query vector, embedding reference, and embedding contract.", nameof(request));
        if (!request.EmbeddingContract.Matches(request.QueryEmbedding) ||
            request.QueryVector.Value.Length != request.EmbeddingContract.Dimension ||
            request.QueryVector.Value.Span.ToArray().Any(value => !float.IsFinite(value)))
            throw new ArgumentException("The query vector must match the supplied embedding contract.", nameof(request));

        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var tableName = VectorTableName(request.EmbeddingContract);
            if (!await TableExistsAsync(tableName, cancellationToken).ConfigureAwait(false)) return [];

            using var table = await OpenTableAsync(tableName, cancellationToken).ConfigureAwait(false);
            var batches = await table.Query()
                .NearestTo(request.QueryVector.Value.ToArray())
                .DistanceType(DistanceType.Cosine)
                .Where(BuildEligibilityPredicate(request.Eligibility))
                .Limit(request.Limit)
                .ToArrow()
                .ConfigureAwait(false);

            // LanceDB returns nearest-neighbour order. Filter checked records before assigning port ranks.
            var records = ReadMemoryRecords(batches)
                .Where(request.Eligibility.IsEligible)
                .Where(record => record.Embedding is not null && request.EmbeddingContract.Matches(record.Embedding))
                .Where(record => record.EmbeddingVector is { } vector && vector.Length == request.EmbeddingContract.Dimension)
                .Take(request.Limit)
                .Select((record, index) => new SearchPortCandidate(record, index + 1, 1d / (index + 1), record.Embedding))
                .ToArray();
            return records;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _connection?.Dispose();
            _connection = null;
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized) return;

        var path = Path.GetFullPath(_options.StoragePath);
        Directory.CreateDirectory(path);
        _connection = new Connection();
        await _connection.Connect(path).ConfigureAwait(false);
        await EnsureTableSchemaAsync(SchemaManifestDefinition(), cancellationToken).ConfigureAwait(false);
        foreach (var definition in CoreTableDefinitions())
            await EnsureTableSchemaAsync(definition, cancellationToken).ConfigureAwait(false);
        await ValidateAndRegisterVectorTablesAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task ValidateAndRegisterVectorTablesAsync(CancellationToken cancellationToken)
    {
        var names = await ConnectionOrThrow().TableNames().ConfigureAwait(false);
        foreach (var tableName in names.Where(name => name.StartsWith("memory_vectors_", StringComparison.Ordinal)))
        {
            var manifestEntry = await ReadSchemaManifestRowAsync(tableName, cancellationToken).ConfigureAwait(false);
            if (manifestEntry is not null)
            {
                var definition = DefinitionFromManifest(manifestEntry);
                using var table = await OpenTableAsync(tableName, cancellationToken).ConfigureAwait(false);
                LanceDbSchemaFingerprint.RequireMatch(definition, await table.Schema().ConfigureAwait(false));
                continue;
            }

            using var legacyTable = await OpenTableAsync(tableName, cancellationToken).ConfigureAwait(false);
            var legacySchema = await legacyTable.Schema().ConfigureAwait(false);
            var vectorField = legacySchema.FieldsList.SingleOrDefault(field => field.Name == "vector");
            if (vectorField is null || vectorField.DataType is not FixedSizeListType vectorType || vectorType.ListSize <= 0)
            {
                throw new LanceDbSchemaMismatchException(
                    tableName,
                    "a versioned vector table with a fixed-size vector column",
                    LanceDbSchemaFingerprint.Create(legacySchema),
                    "unregistered vector table has no usable vector schema");
            }

            var batches = await legacyTable.Query().Limit(1).ToArrow().ConfigureAwait(false);
            var firstRecord = ReadMemoryRecords(batches).FirstOrDefault();
            if (firstRecord?.Embedding is null)
            {
                throw new LanceDbSchemaMismatchException(
                    tableName,
                    "a manifest entry or a non-empty legacy vector table with embedding metadata",
                    "no embedding metadata",
                    "the embedding identity cannot be inferred safely");
            }

            var definitionForLegacyTable = VectorTableDefinition(firstRecord.Embedding);
            if (!string.Equals(definitionForLegacyTable.TableName, tableName, StringComparison.Ordinal))
            {
                throw new LanceDbSchemaMismatchException(
                    tableName,
                    definitionForLegacyTable.TableName,
                    tableName,
                    "legacy vector table name does not match its embedded identity");
            }
            if (firstRecord.Embedding.Dimension != vectorType.ListSize)
            {
                throw new LanceDbSchemaMismatchException(
                    tableName,
                    firstRecord.Embedding.Dimension.ToString(CultureInfo.InvariantCulture),
                    vectorType.ListSize.ToString(CultureInfo.InvariantCulture),
                    "legacy vector dimension disagrees with embedding metadata");
            }
            LanceDbSchemaFingerprint.RequireMatch(definitionForLegacyTable, legacySchema);
            await VerifyOrRegisterSchemaManifestAsync(definitionForLegacyTable, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureTableSchemaAsync(LanceDbTableSchemaDefinition definition, CancellationToken cancellationToken)
    {
        using var table = await OpenOrCreateTableAsync(definition.TableName, definition.Schema, cancellationToken).ConfigureAwait(false);
        LanceDbSchemaFingerprint.RequireMatch(definition, await table.Schema().ConfigureAwait(false));
        await VerifyOrRegisterSchemaManifestAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyOrRegisterSchemaManifestAsync(LanceDbTableSchemaDefinition definition, CancellationToken cancellationToken)
    {
        var persisted = await ReadSchemaManifestRowAsync(definition.TableName, cancellationToken).ConfigureAwait(false);
        if (persisted is not null)
        {
            if (!string.Equals(persisted.SchemaVersion, definition.Version, StringComparison.Ordinal) ||
                !string.Equals(persisted.SchemaFingerprint, definition.Fingerprint, StringComparison.Ordinal) ||
                persisted.Embedding != definition.Embedding)
            {
                throw new LanceDbSchemaMismatchException(
                    definition.TableName,
                    $"version={definition.Version}; fingerprint={definition.Fingerprint}; embedding={definition.Embedding}",
                    $"version={persisted.SchemaVersion}; fingerprint={persisted.SchemaFingerprint}; embedding={persisted.Embedding}",
                    "registered manifest entry disagrees with the adapter schema policy");
            }
            return;
        }

        using var table = await OpenTableAsync(SchemaManifestTable, cancellationToken).ConfigureAwait(false);
        await table.MergeInsert("table_name")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(BuildSchemaManifestBatch([PersistedSchemaManifestRow.From(definition)]))
            .ConfigureAwait(false);
    }

    private async Task<lancedb.Table> OpenOrCreateTableAsync(string tableName, Schema schema, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await TableExistsAsync(tableName, cancellationToken).ConfigureAwait(false))
            return await OpenTableAsync(tableName, cancellationToken).ConfigureAwait(false);
        return await ConnectionOrThrow().CreateEmptyTable(tableName, new CreateTableOptions { Schema = schema }).ConfigureAwait(false);
    }

    private async Task<lancedb.Table> OpenTableAsync(string tableName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await ConnectionOrThrow().OpenTable(tableName).ConfigureAwait(false);
    }

    private async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var names = await ConnectionOrThrow().TableNames().ConfigureAwait(false);
        return names.Contains(tableName, StringComparer.Ordinal);
    }

    private Connection ConnectionOrThrow() => _connection ?? throw new InvalidOperationException("LanceDB connection has not been initialized.");

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

    private async Task<IReadOnlyList<MemoryRecord>> ReadRecordsAsync(string predicate, CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query().Where(predicate).ToArrow().ConfigureAwait(false);
        return ReadMemoryRecords(batches).ToArray();
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

    private static bool IsNullableMemoryColumn(string column) => column is
        "project_id" or "workspace_id" or "chat_id" or "run_id" or "reason" or "embedding_json" or
        "embedding_vector_json" or "expires_at_utc" or "decision_details_json";

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

    private static string BuildAuthorizedPredicate(AuthorizedScopeSet scopes) =>
        $"({string.Join(" OR ", scopes.Selectors.Select(BuildSelectorPredicate))})";

    private static string BuildEligibilityPredicate(MemorySearchEligibility eligibility)
    {
        var clauses = new List<string>
        {
            BuildAuthorizedPredicate(eligibility.AuthorizedScopes),
            $"status = {Literal(eligibility.RequiredLifecycleStatus.ToString())}",
            $"(expires_at_utc IS NULL OR expires_at_utc > {Literal(Utc(eligibility.AsOfUtc))})"
        };
        if (eligibility.Types is { Count: > 0 })
            clauses.Add($"record_type IN ({string.Join(", ", eligibility.Types.OrderBy(type => type).Select(type => Literal(type.ToString())))})");
        return string.Join(" AND ", clauses.Select(clause => $"({clause})"));
    }

    private static string BuildSelectorPredicate(ScopeSelector selector) => string.Join(" AND ",
    [
        $"tenant_id = {Literal(selector.Scope.TenantId.Value)}",
        NullableScopePredicate("project_id", selector.Scope.ProjectId),
        NullableScopePredicate("workspace_id", selector.Scope.WorkspaceId),
        NullableScopePredicate("chat_id", selector.Scope.ChatId),
        NullableScopePredicate("run_id", selector.Scope.RunId)
    ]);

    private static string NullableScopePredicate(string column, ScopeId? value) => value is { } supplied
        ? $"{column} = {Literal(supplied.Value)}"
        : $"{column} IS NULL";

    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string ScopeKey(MemoryScope scope) => string.Concat(
        ScopedValue(scope.TenantId.Value), ScopedValue(scope.ProjectId?.Value), ScopedValue(scope.WorkspaceId?.Value),
        ScopedValue(scope.ChatId?.Value), ScopedValue(scope.RunId?.Value));

    private static string ScopedValue(string? value) => value is null ? "-1:" : string.Create(CultureInfo.InvariantCulture, $"{value.Length}:{value}");

    private static string ReceiptKey(string kind, ScopeSelector scope, string key) => Hash($"{kind}\u001f{ScopeKey(scope.Scope)}\u001f{key}");

    private static string VectorTableName(EmbeddingContract contract) => VectorTableName(
        new EmbeddingReference(contract.Provider, contract.Model, contract.ModelVersion, contract.Dimension, contract.Normalization, "contract"));

    private static string VectorTableName(EmbeddingReference embedding) => $"memory_vectors_{Hash(EmbeddingKey(embedding)!)[..24]}";

    private static string? EmbeddingKey(EmbeddingReference? embedding) => embedding is null
        ? null
        : string.Concat(embedding.Provider, "\u001f", embedding.Model, "\u001f", embedding.ModelVersion, "\u001f",
            embedding.Dimension.ToString(CultureInfo.InvariantCulture), "\u001f", embedding.Normalization);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value)
    {
        var parsed = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (parsed.Offset != TimeSpan.Zero)
            throw new InvalidDataException("LanceDB timestamp values must be persisted with a UTC offset.");
        return parsed;
    }

    private static IReadOnlyList<string> Tokenize(string text) => text.Split(
        [' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(token => token.ToUpperInvariant())
        .Where(token => token.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static double LexicalScore(string content, IReadOnlyList<string> queryTerms)
    {
        var candidateTerms = Tokenize(content).ToHashSet(StringComparer.Ordinal);
        var matches = queryTerms.Count(candidateTerms.Contains);
        return matches == 0 ? 0d : (double)matches / queryTerms.Count;
    }

    private static void ValidateMemoryRecordForSchema(MemoryRecord record)
    {
        record.Validate();
        if (!Enum.IsDefined(record.Type))
            throw new ArgumentOutOfRangeException(nameof(record.Type), record.Type, "Memory record type must be a defined contract value.");
        if (!Enum.IsDefined(record.Status))
            throw new ArgumentOutOfRangeException(nameof(record.Status), record.Status, "Memory record status must be a defined contract value.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LanceDbMemoryStore));
    }

    private sealed class Transaction(LanceDbMemoryStore owner) : IMemoryStoreTransaction
    {
        private readonly Dictionary<MemoryId, MemoryRecord?> _recordChanges = [];
        private readonly Dictionary<string, SessionHotMemory> _hotChanges = [];
        private readonly Dictionary<string, IdempotencyReceipt> _receiptChanges = [];
        private readonly List<OutboxMessage> _outboxChanges = [];
        private readonly Dictionary<MemoryId, MemoryRecord?> _recordSnapshots = [];
        private readonly Dictionary<string, SessionHotMemory?> _hotSnapshots = [];
        private bool _committed;
        private bool _disposed;

        public async Task<IdempotencyReceipt?> FindReceiptAsync(
            AuthorizedScopeSet authorizedScopes,
            string commandKind,
            ScopeSelector scope,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            if (!authorizedScopes.Contains(scope.Scope)) return null;
            var key = ReceiptKey(commandKind, scope, idempotencyKey);
            return _receiptChanges.TryGetValue(key, out var changed)
                ? changed
                : await owner.ReadReceiptAsync(authorizedScopes, commandKind, scope, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }

        public async Task<MemoryRecord?> FindByDeduplicationKeyAsync(
            AuthorizedScopeSet authorizedScopes,
            ScopeSelector scope,
            string deduplicationKey,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            if (!authorizedScopes.Contains(scope.Scope)) return null;
            var candidates = new List<MemoryRecord>();
            var predicate = $"({BuildSelectorPredicate(scope)}) AND deduplication_key = {Literal(deduplicationKey)}";
            candidates.AddRange(await owner.ReadRecordsAsync(predicate, cancellationToken).ConfigureAwait(false));
            candidates.AddRange(_recordChanges.Values.Where(record => record is not null).Cast<MemoryRecord>()
                .Where(record => record.Scope == scope.Scope && record.DeduplicationKey == deduplicationKey));
            return candidates.Where(record => authorizedScopes.Contains(record.Scope)).OrderBy(record => record.Id.Value, StringComparer.Ordinal).FirstOrDefault();
        }

        public async Task<MemoryRecord?> FindRecordAsync(AuthorizedScopeSet scopes, MemoryId id, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            var record = await CurrentRecordAsync(id, cancellationToken).ConfigureAwait(false);
            return record is not null && scopes.Contains(record.Scope) ? record : null;
        }

        public async Task<SessionHotMemory?> FindHotMemoryAsync(
            AuthorizedScopeSet scopes,
            MemoryScope exactScope,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            if (!scopes.Contains(exactScope)) return null;
            var key = ScopeKey(exactScope);
            var memory = await CurrentHotMemoryAsync(exactScope, cancellationToken).ConfigureAwait(false);
            return memory is not null && scopes.Contains(memory.Scope) && _hotChanges.ContainsKey(key)
                ? memory
                : memory is not null && scopes.Contains(memory.Scope) ? memory : null;
        }

        public async Task<ConditionalWriteResult> WriteRecordAsync(
            AuthorizedScopeSet authorizedScopes,
            MemoryRecord record,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(record);
            ValidateMemoryRecordForSchema(record);
            if (!authorizedScopes.Contains(record.Scope)) return new(false, null);
            var existing = await CurrentRecordAsync(record.Id, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!authorizedScopes.Contains(existing.Scope) || expectedVersion == 0 || existing.Version != expectedVersion)
                    return new(false, authorizedScopes.Contains(existing.Scope) ? existing.Version : null);
            }
            else if (expectedVersion != 0)
            {
                return new(false, null);
            }

            _recordChanges[record.Id] = record;
            return new(true, record.Version);
        }

        public async Task<IReadOnlyList<ConditionalWriteResult>> WriteRecordsAsync(
            AuthorizedScopeSet authorizedScopes,
            IReadOnlyList<ConditionalRecordWrite> writes,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(writes);
            var results = new List<ConditionalWriteResult>(writes.Count);
            foreach (var write in writes)
                results.Add(await WriteRecordAsync(authorizedScopes, write.Record, write.ExpectedVersion, cancellationToken).ConfigureAwait(false));
            return results;
        }

        public async Task<ConditionalWriteResult> DeleteRecordAsync(
            AuthorizedScopeSet authorizedScopes,
            MemoryRecord record,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(record);
            var existing = await CurrentRecordAsync(record.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null) return new(false, null);
            if (!authorizedScopes.Contains(record.Scope) || !authorizedScopes.Contains(existing.Scope)) return new(false, null);
            if (existing.Version != expectedVersion) return new(false, existing.Version);
            _recordChanges[record.Id] = null;
            return new(true, null);
        }

        public async Task<ConditionalWriteResult> WriteHotMemoryAsync(
            AuthorizedScopeSet authorizedScopes,
            SessionHotMemory memory,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(memory);
            memory.Validate();
            if (!authorizedScopes.Contains(memory.Scope)) return new(false, null);
            var existing = await CurrentHotMemoryAsync(memory.Scope, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!authorizedScopes.Contains(existing.Scope) || expectedVersion == 0 || existing.Version != expectedVersion)
                    return new(false, authorizedScopes.Contains(existing.Scope) ? existing.Version : null);
            }
            else if (expectedVersion != 0)
            {
                return new(false, null);
            }

            _hotChanges[ScopeKey(memory.Scope)] = memory;
            return new(true, memory.Version);
        }

        public Task SaveReceiptAsync(AuthorizedScopeSet authorizedScopes, IdempotencyReceipt receipt, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(receipt);
            cancellationToken.ThrowIfCancellationRequested();
            RequireAuthorized(authorizedScopes, receipt.Scope);
            _receiptChanges[ReceiptKey(receipt.CommandKind, receipt.Scope, receipt.IdempotencyKey)] = receipt;
            return Task.CompletedTask;
        }

        public Task EnqueueAsync(AuthorizedScopeSet authorizedScopes, OutboxMessage message, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            RequireAuthorized(authorizedScopes, message.Scope);
            _outboxChanges.Add(message);
            return Task.CompletedTask;
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            foreach (var change in _recordChanges)
            {
                var previous = _recordSnapshots.GetValueOrDefault(change.Key);
                if (change.Value is null) await owner.DeleteRecordAsync(previous!, cancellationToken).ConfigureAwait(false);
                else await owner.PersistRecordAsync(change.Value, previous, cancellationToken).ConfigureAwait(false);
            }
            foreach (var memory in _hotChanges.Values)
                await owner.PersistHotMemoryAsync(memory, cancellationToken).ConfigureAwait(false);
            foreach (var receipt in _receiptChanges.Values)
                await owner.PersistReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
            foreach (var message in _outboxChanges)
                await owner.PersistOutboxMessageAsync(message, cancellationToken).ConfigureAwait(false);
            _committed = true;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            owner._gate.Release();
            return ValueTask.CompletedTask;
        }

        private async Task<MemoryRecord?> CurrentRecordAsync(MemoryId id, CancellationToken cancellationToken)
        {
            if (_recordChanges.TryGetValue(id, out var changed)) return changed;
            if (_recordSnapshots.TryGetValue(id, out var snapshot)) return snapshot;
            var loaded = await owner.ReadRecordUnscopedAsync(id, cancellationToken).ConfigureAwait(false);
            _recordSnapshots[id] = loaded;
            return loaded;
        }

        private async Task<SessionHotMemory?> CurrentHotMemoryAsync(MemoryScope scope, CancellationToken cancellationToken)
        {
            var key = ScopeKey(scope);
            if (_hotChanges.TryGetValue(key, out var changed)) return changed;
            if (_hotSnapshots.TryGetValue(key, out var snapshot)) return snapshot;
            var allScopes = new AuthorizedScopeSet([new ScopeSelector(scope)]);
            var loaded = await owner.ReadHotMemoryAsync(scope, allScopes, cancellationToken).ConfigureAwait(false);
            _hotSnapshots[key] = loaded;
            return loaded;
        }

        private void ThrowIfUnavailable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Transaction));
            if (_committed) throw new InvalidOperationException("A LanceDB memory transaction cannot be reused after commit.");
        }

        private static void RequireAuthorized(AuthorizedScopeSet authorizedScopes, ScopeSelector scope)
        {
            if (!authorizedScopes.Contains(scope.Scope))
                throw new UnauthorizedAccessException("A storage transaction cannot write outside its authorized exact selectors.");
        }
    }

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
