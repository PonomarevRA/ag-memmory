using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using Apache.Arrow;
using lancedb;
using Xunit;

namespace AgMemory.Storage.LanceDb.Tests;

/// <summary>Selected-column aggregate status preserves scope/lifecycle semantics while never hydrating unrelated record bodies.</summary>
public sealed class LanceDbStatusProjectionTests
{
    private static readonly MemoryScope Scope = new(new("status-tenant"), new("status-project"));
    private static readonly AuthorizedScopeSet Authorized = new([new ScopeSelector(Scope)]);

    /// <summary>Compare against the old full-record classification on valid data, including inactive expiry and foreign selectors.</summary>
    [Fact]
    public async Task NarrowStatusMatchesFullRecordCountsTypesAndLatestUpdate()
    {
        var path = Path.Combine(Path.GetTempPath(), "agmemory-status-unit-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var records = new[] {
                Record("active-fact", now) with { Type = MemoryRecordType.Fact },
                Record("active-event", now.AddMinutes(1)) with { Type = MemoryRecordType.Event, ExpiresAt = now.AddDays(1) },
                Record("expired", now.AddMinutes(2)) with { ExpiresAt = now.AddDays(-1) },
                Record("draft", now.AddMinutes(3)) with { Status = MemoryLifecycleStatus.Draft, ExpiresAt = now.AddDays(-1) },
                Record("invalid", now.AddMinutes(4)) with { Status = MemoryLifecycleStatus.Invalid },
                Record("superseded", now.AddMinutes(5)) with { Status = MemoryLifecycleStatus.Superseded },
                Record("foreign", now.AddYears(1)) with { Scope = Scope with { WorkspaceId = new("foreign") } }
            };
            await WriteAsync(store, records);
            var full = await store.ListAsync(Authorized, default);
            var result = await store.ReadStatusAsync(Authorized, default);
            Assert.Equal(full.Count, result.TotalMemoryCount);
            Assert.Equal(6, result.TotalMemoryCount);
            Assert.Equal(2, result.ActiveMemoryCount);
            Assert.Equal(1, result.ExpiredMemoryCount);
            Assert.Equal(3, result.InactiveMemoryCount);
            Assert.Equal(now.AddMinutes(5), result.LatestUpdateAt);
            Assert.Equal(new[] { MemoryRecordType.Event, MemoryRecordType.Fact }, result.ActiveByType.Select(item => item.Type));
            Assert.All(result.ActiveByType, item => Assert.Equal(1, item.Count));
            var empty = await store.ReadStatusAsync(new([new ScopeSelector(Scope with { WorkspaceId = new("absent") })]), default);
            Assert.Equal(0, empty.TotalMemoryCount);
            Assert.Null(empty.LatestUpdateAt);
            Assert.Empty(empty.ActiveByType);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    /// <summary>Invalid unused JSON proves status does not fall back to the general full-record materializer.</summary>
    [Fact]
    public async Task StatusDoesNotDeserializeTextProvenanceEntitiesOrEmbeddingBodies()
    {
        var path = Path.Combine(Path.GetTempPath(), "agmemory-status-projection-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            await WriteAsync(store, [Record("projection", DateTimeOffset.UtcNow)]);
            using var connection = new Connection();
            await connection.Connect(path);
            using var table = await connection.OpenTable("memory_records");
            using var original = await table.Query().ToArrow();
            using var invalid = new StringArray.Builder().Append("invalid unused JSON").Build();
            var arrays = original.Schema.FieldsList.Select(field => field.Name is "provenance_json" or "entities_json" or "embedding_json"
                ? invalid : original.Column(field.Name)).ToArray();
            using var corrupted = new RecordBatch(original.Schema, arrays, original.Length);
            await table.MergeInsert("id").WhenMatchedUpdateAll().WhenNotMatchedInsertAll().Execute(corrupted);
            var status = await store.ReadStatusAsync(Authorized, default);
            Assert.Equal(1, status.TotalMemoryCount);
            Assert.Equal(1, status.ActiveMemoryCount);
            Assert.Equal(MemoryRecordType.Fact, Assert.Single(status.ActiveByType).Type);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    /// <summary>A pre-cancelled status read never creates its physical store; the global gate still owns all native operations.</summary>
    [Fact]
    public async Task CancelledStatusReadDoesNotOpenStorage()
    {
        var path = Path.Combine(Path.GetTempPath(), "agmemory-status-cancel-" + Guid.NewGuid().ToString("N"));
        await using var store = new LanceDbMemoryStore(new(path));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadStatusAsync(Authorized, new CancellationToken(true)));
        Assert.False(Directory.Exists(path));
    }

    /// <summary>Seed through the unchanged transaction/CAS API, including an explicitly authorized foreign negative control.</summary>
    private static async Task WriteAsync(LanceDbMemoryStore store, IReadOnlyList<MemoryRecord> records)
    {
        await using var transaction = await store.BeginTransactionAsync(default);
        var scopes = new AuthorizedScopeSet(records.Select(record => new ScopeSelector(record.Scope)).Distinct());
        var result = await transaction.WriteRecordsAsync(scopes, records.Select(record => new ConditionalRecordWrite(record, 0)).ToArray(), default);
        Assert.All(result, write => Assert.True(write.Applied));
        await transaction.CommitAsync(default);
    }

    /// <summary>Build a text-heavy valid record whose document payload is irrelevant to aggregate status.</summary>
    private static MemoryRecord Record(string id, DateTimeOffset updated) => new(new(id), Scope, MemoryRecordType.Fact,
        MemoryLifecycleStatus.Active, new string('x', 12_000), null, .7, .8, 5, updated.AddHours(-1), updated, 1, [],
        new("status-unit", null, null, null, null, null, null, []), null, null, "dedup-" + id);
}
