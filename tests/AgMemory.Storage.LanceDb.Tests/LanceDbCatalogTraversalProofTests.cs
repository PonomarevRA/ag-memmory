using Apache.Arrow;
using Apache.Arrow.Types;
using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using lancedb;
using Xunit;
using LanceTable = lancedb.Table;

namespace AgMemory.Storage.LanceDb.Tests;

/// <summary>
/// Compatibility proof for the catalog design gate. This deliberately uses the legacy physical record table through
/// LanceDB's flat scan API rather than the adapter's general memory materializer.
/// </summary>
public sealed class LanceDbCatalogTraversalProofTests
{
    private const string RecordsTable = "memory_records";
    private const string ProbeLeavesTable = "catalog_generation_probe_leaves";
    private const string ReaderCatalogBuildRunsTable = "memory_reader_catalog_build_runs";
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly MemoryScope ScopeA = new(new("tenant-a"), new("project-a"), new("workspace-a"), new("chat-a"), new("run-a"));
    private static readonly MemoryScope ScopeB = new(new("tenant-b"), new("project-b"), new("workspace-b"), new("chat-b"), new("run-b"));
    private static readonly string[] CatalogColumns =
    [
        "id", "tenant_id", "project_id", "workspace_id", "chat_id", "run_id", "record_type", "status",
        "canonical_text", "created_at_utc", "updated_at_utc", "version", "expires_at_utc"
    ];

    [Fact]
    public async Task LegacyRecords_CanBeTraversedInFixedSelectedColumnPortions_AndPersistedAsOneGeneration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-lancedb-catalog-proof-{Guid.NewGuid():N}");
        try
        {
            var expected = Enumerable.Range(0, 53)
                .Select(index => Record($"catalog-{index:D3}", ScopeA, $"catalog record {index:D3}"))
                .ToArray();
            var foreign = Record("catalog-foreign", ScopeB, "foreign record");
            var inactive = Record("catalog-inactive", ScopeA, "inactive record") with { Status = MemoryLifecycleStatus.Invalid };
            var expired = Record("catalog-expired", ScopeA, "expired record") with { ExpiresAt = Now };

            await using (var store = new LanceDbMemoryStore(new(path)))
            {
                await WriteAsync(store, Authorized(ScopeA, ScopeB), [.. expected, foreign, inactive, expired]);
            }

            using (var connection = new Connection())
            {
                await connection.Connect(path);
                using var records = await connection.OpenTable(RecordsTable);
                using var leaves = await connection.CreateEmptyTable(ProbeLeavesTable, new CreateTableOptions { Schema = ProbeLeafSchema() });

                var firstTraversal = await TraverseAndPersistGenerationAsync(records, leaves, "generation-a");
                var secondTraversal = await TraverseAsync(records);

                Assert.Equal([20, 20, 13], firstTraversal.PortionLengths);
                Assert.Equal(firstTraversal.MemoryIds, secondTraversal);
                Assert.Equal(expected.Select(record => record.Id.Value).Order(StringComparer.Ordinal), firstTraversal.MemoryIds.Order(StringComparer.Ordinal));
                Assert.Equal(expected.Length, await leaves.CountRows("generation_key = 'generation-a'"));
            }

            using (var connection = new Connection())
            {
                await connection.Connect(path);
                using var leaves = await connection.OpenTable(ProbeLeavesTable);
                var persisted = await leaves.Query()
                    .Select(["generation_key", "leaf_position", "memory_id"])
                    .Where("generation_key = 'generation-a'")
                    .ToArrow();

                Assert.Equal(expected.Length, persisted.Length);
                Assert.DoesNotContain(persisted.Schema.FieldsList, field => field.Name.Contains("embedding", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task ReaderCatalog_ExposesOnlyEligibleReadyLeavesAndInvalidatesAnOldGenerationAfterMutation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-reader-catalog-{Guid.NewGuid():N}");
        try
        {
            var eligible = Enumerable.Range(0, 53)
                .Select(index => Record($"ready-{index:D3}", ScopeA, $"ready record {index:D3}"))
                .ToArray();
            var foreign = Record("ready-foreign", ScopeB, "foreign");
            var inactive = Record("ready-inactive", ScopeA, "inactive") with { Status = MemoryLifecycleStatus.Invalid };
            var expired = Record("ready-expired", ScopeA, "expired") with { ExpiresAt = Now };
            var eligibility = new MemorySearchEligibility(new(new[] { new ScopeSelector(ScopeA) }), null, Now);

            await using var store = new LanceDbMemoryStore(new(path));
            await WriteAsync(store, Authorized(ScopeA, ScopeB), [.. eligible, foreign, inactive, expired]);

            var first = await store.ReadReadyLeafPageAsync(eligibility, null, default);
            var second = await store.ReadReadyLeafPageAsync(eligibility, first!.NextCursor, default);
            var third = await store.ReadReadyLeafPageAsync(eligibility, second!.NextCursor, default);

            Assert.Equal([20, 20, 13], new[] { first.Entries.Count, second.Entries.Count, third!.Entries.Count });
            Assert.Null(third.NextCursor);
            Assert.Equal(eligible.Select(record => record.Id).OrderBy(id => id.Value, StringComparer.Ordinal),
                first.Entries.Concat(second.Entries).Concat(third.Entries).Select(entry => entry.MemoryId).OrderBy(id => id.Value, StringComparer.Ordinal));

            var updated = eligible[0] with { CanonicalText = "updated", UpdatedAt = Now.AddMinutes(1), Version = 2 };
            await UpdateAsync(store, Authorized(ScopeA), updated, expectedVersion: 1);

            Assert.Null(await store.ReadReadyLeafPageAsync(eligibility, first.NextCursor, default));
            var rebuilt = await store.ReadReadyLeafPageAsync(eligibility, null, default);
            Assert.NotNull(rebuilt);
            Assert.NotEqual(first.GenerationKey, rebuilt!.GenerationKey);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task ReaderCatalog_OrdersAllLeavesGloballyByCreatedAtThenMemoryIdAcrossBuildPortions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-reader-catalog-order-{Guid.NewGuid():N}");
        try
        {
            var records = Enumerable.Range(0, 53)
                .Select(index => Record($"ordered-{index:D3}", ScopeA, $"ordered record {index:D3}") with
                {
                    // Physical insertion is oldest-to-newest; the required catalog order is the inverse.
                    CreatedAt = Now.AddMinutes(53 - index),
                    UpdatedAt = Now.AddMinutes(53 - index)
                })
                .ToArray();
            var eligibility = new MemorySearchEligibility(new(new[] { new ScopeSelector(ScopeA) }), null, Now.AddHours(2));

            await using var store = new LanceDbMemoryStore(new(path));
            await WriteAsync(store, Authorized(ScopeA), records);

            var first = await store.ReadReadyLeafPageAsync(eligibility, null, default);
            var second = await store.ReadReadyLeafPageAsync(eligibility, first!.NextCursor, default);
            var third = await store.ReadReadyLeafPageAsync(eligibility, second!.NextCursor, default);

            var expected = records.OrderBy(record => record.CreatedAt)
                .ThenBy(record => record.Id.Value, StringComparer.Ordinal)
                .Select(record => record.Id)
                .ToArray();
            var actual = first.Entries.Concat(second!.Entries).Concat(third!.Entries)
                .Select(entry => entry.MemoryId)
                .ToArray();

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task ReaderCatalog_DiscardsPersistentMergeStagingAfterPublishingReadyLeaves()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-reader-catalog-staging-{Guid.NewGuid():N}");
        try
        {
            var records = Enumerable.Range(0, 53)
                .Select(index => Record($"staging-{index:D3}", ScopeA, $"staging record {index:D3}"))
                .ToArray();
            var eligibility = new MemorySearchEligibility(new(new[] { new ScopeSelector(ScopeA) }), null, Now.AddHours(1));

            await using var store = new LanceDbMemoryStore(new(path));
            await WriteAsync(store, Authorized(ScopeA), records);
            Assert.NotNull(await store.ReadReadyLeafPageAsync(eligibility, null, default));

            using var connection = new Connection();
            await connection.Connect(path);
            using var staging = await connection.OpenTable(ReaderCatalogBuildRunsTable);
            Assert.Equal(0, await staging.CountRows());
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static async Task<CatalogTraversal> TraverseAndPersistGenerationAsync(LanceTable records, LanceTable leaves, string generation)
    {
        var memoryIds = new List<string>();
        var portionLengths = new List<int>();
        var position = 0;
        for (var sourceOffset = 0; ; sourceOffset += MemoryReaderLimits.DocumentsPerPage)
        {
            var portion = await ReadPortionAsync(records, sourceOffset);
            if (portion.Length == 0) break;

            portionLengths.Add(portion.Length);
            var ids = ReadIds(portion);
            memoryIds.AddRange(ids);
            await leaves.MergeInsert("leaf_key")
                .WhenMatchedUpdateAll()
                .WhenNotMatchedInsertAll()
                .Execute(BuildProbeLeafBatch(generation, position, ids))
                .ConfigureAwait(false);
            position += ids.Count;

            if (portion.Length < MemoryReaderLimits.DocumentsPerPage) break;
        }

        return new(memoryIds, portionLengths);
    }

    private static async Task<IReadOnlyList<string>> TraverseAsync(LanceTable records)
    {
        var memoryIds = new List<string>();
        for (var sourceOffset = 0; ; sourceOffset += MemoryReaderLimits.DocumentsPerPage)
        {
            var portion = await ReadPortionAsync(records, sourceOffset);
            if (portion.Length == 0) break;
            memoryIds.AddRange(ReadIds(portion));
            if (portion.Length < MemoryReaderLimits.DocumentsPerPage) break;
        }

        return memoryIds;
    }

    private static async Task<RecordBatch> ReadPortionAsync(LanceTable records, int sourceOffset)
    {
        var portion = await records.Query()
            .Select(CatalogColumns)
            .Where(ExactEligibleScopePredicate())
            .Limit(MemoryReaderLimits.DocumentsPerPage)
            .Offset(sourceOffset)
            .WithRowId()
            .ToArrow()
            .ConfigureAwait(false);

        Assert.All(portion.Schema.FieldsList, field => Assert.True(
            CatalogColumns.Contains(field.Name, StringComparer.Ordinal) ||
            field.Name.Contains("rowid", StringComparison.OrdinalIgnoreCase),
            $"Unexpected source column '{field.Name}' was materialised."));
        Assert.Contains(portion.Schema.FieldsList, field => field.Name.Contains("rowid", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(portion.Schema.FieldsList, field => field.Name.Contains("embedding", StringComparison.OrdinalIgnoreCase));
        return portion;
    }

    private static IReadOnlyList<string> ReadIds(RecordBatch portion)
    {
        var ids = portion.Column("id") as StringArray
            ?? throw new InvalidDataException("The selected catalog id projection is not a string column.");
        return Enumerable.Range(0, portion.Length)
            .Select(index => ids.GetString(index) ?? throw new InvalidDataException("The selected catalog id is null."))
            .ToArray();
    }

    private static string ExactEligibleScopePredicate() => string.Join(" AND ",
    [
        "tenant_id = 'tenant-a'",
        "project_id = 'project-a'",
        "workspace_id = 'workspace-a'",
        "chat_id = 'chat-a'",
        "run_id = 'run-a'",
        "status = 'Active'",
        $"(expires_at_utc IS NULL OR expires_at_utc > '{Now:O}')"
    ]);

    private static Schema ProbeLeafSchema() => new Schema.Builder()
        .Field(new Field("leaf_key", StringType.Default, nullable: false))
        .Field(new Field("generation_key", StringType.Default, nullable: false))
        .Field(new Field("leaf_position", StringType.Default, nullable: false))
        .Field(new Field("memory_id", StringType.Default, nullable: false))
        .Build();

    private static RecordBatch BuildProbeLeafBatch(string generation, int firstPosition, IReadOnlyList<string> ids)
    {
        var leafKeys = new StringArray.Builder();
        var generations = new StringArray.Builder();
        var positions = new StringArray.Builder();
        var memoryIds = new StringArray.Builder();
        foreach (var item in ids.Select((id, offset) => new { Id = id, Position = firstPosition + offset }))
        {
            leafKeys.Append($"{generation}:{item.Position:D8}");
            generations.Append(generation);
            positions.Append(item.Position.ToString(System.Globalization.CultureInfo.InvariantCulture));
            memoryIds.Append(item.Id);
        }

        return new RecordBatch(ProbeLeafSchema(), [leafKeys.Build(), generations.Build(), positions.Build(), memoryIds.Build()], ids.Count);
    }

    private static async Task WriteAsync(LanceDbMemoryStore store, AuthorizedScopeSet scopes, IReadOnlyList<MemoryRecord> records)
    {
        await using var transaction = await store.BeginTransactionAsync(default);
        var writes = records.Select(record => new ConditionalRecordWrite(record, 0)).ToArray();
        Assert.All(await transaction.WriteRecordsAsync(scopes, writes, default), result => Assert.True(result.Applied));
        await transaction.CommitAsync(default);
    }

    private static async Task UpdateAsync(LanceDbMemoryStore store, AuthorizedScopeSet scopes, MemoryRecord record, long expectedVersion)
    {
        await using var transaction = await store.BeginTransactionAsync(default);
        Assert.True((await transaction.WriteRecordAsync(scopes, record, expectedVersion, default)).Applied);
        await transaction.CommitAsync(default);
    }

    private static AuthorizedScopeSet Authorized(params MemoryScope[] scopes) => new(scopes.Select(scope => new ScopeSelector(scope)));

    private static MemoryRecord Record(string id, MemoryScope scope, string canonicalText) => new(
        new MemoryId(id), scope, MemoryRecordType.Fact, MemoryLifecycleStatus.Active, canonicalText, null, .7, .8, 5,
        Now, Now, 1, ["catalog"], new("proof", null, null, null, null, null, null, [new($"evidence-{id}")]),
        null, null, $"dedup-{id}");

    private sealed record CatalogTraversal(IReadOnlyList<string> MemoryIds, IReadOnlyList<int> PortionLengths);
}
