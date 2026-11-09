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

    /// <summary>Generations are locators, not authority; all Wiki projections must bind them to the exact ready scope.</summary>
    [Fact]
    public async Task WikiProjections_RejectForeignAndInvalidatedGenerations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-wiki-isolation-{Guid.NewGuid():N}");
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var a = Record("isolation-a", ScopeA, "# Private A\n[[doc:private/target|Secret child]]");
            var target = Record("isolation-target", ScopeA, "# Private target");
            var b = Record("isolation-b", ScopeB, "# Private B");
            await WriteAsync(store, Authorized(ScopeA, ScopeB), [a, target, b]);
            await store.UpsertWikiMetadataAsync(new(ScopeA, target.Id, 1, "Private target", "private", "target", []), default);
            var eligibilityA = new MemorySearchEligibility(Authorized(ScopeA), null, Now);
            var eligibilityB = new MemorySearchEligibility(Authorized(ScopeB), null, Now);
            var generationA = await store.ReadReadyLeafPageAsync(eligibilityA, null, default);
            await store.ReadReadyLeafPageAsync(eligibilityB, null, default);
            var key = generationA!.GenerationKey;

            Assert.Equal(2, (await store.ReadWikiTreeDocumentsAsync(eligibilityA, key, default)).Count);
            Assert.Single(await store.ReadWikiTreeChildEdgesAsync(eligibilityA, key, default));
            Assert.Single(await store.ReadWikiRelationsAsync(eligibilityA, key, a.Id, MemoryWikiRelationKind.Related, default));
            Assert.Empty(await store.ReadWikiTreeDocumentsAsync(eligibilityB, key, default));
            Assert.Empty(await store.ReadWikiTreeChildEdgesAsync(eligibilityB, key, default));
            Assert.Empty(await store.ReadWikiRelationsAsync(eligibilityB, key, a.Id, MemoryWikiRelationKind.Related, default));
            await UpdateAsync(store, Authorized(ScopeA), a with { Version = 2, CanonicalText = "Updated" }, 1);
            Assert.Empty(await store.ReadWikiTreeDocumentsAsync(eligibilityA, key, default));
            Assert.Empty(await store.ReadWikiRelationsAsync(eligibilityA, key, a.Id, MemoryWikiRelationKind.Related, default));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    /// <summary>Ready leaves preserve useful bounded previews and recheck expiry even when no write invalidates the generation.</summary>
    [Fact]
    public async Task WikiProjections_RecheckExpiryAndReturnPreviewAndTypeWithoutMutation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-wiki-expiry-{Guid.NewGuid():N}");
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var source = Record("expiry-source", ScopeA, "# Source", ["shared"]);
            var target = Record("expiry-target", ScopeA, "# Visible target body", ["shared"]) with
                { Type = MemoryRecordType.Outcome, ExpiresAt = Now.AddMinutes(1) };
            await WriteAsync(store, Authorized(ScopeA), [source, target]);
            var before = new MemorySearchEligibility(Authorized(ScopeA), null, Now);
            var after = new MemorySearchEligibility(Authorized(ScopeA), null, Now.AddMinutes(2));
            var first = await store.ReadReadyLeafPageAsync(before, null, default);
            Assert.Equal("# Visible target body", Assert.Single(first!.Entries, entry => entry.MemoryId == target.Id).Preview);
            var tree = await store.ReadWikiTreeDocumentsAsync(before, first.GenerationKey, default);
            Assert.Equal(MemoryRecordType.Outcome, Assert.Single(tree, entry => entry.MemoryId == target.Id).Type);
            Assert.Single(await store.ReadWikiRelationsAsync(before, first.GenerationKey, source.Id, MemoryWikiRelationKind.Related, default));
            Assert.Equal(2, await store.CountEligibleAsync(before, default));

            var later = await store.ReadReadyLeafPageAsync(after, null, default);
            Assert.DoesNotContain(later!.Entries, entry => entry.MemoryId == target.Id);
            Assert.DoesNotContain(await store.ReadWikiTreeDocumentsAsync(after, first.GenerationKey, default), entry => entry.MemoryId == target.Id);
            Assert.Empty(await store.ReadWikiRelationsAsync(after, first.GenerationKey, source.Id, MemoryWikiRelationKind.Related, default));
            Assert.Equal(1, await store.CountEligibleAsync(after, default));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    /// <summary>The compiled tree and related candidates use the same intentional Wiki visibility policy as cards.</summary>
    [Fact]
    public async Task WikiTree_HidesUncuratedEventsAndBlankFallbacksButKeepsCuratedEvents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-wiki-curation-{Guid.NewGuid():N}");
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var visible = Record("curation-visible", ScopeA, "# Article");
            var hiddenEvent = Record("curation-event", ScopeA, "Operational event") with { Type = MemoryRecordType.Event };
            var blank = Record("curation-blank", ScopeA, "Legacy blank placeholder") with { Type = MemoryRecordType.Outcome };
            var curatedEvent = Record("curation-promoted", ScopeA, "# Promoted event") with { Type = MemoryRecordType.Event };
            await WriteAsync(store, Authorized(ScopeA), [visible, hiddenEvent, blank, curatedEvent]);
            // Current writes reject blanks; only legacy rows can contain them.
            await ReplaceLegacyColumnAsync(path, RecordsTable, "id", "canonical_text", "   ", "id = 'curation-blank'");
            await store.UpsertWikiMetadataAsync(new(ScopeA, curatedEvent.Id, 1, "Promoted event", "wiki", "promoted", []), default);
            var eligibility = new MemorySearchEligibility(Authorized(ScopeA), null, Now);
            var page = await store.ReadReadyLeafPageAsync(eligibility, null, default);
            var documents = await store.ReadWikiTreeDocumentsAsync(eligibility, page!.GenerationKey, default);
            Assert.Equal(2, documents.Count);
            Assert.Contains(documents, document => document.MemoryId == visible.Id);
            Assert.Contains(documents, document => document.MemoryId == curatedEvent.Id);
            var related = await store.ReadWikiRelationsAsync(eligibility, page.GenerationKey, visible.Id, MemoryWikiRelationKind.Related, default);
            Assert.Equal(curatedEvent.Id, Assert.Single(related).TargetMemoryId);
            await ReplaceLegacyColumnAsync(path, "memory_reader_catalog_generations", "scope_key", "state",
                "Ready:shared-entity-related-v1", null);
            Assert.Empty(await store.ReadWikiTreeDocumentsAsync(eligibility, page.GenerationKey, default));
            var rebuilt = await store.ReadReadyLeafPageAsync(eligibility, null, default);
            Assert.NotEqual(page.GenerationKey, rebuilt!.GenerationKey);
            Assert.Equal(2, (await store.ReadWikiTreeDocumentsAsync(eligibility, rebuilt.GenerationKey, default)).Count);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    /// <summary>Compiled facets stay exact across expiry, source metadata mutations, scopes and empty generations.</summary>
    [Fact]
    public async Task CompiledFacets_AreScopeBoundAndRebuildAtExpiryAndMetadataChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-compiled-facets-{Guid.NewGuid():N}");
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var permanent = Record("facet-permanent", ScopeA, "# Permanent");
            var expiring = Record("facet-expiring", ScopeA, "# Expiring") with { ExpiresAt = Now.AddMinutes(1) };
            var hidden = Record("facet-hidden", ScopeA, "Operational") with { Type = MemoryRecordType.Event };
            var foreign = Record("facet-foreign", ScopeB, "# Foreign");
            await WriteAsync(store, Authorized(ScopeA, ScopeB), [permanent, expiring, hidden, foreign]);
            await store.UpsertWikiMetadataAsync(new(ScopeA, permanent.Id, 1, "Permanent", "engineering/core", "permanent", ["shared"]), default);
            await store.UpsertWikiMetadataAsync(new(ScopeA, expiring.Id, 1, "Expiring", "engineering/core", "expiring", ["shared"]), default);
            var before = new MemorySearchEligibility(Authorized(ScopeA), null, Now);
            var other = new MemorySearchEligibility(Authorized(ScopeB), null, Now);
            var initial = await store.ReadReadyLeafPageAsync(before, null, default);
            var facets = await store.ReadReadyFacetsAsync(before, initial!.GenerationKey, default);
            Assert.Equal(new[] { "engineering", "engineering/core" }, facets!.Namespaces.Select(facet => facet.Locator));
            Assert.All(facets.Namespaces, facet => Assert.Equal(2, facet.Count));
            Assert.Equal(2, Assert.Single(facets.Tags).Count);
            Assert.Null(await store.ReadReadyFacetsAsync(other, initial.GenerationKey, default));
            Assert.Null(await store.ReadReadyFacetsAsync(before, "unknown-generation", default));

            var after = new MemorySearchEligibility(Authorized(ScopeA), null, Now.AddMinutes(1));
            Assert.Null(await store.ReadReadyFacetsAsync(after, initial.GenerationKey, default));
            Assert.Null(await store.ReadReadyLeafPageAsync(after, new(initial.GenerationKey, 0), default));
            var expired = await store.ReadReadyLeafPageAsync(after, null, default);
            Assert.NotEqual(initial.GenerationKey, expired!.GenerationKey);
            var remaining = await store.ReadReadyFacetsAsync(after, expired.GenerationKey, default);
            Assert.All(remaining!.Namespaces, facet => Assert.Equal(1, facet.Count));
            Assert.Equal(1, Assert.Single(remaining.Tags).Count);

            await store.UpsertWikiMetadataAsync(new(ScopeA, permanent.Id, 1, "Permanent", "guide", "permanent", ["changed"]), default);
            Assert.Null(await store.ReadReadyFacetsAsync(after, expired.GenerationKey, default));
            var changed = await store.ReadReadyLeafPageAsync(after, null, default);
            var changedFacets = await store.ReadReadyFacetsAsync(after, changed!.GenerationKey, default);
            Assert.Equal("guide", Assert.Single(changedFacets!.Namespaces).Locator);
            Assert.Equal("changed", Assert.Single(changedFacets.Tags).Label);
            Assert.Null(await store.ReadReadyFacetsAsync(before, changed.GenerationKey, default));
            var earlier = await store.ReadReadyLeafPageAsync(before, null, default);
            Assert.NotEqual(changed.GenerationKey, earlier!.GenerationKey);
            var earlierFacets = await store.ReadReadyFacetsAsync(before, earlier.GenerationKey, default);
            Assert.Contains(earlierFacets!.Tags, facet => facet.Label == "shared");
            Assert.Contains(earlierFacets.Tags, facet => facet.Label == "changed");

            var emptyScope = ScopeA with { ProjectId = new ScopeId("empty-project") };
            var emptyEligibility = new MemorySearchEligibility(Authorized(emptyScope), null, Now);
            var empty = await store.ReadReadyLeafPageAsync(emptyEligibility, null, default);
            var emptyFacets = await store.ReadReadyFacetsAsync(emptyEligibility, empty!.GenerationKey, default);
            Assert.Empty(emptyFacets!.Namespaces);
            Assert.Empty(emptyFacets.Tags);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    /// <summary>Repeated slug links resolve in memory; ambiguous legacy paths and foreign, missing or self targets remain ignored.</summary>
    [Fact]
    public async Task WikiCompilation_ResolvesRepeatedSlugsAndIgnoresAmbiguousLegacyTargets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-slug-index-{Guid.NewGuid():N}");
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var links = string.Join('\n', Enumerable.Range(0, 200).Select(index => $"[[doc:wiki/target|Link {index:D3}]]"));
            var source = Record("slug-source", ScopeA, links + "\n[[doc:wiki/source|Self]]\n[[doc:wiki/missing|Missing]]\n[[doc:wiki/foreign|Foreign]]", []);
            var first = Record("slug-a", ScopeA, "# Target A", []);
            var second = Record("slug-b", ScopeA, "# Target B", []);
            var foreign = Record("slug-foreign", ScopeB, "# Foreign", []);
            await WriteAsync(store, Authorized(ScopeA, ScopeB), [source, first, second, foreign]);
            await store.UpsertWikiMetadataAsync(new(ScopeA, source.Id, 1, "Source", "wiki", "source", []), default);
            await store.UpsertWikiMetadataAsync(new(ScopeA, first.Id, 1, "A", "wiki", "target", []), default);
            await store.UpsertWikiMetadataAsync(new(ScopeA, second.Id, 1, "B", "wiki", "another", []), default);
            await store.UpsertWikiMetadataAsync(new(ScopeB, foreign.Id, 1, "Foreign", "wiki", "foreign", []), default);
            var eligibility = new MemorySearchEligibility(Authorized(ScopeA), null, Now);
            var initial = await store.ReadReadyLeafPageAsync(eligibility, null, default);
            var children = await store.ReadWikiRelationsAsync(eligibility, initial!.GenerationKey, source.Id, MemoryWikiRelationKind.Child, default);
            var child = Assert.Single(children);
            Assert.Equal(first.Id, child.TargetMemoryId);
            Assert.Equal("Link 000", child.Label);

            // A released store may contain malformed duplicate slug metadata; preserve the old exactly-one-match rule.
            await ReplaceLegacyColumnAsync(path, "memory_reader_wiki_metadata", "metadata_key", "slug", "target", "memory_id = 'slug-b'");
            await store.UpsertWikiMetadataAsync(new(ScopeA, source.Id, 1, "Source", "wiki", "source", []), default);
            var ambiguous = await store.ReadReadyLeafPageAsync(eligibility, null, default);
            Assert.Empty(await store.ReadWikiRelationsAsync(eligibility, ambiguous!.GenerationKey, source.Id, MemoryWikiRelationKind.Child, default));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    /// <summary>Seeds a legacy persisted row without weakening the current command validation contract.</summary>
    private static async Task ReplaceLegacyColumnAsync(
        string path, string tableName, string mergeKey, string column, string value, string? predicate)
    {
        using var connection = new Connection();
        await connection.Connect(path);
        using var table = await connection.OpenTable(tableName);
        var query = table.Query();
        if (predicate is not null) query = query.Where(predicate);
        var batch = await query.ToArrow();
        var replacement = new StringArray.Builder();
        for (var index = 0; index < batch.Length; index++) replacement.Append(value);
        var replacementArray = replacement.Build();
        var arrays = batch.Schema.FieldsList.Select(field => field.Name == column ? replacementArray : batch.Column(field.Name)).ToArray();
        await table.MergeInsert(mergeKey).WhenMatchedUpdateAll().WhenNotMatchedInsertAll()
            .Execute(new RecordBatch(batch.Schema, arrays, batch.Length));
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
