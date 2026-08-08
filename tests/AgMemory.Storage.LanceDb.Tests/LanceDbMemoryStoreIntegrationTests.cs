using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using Xunit;

namespace AgMemory.Storage.LanceDb.Tests;

public sealed class LanceDbMemoryStoreIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly ContractVersion ContractVersion = new("test-contract-v1");
    private static readonly EmbeddingContract EmbeddingContract = new("test", "small", "v1", 3, "l2", new("embedding-contract-v1"));
    private static readonly MemoryScope ScopeA = new(new("tenant-a"), new("project-a"), new("workspace-a"), new("chat-a"), new("run-a"));
    private static readonly MemoryScope ScopeB = new(new("tenant-b"), new("project-b"), new("workspace-b"), new("chat-b"), new("run-b"));

    [Fact]
    public async Task TransactionBatch_PersistsReopensAndRetainsHotReceiptsAndOutbox()
    {
        var path = TemporaryPath();
        try
        {
            var allowed = Authorized(ScopeA, ScopeB);
            var first = Record("memory-1", ScopeA, "Durable LanceDB record", [1f, 0f, 0f]);
            var updated = first with { CanonicalText = "Durable LanceDB record updated", Version = 2, UpdatedAt = Now.AddMinutes(1) };
            var second = Record("memory-2", ScopeB, "A second durable record", [0f, 1f, 0f]);
            var hot = new SessionHotMemory(new("hot-1"), ScopeA, "current scoped goal", Provenance("hot"), 1, Now, Now, Now.AddMinutes(10));
            var receipt = new IdempotencyReceipt("remember", new ScopeSelector(ScopeA), "key-1", "fingerprint-1", first.Id.Value, "Created", ContractVersion);
            var outbox = new OutboxMessage(new ScopeSelector(ScopeA), "message-1", "remember", new("command-1"), new("correlation-1"), first.Id.Value, ContractVersion);

            await using (var store = new LanceDbMemoryStore(new(path)))
            {
                await using var transaction = await store.BeginTransactionAsync(default);
                var batch = await transaction.WriteRecordsAsync(allowed,
                [
                    new(first, 0),
                    new(updated, 1),
                    new(second, 0)
                ], default);
                Assert.Collection(batch,
                    result => { Assert.True(result.Applied); Assert.Equal(1, result.CurrentVersion); },
                    result => { Assert.True(result.Applied); Assert.Equal(2, result.CurrentVersion); },
                    result => { Assert.True(result.Applied); Assert.Equal(1, result.CurrentVersion); });
                Assert.True((await transaction.WriteHotMemoryAsync(allowed, hot, 0, default)).Applied);
                await transaction.SaveReceiptAsync(allowed, receipt, default);
                await transaction.EnqueueAsync(allowed, outbox, default);
                await transaction.CommitAsync(default);
            }

            await using var reopened = new LanceDbMemoryStore(new(path));
            var restored = await reopened.GetAsync(Authorized(ScopeA), first.Id, default);
            Assert.NotNull(restored);
            Assert.Equal(updated.CanonicalText, restored!.CanonicalText);
            Assert.Equal(2, restored.Version);
            Assert.Equal(new[] { 1f, 0f, 0f }, restored.EmbeddingVector!.Value.ToArray());
            Assert.Single(await reopened.ListAsync(Authorized(ScopeA), default));
            Assert.NotNull(await reopened.GetHotMemoryAsync(Authorized(ScopeA), ScopeA, default));

            await using var query = await reopened.BeginTransactionAsync(default);
            var replay = await query.FindReceiptAsync(Authorized(ScopeA), "remember", new ScopeSelector(ScopeA), "key-1", default);
            Assert.Equal(receipt, replay);
        }
        finally
        {
            DeleteTemporaryPath(path);
        }
    }

    [Fact]
    public async Task Search_AppliesExactScopeLifecycleExpiryAndTypeBeforeRanks()
    {
        var path = TemporaryPath();
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var eligible = Record("eligible", ScopeA, "durable memory isolation", [1f, 0f, 0f]);
            var foreign = Record("foreign", ScopeB, "durable memory isolation", [0.99f, 0.01f, 0f]);
            var inactive = Record("inactive", ScopeA, "durable memory isolation", [0.98f, 0.02f, 0f]) with { Status = MemoryLifecycleStatus.Invalid };
            var expired = Record("expired", ScopeA, "durable memory isolation", [0.97f, 0.03f, 0f]) with { ExpiresAt = Now };
            var wrongType = Record("wrong-type", ScopeA, "durable memory isolation", [0.96f, 0.04f, 0f]) with { Type = MemoryRecordType.Summary };
            await WriteAsync(store, Authorized(ScopeA, ScopeB), [eligible, foreign, inactive, expired, wrongType]);

            var eligibility = new MemorySearchEligibility(Authorized(ScopeA), new HashSet<MemoryRecordType> { MemoryRecordType.Fact }, Now);
            var request = new SearchPortRequest(eligibility, "durable isolation", new float[] { 1f, 0f, 0f },
                new("test", "small", "v1", 3, "l2", "query"), EmbeddingContract, 10);

            var lexical = await ((ILexicalSearch)store).SearchAsync(request, default);
            var vector = await ((IVectorSearch)store).SearchAsync(request, default);

            Assert.Collection(lexical, candidate =>
            {
                Assert.Equal("eligible", candidate.Record.Id.Value);
                Assert.Equal(1, candidate.ProviderRank);
                Assert.True(double.IsFinite(candidate.ProviderScore));
            });
            Assert.Collection(vector, candidate =>
            {
                Assert.Equal("eligible", candidate.Record.Id.Value);
                Assert.Equal(1, candidate.ProviderRank);
                Assert.True(double.IsFinite(candidate.ProviderScore));
            });
        }
        finally
        {
            DeleteTemporaryPath(path);
        }
    }

    [Fact]
    public async Task WritesAndDeletes_FailClosedForForeignExactScopeAndStaleVersion()
    {
        var path = TemporaryPath();
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var record = Record("protected", ScopeA, "scope protected", [1f, 0f, 0f]);
            var removable = Record("removable", ScopeA, "delete succeeds", [0f, 1f, 0f]);
            await WriteAsync(store, Authorized(ScopeA), [record, removable]);

            await using (var foreign = await store.BeginTransactionAsync(default))
            {
                var write = await foreign.WriteRecordAsync(Authorized(ScopeB), record with { Version = 2, CanonicalText = "foreign overwrite" }, 1, default);
                var delete = await foreign.DeleteRecordAsync(Authorized(ScopeB), record, 1, default);
                Assert.False(write.Applied);
                Assert.False(delete.Applied);
                await foreign.CommitAsync(default);
            }

            await using (var stale = await store.BeginTransactionAsync(default))
            {
                var write = await stale.WriteRecordAsync(Authorized(ScopeA), record with { Version = 2 }, 99, default);
                Assert.False(write.Applied);
                Assert.Equal(1, write.CurrentVersion);
                await stale.CommitAsync(default);
            }

            await using (var deletion = await store.BeginTransactionAsync(default))
            {
                var deleted = await deletion.DeleteRecordAsync(Authorized(ScopeA), removable, 1, default);
                Assert.True(deleted.Applied);
                await deletion.CommitAsync(default);
            }

            var recovered = await store.GetAsync(Authorized(ScopeA), record.Id, default);
            Assert.NotNull(recovered);
            Assert.Equal("scope protected", recovered!.CanonicalText);
            Assert.Equal(1, recovered.Version);
            Assert.Null(await store.GetAsync(Authorized(ScopeB), record.Id, default));
            Assert.Null(await store.GetAsync(Authorized(ScopeA), removable.Id, default));
        }
        finally
        {
            DeleteTemporaryPath(path);
        }
    }

    [Fact]
    public async Task MetadataPredicate_EscapesOpaqueScopeValuesWithoutWideningResults()
    {
        var path = TemporaryPath();
        try
        {
            var quotedScope = ScopeA with { TenantId = new ScopeId("tenant' OR tenant_id = 'tenant-a") };
            await using var store = new LanceDbMemoryStore(new(path));
            var plain = Record("plain", ScopeA, "plain tenant", [1f, 0f, 0f]);
            var quoted = Record("quoted", quotedScope, "quoted tenant", [0f, 1f, 0f]);
            await WriteAsync(store, Authorized(ScopeA, quotedScope), [plain, quoted]);

            var plainRows = await store.ListAsync(Authorized(ScopeA), default);
            var quotedRows = await store.ListAsync(Authorized(quotedScope), default);
            Assert.Collection(plainRows, row => Assert.Equal("plain", row.Id.Value));
            Assert.Collection(quotedRows, row => Assert.Equal("quoted", row.Id.Value));
        }
        finally
        {
            DeleteTemporaryPath(path);
        }
    }

    private static async Task WriteAsync(LanceDbMemoryStore store, AuthorizedScopeSet scopes, IReadOnlyList<MemoryRecord> records)
    {
        await using var transaction = await store.BeginTransactionAsync(default);
        var results = await transaction.WriteRecordsAsync(scopes, records.Select(record => new ConditionalRecordWrite(record, 0)).ToArray(), default);
        Assert.All(results, result => Assert.True(result.Applied));
        await transaction.CommitAsync(default);
    }

    private static AuthorizedScopeSet Authorized(params MemoryScope[] scopes) => new(scopes.Select(scope => new ScopeSelector(scope)));

    private static MemoryRecord Record(string id, MemoryScope scope, string text, float[] vector) => new(
        new MemoryId(id), scope, MemoryRecordType.Fact, MemoryLifecycleStatus.Active, text, null, .7, .8, 5,
        Now, Now, 1, ["memory"], Provenance(id), new("test", "small", "v1", 3, "l2", $"content-{id}"), null,
        $"dedup-{id}", null, vector);

    private static MemoryProvenance Provenance(string evidence) => new(
        "synthetic", null, ScopeA.WorkspaceId, ScopeA.ChatId, ScopeA.RunId, null, null, [new($"evidence-{evidence}")]);

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"agmemory-lancedb-tests-{Guid.NewGuid():N}");

    private static void DeleteTemporaryPath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
