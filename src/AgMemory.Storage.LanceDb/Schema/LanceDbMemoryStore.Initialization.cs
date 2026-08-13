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
        {
            await MigrateKnownReaderWikiRelationsSchemaAsync(definition, cancellationToken).ConfigureAwait(false);
            await EnsureTableSchemaAsync(definition, cancellationToken).ConfigureAwait(false);
        }
        await ValidateAndRegisterVectorTablesAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    /// <summary>
    /// Upgrades the one released reader-cache schema that predated
    /// <c>shared_entity_count</c>. This is deliberately narrow: any schema other than the
    /// exact six-column predecessor remains fail-closed under the normal schema policy.
    /// </summary>
    private async Task MigrateKnownReaderWikiRelationsSchemaAsync(
        LanceDbTableSchemaDefinition definition,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(definition.TableName, ReaderWikiRelationsTable, StringComparison.Ordinal) ||
            !await TableExistsAsync(definition.TableName, cancellationToken).ConfigureAwait(false))
            return;

        using var legacy = await OpenTableAsync(definition.TableName, cancellationToken).ConfigureAwait(false);
        var actual = await legacy.Schema().ConfigureAwait(false);
        if (string.Equals(LanceDbSchemaFingerprint.Create(actual), definition.Fingerprint, StringComparison.Ordinal)) return;
        if (!string.Equals(
                LanceDbSchemaFingerprint.Create(actual),
                LanceDbSchemaFingerprint.Create(CreateLegacyReaderWikiRelationsSchema()),
                StringComparison.Ordinal))
            return;

        // LanceDB applies this calculated value to every existing row, so relation data is
        // retained and the new required field is never null.
        await legacy.AddColumns(new Dictionary<string, string>
        {
            ["shared_entity_count"] = "'0'"
        }).ConfigureAwait(false);
        LanceDbSchemaFingerprint.RequireMatch(definition, await legacy.Schema().ConfigureAwait(false));

        using var manifest = await OpenTableAsync(SchemaManifestTable, cancellationToken).ConfigureAwait(false);
        await manifest.MergeInsert("table_name")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(BuildSchemaManifestBatch([PersistedSchemaManifestRow.From(definition)]))
            .ConfigureAwait(false);
    }

    private static Schema CreateLegacyReaderWikiRelationsSchema() => new Schema.Builder()
        .Field(new Field("relation_key", StringType.Default, nullable: false))
        .Field(new Field("generation_key", StringType.Default, nullable: false))
        .Field(new Field("source_memory_id", StringType.Default, nullable: false))
        .Field(new Field("target_memory_id", StringType.Default, nullable: false))
        .Field(new Field("kind", StringType.Default, nullable: false))
        .Field(new Field("label", StringType.Default, nullable: false))
        .Build();

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
}
