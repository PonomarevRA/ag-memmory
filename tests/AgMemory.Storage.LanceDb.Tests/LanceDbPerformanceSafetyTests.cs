using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using Apache.Arrow;
using lancedb;
using Xunit;

namespace AgMemory.Storage.LanceDb.Tests;

/// <summary>Exercises CAS serialization, cancellation, bounded lexical hydration and derived generation retention on isolated stores.</summary>
public sealed class LanceDbPerformanceSafetyTests
{
    private static readonly MemoryScope Scope = new(new("performance-safety"), new("project"));
    private static readonly AuthorizedScopeSet Allowed = new([new(Scope)]);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    /// <summary>Independent runtimes sharing a path must not both accept the same expected version.</summary>
    [Fact]
    public async Task SeparateStoreInstancesSerializeCompareAndSwapAndCancelledWaiters()
    {
        var path = TemporaryPath();
        try
        {
            await using var first = new LanceDbMemoryStore(new(path));
            await using var second = new LanceDbMemoryStore(new(path));
            await WriteAsync(first, Record(0), 0);
            var original = Assert.IsType<MemoryRecord>(await second.GetAsync(Allowed, new("id-0000"), default));
            await using (var held = await first.BeginTransactionAsync(default))
            {
                using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.GetAsync(Allowed, original.Id, cancelled.Token));
            }
            var attempts = await Task.WhenAll(UpdateAsync(first, original), UpdateAsync(second, original));
            Assert.Single(attempts, applied => applied);
            Assert.Equal(2, (await first.GetAsync(Allowed, original.Id, default))!.Version);
            Assert.Equal(2, (await second.GetAsync(Allowed, original.Id, default))!.Version);
            Assert.True(second.GateStatistics.WaitMilliseconds > 0);
            Assert.True(File.Exists(Path.Combine(path, ".agmemory-store.lock")));
        }
        finally { Delete(path); }
    }

    /// <summary>An actual pending audit append on its kernel lease must not block a primary read on another native connection.</summary>
    [Fact]
    public async Task HeldAuditLeaseDoesNotMonopolizePrimaryMemoryLease()
    {
        var path = TemporaryPath();
        try
        {
            await using var primary = new LanceDbMemoryStore(new(path));
            await using var audit = new LanceDbMemoryStore(new(path));
            await WriteAsync(primary, Record(0), 0);
            var first = new UsageAuditEvent(Guid.NewGuid(), Now, UsageAuditSource.Mcp, UsageAuditOperation.Status,
                UsageAuditClientLabel.Codex, null, new(new string('a', 64), 0), 0, 0, 0,
                UsageAuditPolicy.EstimationMethodVersion, UsageAuditOutcome.Succeeded, null);
            await audit.AppendAsync(first, default); // Audit connection/schema initialization happens before the simulated native stall.
            Task<UsageAuditAppendResult> pending;
            using (var auditLease = new FileStream(Path.Combine(path, ".agmemory-audit.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                pending = audit.AppendAsync(first with { EventId = Guid.NewGuid() }, default);
                var delay = Task.Delay(100);
                Assert.Same(delay, await Task.WhenAny(pending, delay));
                var record = await primary.GetAsync(Allowed, new("id-0000"), default).WaitAsync(TimeSpan.FromSeconds(1));
                Assert.NotNull(record);
                await primary.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
                Assert.False(pending.IsCompleted);
            }
            Assert.Equal(UsageAuditAppendResult.Appended, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { Delete(path); }
    }

    /// <summary>Grace protects retired rows, zero-grace collection removes only obsolete generations and stale tokens remain rejected.</summary>
    [Fact]
    public async Task CollectionHonorsGraceAndPreservesActiveGenerationAndDurableMetadata()
    {
        var path = TemporaryPath();
        try
        {
            string retired;
            string active;
            await using (var store = new LanceDbMemoryStore(new(path)))
            {
                await WriteAsync(store, Record(0), 0);
                await store.UpsertWikiMetadataAsync(new(Scope, new("id-0000"), 1, "Curated", "wiki", "curated", ["tag"]), default);
                var eligibility = new MemorySearchEligibility(Allowed, null, Now);
                retired = (await store.ReadReadyLeafPageAsync(eligibility, null, default))!.GenerationKey;
                await WriteAsync(store, Record(0) with { CanonicalText = "alpha changed", Version = 2 }, 1);
                active = (await store.ReadReadyLeafPageAsync(eligibility, null, default))!.GenerationKey;
                Assert.NotEqual(retired, active);
                Assert.Null(await store.ReadReadyLeafPageAsync(eligibility, new(retired, 0), default));
                var kept = await store.CollectReaderCatalogGarbageAsync();
                Assert.Equal(0, kept.CollectedGenerations);
                Assert.True(kept.PendingGenerations > 0);
                Assert.NotNull(await store.ReadWikiMetadataAsync(Scope, new("id-0000"), 1, default));
            }
            await using (var maintenance = new LanceDbMemoryStore(new(path) { ReaderCatalogRetirementGrace = TimeSpan.Zero }))
            {
                var collected = await maintenance.CollectReaderCatalogGarbageAsync();
                Assert.True(collected.CollectedGenerations > 0);
                Assert.Equal(0, collected.PendingGenerations);
                Assert.Equal(2, (await maintenance.GetAsync(Allowed, new("id-0000"), default))!.Version);
            }
            using var connection = new Connection();
            await connection.Connect(path);
            foreach (var tableName in new[] { "memory_reader_catalog_leaves", "memory_reader_wiki_documents", "memory_reader_wiki_tree_index", "memory_reader_catalog_facets" })
            {
                using var table = await connection.OpenTable(tableName);
                using var rows = await table.Query().Select(["generation_key"]).ToArrow();
                var generations = (StringArray)rows.Column(0);
                Assert.True(rows.Length > 0);
                for (var row = 0; row < rows.Length; row++) Assert.Equal(active, generations.GetString(row));
            }
        }
        finally { Delete(path); }
    }

    /// <summary>Cancels exactly after successful invalidation, proving metadata merge ordering cannot leave stale Ready facets behind.</summary>
    [Fact]
    public async Task MetadataCancellationAfterInvalidationLeavesOldSourceAndRetiresReadyProjection()
    {
        var path = TemporaryPath();
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            await WriteAsync(store, Record(0), 0);
            var original = new MemoryWikiMetadata(Scope, new("id-0000"), 1, "Original", "wiki", "original", ["old"]);
            await store.UpsertWikiMetadataAsync(original, default);
            var eligibility = new MemorySearchEligibility(Allowed, null, Now);
            var ready = (await store.ReadReadyLeafPageAsync(eligibility, null, default))!;
            using var cancellation = new CancellationTokenSource();
            using var boundary = new Activity("metadata-cancellation-boundary");
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Name == "agmemory.storage.catalog.invalidated") owner.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
            {
                if (ReferenceEquals(Activity.Current, boundary)) cancellation.Cancel();
            });
            listener.Start();
            boundary.Start();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpsertWikiMetadataAsync(
                original with { Title = "Changed", Tags = ["new"] }, cancellation.Token));
            boundary.Stop();
            var restored = Assert.IsType<MemoryWikiMetadata>(await store.ReadWikiMetadataAsync(Scope, original.MemoryId, 1, default));
            Assert.Equal(original with { Tags = restored.Tags }, restored);
            Assert.Equal<string>(original.Tags, restored.Tags);
            Assert.Null(await store.ReadReadyLeafPageAsync(eligibility, new(ready.GenerationKey, 0), default));
        }
        finally { Delete(path); }
    }

    /// <summary>A same-scope waiter relinquishes the primary lease while a producer stages; cancelling that waiter cannot deadlock the producer.</summary>
    [Fact]
    public async Task SameScopeBuildWaiterAllowsReadsAndCancellationWithoutDeadlock()
    {
        var path = TemporaryPath();
        try
        {
            await using var producer = new LanceDbMemoryStore(new(path));
            await using var waiter = new LanceDbMemoryStore(new(path));
            await using (var seed = await producer.BeginTransactionAsync(default))
            {
                await seed.WriteRecordsAsync(Allowed, Enumerable.Range(0, 600)
                    .Select(index => new ConditionalRecordWrite(Record(index), 0)).ToArray(), default);
                await seed.CommitAsync(default);
            }
            await waiter.GetAsync(Allowed, new("missing"), default);
            var eligibility = new MemorySearchEligibility(Allowed, null, Now);
            var firstBuild = producer.ReadReadyLeafPageAsync(eligibility, null, default);
            while (producer.WorkStatistics.CatalogSourcePortions == 0) await Task.Yield();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var secondBuild = waiter.ReadReadyLeafPageAsync(eligibility, null, cancellation.Token);
            await using (var collector = new LanceDbMemoryStore(new(path) { ReaderCatalogRetirementGrace = TimeSpan.Zero }))
                Assert.Equal(0, (await collector.CollectReaderCatalogGarbageAsync()).CollectedGenerations);
            var unrelatedScope = new AuthorizedScopeSet([new(Scope with { ProjectId = new("other") })]);
            Assert.Null(await waiter.GetAsync(unrelatedScope, new("missing"), default).WaitAsync(TimeSpan.FromMilliseconds(500)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondBuild);
            Assert.NotNull(await firstBuild.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal(0, producer.WorkStatistics.CatalogBuildAborts);
            Assert.Equal(0, waiter.WorkStatistics.CatalogSourcePortions);
        }
        finally { Delete(path); }
    }

    /// <summary>An unrelated record-table mutation during sliced Offset traversal aborts publication; retry traverses every scoped ID exactly once.</summary>
    [Fact]
    public async Task UnrelatedWriteFencesInProgressPhysicalSourceTraversal()
    {
        var path = TemporaryPath();
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            await using (var seed = await store.BeginTransactionAsync(default))
            {
                var writes = Enumerable.Range(0, 600).Select(index => new ConditionalRecordWrite(Record(index), 0)).ToArray();
                await seed.WriteRecordsAsync(Allowed, writes, default);
                await seed.CommitAsync(default);
            }
            var eligibility = new MemorySearchEligibility(Allowed, null, Now);
            var building = store.ReadReadyLeafPageAsync(eligibility, null, default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (store.WorkStatistics.CatalogSourcePortions == 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            await using (var writer = await store.BeginTransactionAsync(timeout.Token))
            {
                var unrelatedScope = Scope with { ProjectId = new("unrelated") };
                var unrelated = Record(9999) with { Scope = unrelatedScope };
                Assert.True((await writer.WriteRecordAsync(new([new(unrelatedScope)]), unrelated, 0, timeout.Token)).Applied);
                await writer.CommitAsync(timeout.Token);
            }
            Assert.Null(await building.WaitAsync(timeout.Token));
            Assert.Equal(1, store.WorkStatistics.CatalogBuildAborts);
            var ids = new List<string>();
            MemoryReaderCatalogCursor? cursor = null;
            do
            {
                var page = Assert.IsType<MemoryReaderCatalogLeafPage>(await store.ReadReadyLeafPageAsync(eligibility, cursor, timeout.Token));
                ids.AddRange(page.Entries.Select(entry => entry.MemoryId.Value));
                cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Equal(Enumerable.Range(0, 600).Select(index => $"id-{index:D4}"), ids);
            Assert.Equal(600, ids.Distinct(StringComparer.Ordinal).Count());
            Assert.True(store.WorkStatistics.CatalogLeaseSlices > 0);
        }
        finally { Delete(path); }
    }

    /// <summary>A hundred small rebuilds keep only active derived rows; abandoned build-run rows are collected without touching source data.</summary>
    [Fact]
    public async Task HundredRebuildsCollectRetiredAndAbandonedGenerations()
    {
        var path = TemporaryPath();
        try
        {
            await using (var store = new LanceDbMemoryStore(new(path)
                         { ReaderCatalogRetirementGrace = TimeSpan.Zero, ReaderCatalogCollectionInterval = TimeSpan.Zero }))
            {
                await WriteAsync(store, Record(0), 0);
                var eligibility = new MemorySearchEligibility(Allowed, null, Now);
                for (var version = 1; version <= 100; version++)
                {
                    if (version > 1) await WriteAsync(store, Record(0) with { Version = version }, version - 1);
                    var page = Assert.IsType<MemoryReaderCatalogLeafPage>(await store.ReadReadyLeafPageAsync(eligibility, null, default));
                    Assert.Equal(version, Assert.Single(page.Entries).Version);
                }
                Assert.Equal(100, (await store.GetAsync(Allowed, new("id-0000"), default))!.Version);
            }
            // Emulate an interrupted producer's run without an active generation pointer.
            using (var connection = new Connection())
            {
                await connection.Connect(path);
                using var table = await connection.OpenTable("memory_reader_catalog_build_runs");
                var schema = await table.Schema();
                var columns = new List<IArrowArray>();
                foreach (var field in schema.FieldsList) columns.Add(new StringArray.Builder().Append("abandoned").Build());
                using var abandoned = new RecordBatch(schema, columns, 1);
                await table.Add(abandoned);
            }
            await using (var collector = new LanceDbMemoryStore(new(path) { ReaderCatalogRetirementGrace = TimeSpan.Zero }))
                Assert.Equal(1, (await collector.CollectReaderCatalogGarbageAsync()).CollectedGenerations);
            using var verification = new Connection();
            await verification.Connect(path);
            foreach (var name in new[] { "memory_reader_catalog_leaves", "memory_reader_wiki_documents", "memory_reader_catalog_facets" })
            {
                using var table = await verification.OpenTable(name);
                using var rows = await table.Query().ToArrow();
                Assert.Equal(1, rows.Length);
            }
            using var runs = await verification.OpenTable("memory_reader_catalog_build_runs");
            using var empty = await runs.Query().ToArrow();
            Assert.Equal(0, empty.Length);
        }
        finally { Delete(path); }
    }

    /// <summary>A producer that died after writing a Building pointer has no kernel scope lease; zero-grace maintenance retires its derived rows.</summary>
    [Fact]
    public async Task CollectionRetiresAbandonedBuildingPointerWithoutDeletingDurableSource()
    {
        var path = TemporaryPath();
        try
        {
            await using (var store = new LanceDbMemoryStore(new(path)))
            {
                await WriteAsync(store, Record(0), 0);
                await store.ReadReadyLeafPageAsync(new(Allowed, null, Now), null, default);
            }
            using (var connection = new Connection())
            {
                await connection.Connect(path);
                using var pointers = await connection.OpenTable("memory_reader_catalog_generations");
                using var original = await pointers.Query().ToArrow();
                using var building = new StringArray.Builder().Append("Building").Build();
                var columns = original.Schema.FieldsList.Select(field => field.Name == "state" ? building : original.Column(field.Name)).ToArray();
                using var interrupted = new RecordBatch(original.Schema, columns, original.Length);
                await pointers.MergeInsert("scope_key").WhenMatchedUpdateAll().WhenNotMatchedInsertAll().Execute(interrupted);
            }
            await using (var collector = new LanceDbMemoryStore(new(path) { ReaderCatalogRetirementGrace = TimeSpan.Zero }))
            {
                Assert.Equal(1, (await collector.CollectReaderCatalogGarbageAsync()).CollectedGenerations);
                Assert.NotNull(await collector.GetAsync(Allowed, new("id-0000"), default));
            }
            using var verification = new Connection();
            await verification.Connect(path);
            using var documents = await verification.OpenTable("memory_reader_wiki_documents");
            using var rows = await documents.Query().ToArrow();
            Assert.Equal(0, rows.Length);
        }
        finally { Delete(path); }
    }

    /// <summary>The generic lexical port must not hydrate invalid unrelated JSON for losing rows, and ties remain ordinal.</summary>
    [Fact]
    public async Task GenericLexicalHydratesOnlyWinnersAndRetainsExactTieOrder()
    {
        var path = TemporaryPath();
        try
        {
            await using (var store = new LanceDbMemoryStore(new(path)))
            {
                await using var transaction = await store.BeginTransactionAsync(default);
                var records = Enumerable.Range(0, 300).Reverse().Select(index => new ConditionalRecordWrite(Record(index), 0)).ToArray();
                var results = await transaction.WriteRecordsAsync(Allowed, records, default);
                Assert.All(results, result => Assert.True(result.Applied));
                await transaction.CommitAsync(default);
            }
            using (var connection = new Connection())
            {
                await connection.Connect(path);
                using var table = await connection.OpenTable("memory_records");
                using var row = await table.Query().Where("id = 'id-0299'").ToArrow();
                using var invalid = new StringArray.Builder().Append("not JSON").Build();
                var columns = row.Schema.FieldsList.Select(field => field.Name == "provenance_json" ? invalid : row.Column(field.Name)).ToArray();
                using var replacement = new RecordBatch(row.Schema, columns, 1);
                await table.MergeInsert("id").WhenMatchedUpdateAll().WhenNotMatchedInsertAll().Execute(replacement);
            }
            await using var reader = new LanceDbMemoryStore(new(path));
            var request = new SearchPortRequest(new(Allowed, null, Now), "ALPHA/beta", null, null, null, 10);
            var hits = await reader.SearchAsync(request, default);
            Assert.Equal(Enumerable.Range(0, 10).Select(index => $"id-{index:D4}"), hits.Select(hit => hit.Record.Id.Value));
            Assert.All(hits, hit => Assert.Equal(1d, hit.ProviderScore));
        }
        finally { Delete(path); }
    }

    /// <summary>Runs one CAS attempt and commits only an accepted staged record.</summary>
    private static async Task<bool> UpdateAsync(LanceDbMemoryStore store, MemoryRecord original)
    {
        await using var transaction = await store.BeginTransactionAsync(default);
        var outcome = await transaction.WriteRecordAsync(Allowed, original with { Version = 2 }, 1, default);
        if (outcome.Applied) await transaction.CommitAsync(default);
        return outcome.Applied;
    }

    /// <summary>Writes one fixture record with an explicit expected version.</summary>
    private static async Task WriteAsync(LanceDbMemoryStore store, MemoryRecord record, long expected)
    {
        await using var transaction = await store.BeginTransactionAsync(default);
        Assert.True((await transaction.WriteRecordAsync(Allowed, record, expected, default)).Applied);
        await transaction.CommitAsync(default);
    }

    private static MemoryRecord Record(int index) => new(new($"id-{index:D4}"), Scope, MemoryRecordType.Fact,
        MemoryLifecycleStatus.Active, "alpha beta", null, .7, .8, 5, Now, Now, 1, ["entity"],
        new("performance-safety", null, null, null, null, null, null, []), null, null, $"dedup-{index}");
    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"agmemory-performance-safety-{Guid.NewGuid():N}");
    private static void Delete(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
