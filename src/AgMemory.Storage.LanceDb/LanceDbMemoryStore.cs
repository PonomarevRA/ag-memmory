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
public sealed partial class LanceDbMemoryStore : IMemoryStore, IMemoryGraphSource, IMemoryReaderSource, IMemoryReaderCatalogSource, IVectorSearch, ILexicalSearch, IAsyncDisposable
{
    /// <summary>The initial, fail-closed schema policy for tables owned by this adapter.</summary>
    public const string CurrentStorageSchemaVersion = "1.0";

    private const string RecordsTable = "memory_records";
    private const string HotMemoryTable = "session_hot_memory";
    private const string ReceiptsTable = "idempotency_receipts";
    private const string OutboxTable = "outbox_messages";
    private const string ReaderRoutesTable = "memory_reader_routes";
    private const string ReaderCatalogGenerationsTable = "memory_reader_catalog_generations";
    private const string ReaderCatalogLeavesTable = "memory_reader_catalog_leaves";
    private const string ReaderCatalogBuildRunsTable = "memory_reader_catalog_build_runs";
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
    private static readonly string[] ReaderSourceColumnNames =
    [
        "id", "tenant_id", "project_id", "workspace_id", "chat_id", "run_id", "record_type", "status",
        "canonical_text", "created_at_utc", "updated_at_utc", "version", "expires_at_utc"
    ];
    private static readonly string[] ReaderCatalogGenerationColumnNames =
    [
        "scope_key", "generation_key", "state", "created_at_utc", "ready_at_utc"
    ];
    private static readonly string[] ReaderCatalogLeafColumnNames =
    [
        "leaf_key", "scope_key", "generation_key", "leaf_position", "memory_id", "record_type", "updated_at_utc", "version"
    ];
    private static readonly string[] ReaderCatalogBuildRunColumnNames =
    [
        "row_key", "generation_key", "run_key", "row_position", "sort_key", "memory_id", "record_type", "updated_at_utc", "version"
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
            HasNonFiniteValue(request.QueryVector.Value.Span))
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

}
