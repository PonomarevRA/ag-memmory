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
        "canonical_text", "created_at_utc", "updated_at_utc", "version", "expires_at_utc", "entities_json"
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

    [Fact]
    public async Task ReaderCatalog_WikiRelations_AreSemanticDeduplicatedStableAndBoundedAcrossSourcePortions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-reader-wiki-relations-{Guid.NewGuid():N}");
        try
        {
            var targets = Enumerable.Range(0, 130).Select(index => Record($"target-{index:D3}", ScopeA, $"target {index:D3}")).ToArray();
            var outlinks = string.Join(' ', Enumerable.Range(0, 65)
                .Select(index => $"[[doc:docs/t-{index:D3}|target {index:D3}]]"));
            var related = string.Join(' ', Enumerable.Range(65, 65)
                .Select(index => $"[[related:docs/t-{index:D3}|target {index:D3}]]"));
            var main = Record("a-source-main", ScopeA,
                string.Concat(outlinks, ' ', related, " [[doc:docs/t-000|z label]] [[doc:docs/t-000|a label]] [[related:docs/t-000|related duplicate]]"));
            var backlinkSources = Enumerable.Range(0, 51)
                .Select(index => Record($"back-source-{index:D3}", ScopeA, "[[doc:docs/t-000|backlink]]"))
                .ToArray();
            var eligibility = new MemorySearchEligibility(new(new[] { new ScopeSelector(ScopeA) }), null, Now.AddHours(1));

            await using var store = new LanceDbMemoryStore(new(path));
            await WriteAsync(store, Authorized(ScopeA), [.. targets, main, .. backlinkSources]);
            foreach (var target in targets)
                await store.UpsertWikiMetadataAsync(new(ScopeA, target.Id, 1, target.Id.Value, "docs", $"t-{target.Id.Value[^3..]}", []), default);

            var generation = await store.ReadReadyLeafPageAsync(eligibility, null, default);
            Assert.NotNull(generation);
            var mainChildren = await store.ReadWikiRelationsAsync(eligibility, generation!.GenerationKey, main.Id,
                MemoryWikiRelationKind.Child, default);
            var mainRelated = await store.ReadWikiRelationsAsync(eligibility, generation.GenerationKey, main.Id,
                MemoryWikiRelationKind.Related, default);
            var inverseRelated = await store.ReadWikiRelationsAsync(eligibility, generation.GenerationKey, targets[65].Id,
                MemoryWikiRelationKind.Related, default);
            var backlinks = await store.ReadWikiRelationsAsync(eligibility, generation.GenerationKey, targets[0].Id,
                MemoryWikiRelationKind.Backlink, default);

            Assert.Equal(MemoryWikiLimits.MaximumChildrenPerDocument, mainChildren.Count);
            Assert.Equal(MemoryWikiLimits.MaximumRelatedPerDocument, mainRelated.Count);
            Assert.Equal(mainChildren.Select(link => link.TargetMemoryId.Value).OrderBy(value => value, StringComparer.Ordinal),
                mainChildren.Select(link => link.TargetMemoryId.Value));
            Assert.Equal(mainRelated.Select(link => link.TargetMemoryId.Value).OrderBy(value => value, StringComparer.Ordinal),
                mainRelated.Select(link => link.TargetMemoryId.Value));
            Assert.Equal("a label", Assert.Single(mainChildren, link => link.TargetMemoryId == targets[0].Id).Label);
            Assert.Equal("related duplicate", Assert.Single(mainRelated, link => link.TargetMemoryId == targets[0].Id).Label);
            Assert.Equal("target 065", Assert.Single(inverseRelated, link => link.TargetMemoryId == main.Id).Label);
            Assert.Equal(MemoryWikiLimits.MaximumBacklinksPerPage, backlinks.Count);
            Assert.Equal(backlinks.Select(link => link.TargetMemoryId.Value).OrderBy(value => value, StringComparer.Ordinal),
                backlinks.Select(link => link.TargetMemoryId.Value));
            Assert.Contains(backlinks, link => link.TargetMemoryId == main.Id);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task ReaderCatalog_ProjectsSharedEntitiesAsBoundedWeightedRelatedLinks()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-reader-shared-entities-{Guid.NewGuid():N}");
        try
        {
            var source = Record("shared-source", ScopeA, "source", ["Alpha", "beta"]);
            var linked = Record("shared-linked", ScopeA, "linked title", ["alpha", "BETA"]);
            var oneEntity = Record("shared-one", ScopeA, "one title", ["alpha"]);
            var unrelated = Record("shared-none", ScopeA, "none title", ["gamma"]);
            var explicitLink = Record("shared-explicit", ScopeA, "[[related:docs/shared-source|Curated source]]", ["alpha"]);
            var eligibility = new MemorySearchEligibility(new(new[] { new ScopeSelector(ScopeA) }), null, Now.AddHours(1));

            await using var store = new LanceDbMemoryStore(new(path));
            await WriteAsync(store, Authorized(ScopeA), [source, linked, oneEntity, unrelated, explicitLink]);
            await store.UpsertWikiMetadataAsync(new(ScopeA, source.Id, 1, "source", "docs", "shared-source", []), default);

            var generation = await store.ReadReadyLeafPageAsync(eligibility, null, default);
            Assert.NotNull(generation);
            var sourceRelations = await store.ReadWikiRelationsAsync(eligibility, generation!.GenerationKey, source.Id,
                MemoryWikiRelationKind.Related, default);
            var explicitRelations = await store.ReadWikiRelationsAsync(eligibility, generation.GenerationKey, explicitLink.Id,
                MemoryWikiRelationKind.Related, default);

            Assert.Equal(new[] { linked.Id, oneEntity.Id, explicitLink.Id }.OrderBy(id => id.Value, StringComparer.Ordinal),
                sourceRelations.Select(relation => relation.TargetMemoryId).OrderBy(id => id.Value, StringComparer.Ordinal));
            Assert.Equal(2, Assert.Single(sourceRelations, relation => relation.TargetMemoryId == linked.Id).SharedEntityCount);
            Assert.Equal(1, Assert.Single(sourceRelations, relation => relation.TargetMemoryId == oneEntity.Id).SharedEntityCount);
            Assert.DoesNotContain(sourceRelations, relation => relation.TargetMemoryId == unrelated.Id);
            Assert.Equal("Curated source", Assert.Single(explicitRelations, relation => relation.TargetMemoryId == source.Id).Label);
            Assert.Equal(1, Assert.Single(explicitRelations, relation => relation.TargetMemoryId == source.Id).SharedEntityCount);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task ReaderCatalog_CuratedRelatedLinkDisplacesAutomaticCandidateWhenTheLimitIsFull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-reader-curated-relation-priority-{Guid.NewGuid():N}");
        try
        {
            var source = Record("priority-source", ScopeA, "[[related:docs/manual|Curated manual]]", ["alpha"]);
            var automaticTargets = Enumerable.Range(0, MemoryWikiLimits.MaximumRelatedPerDocument)
                .Select(index => Record($"priority-auto-{index:D3}", ScopeA, $"automatic {index:D3}", ["alpha"])).ToArray();
            var manual = Record("priority-manual", ScopeA, "manual", []);
            var eligibility = new MemorySearchEligibility(new(new[] { new ScopeSelector(ScopeA) }), null, Now.AddHours(1));

            await using var store = new LanceDbMemoryStore(new(path));
            await WriteAsync(store, Authorized(ScopeA), [source, manual, .. automaticTargets]);
            await store.UpsertWikiMetadataAsync(new(ScopeA, manual.Id, 1, "manual", "docs", "manual", []), default);

            var generation = await store.ReadReadyLeafPageAsync(eligibility, null, default);
            Assert.NotNull(generation);
            var relations = await store.ReadWikiRelationsAsync(eligibility, generation!.GenerationKey, source.Id,
                MemoryWikiRelationKind.Related, default);

            Assert.Equal(MemoryWikiLimits.MaximumRelatedPerDocument, relations.Count);
            Assert.Equal("Curated manual", Assert.Single(relations, relation => relation.TargetMemoryId == manual.Id).Label);
            Assert.Equal(MemoryWikiLimits.MaximumRelatedPerDocument - 1,
                relations.Count(relation => automaticTargets.Any(target => target.Id == relation.TargetMemoryId)));
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

    private static MemoryRecord Record(string id, MemoryScope scope, string canonicalText, IReadOnlyList<string>? entities = null) => new(
        new MemoryId(id), scope, MemoryRecordType.Fact, MemoryLifecycleStatus.Active, canonicalText, null, .7, .8, 5,
        Now, Now, 1, entities ?? ["catalog"], new("proof", null, null, null, null, null, null, [new($"evidence-{id}")]),
        null, null, $"dedup-{id}");

    private sealed record CatalogTraversal(IReadOnlyList<string> MemoryIds, IReadOnlyList<int> PortionLengths);
}
