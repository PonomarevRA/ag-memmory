using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Core.Tests;
using AgMemory.Storage.LanceDb;
using Xunit;

namespace AgMemory.IntegrationTests;

public sealed class LanceDbHybridRetrievalIntegrationTests
{
    [Fact]
    public async Task HybridSearch_ForwardsVectorToLanceDbAndKeepsEligibilityBeforeRanks()
    {
        var path = TemporaryPath();
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var contract = new EmbeddingContract("test", "small", "v1", 3, "l2", TestData.ContractVersion);
            var queryEmbedding = new EmbeddingReference("test", "small", "v1", 3, "l2", "query-content");
            var embedding = new EmbeddingReference("test", "small", "v1", 3, "l2", "stored-content");
            var primary = WithEmbedding(TestData.Record("primary", text: "memory platform exact scope"), embedding, new float[] { 1f, 0f, 0f });
            var foreignScope = TestData.Scope with { TenantId = new ScopeId("tenant-b") };
            var foreign = WithEmbedding(TestData.Record("foreign", foreignScope, text: "memory platform foreign"), embedding, new float[] { 1f, 0f, 0f });
            var inactive = WithEmbedding(TestData.Record("inactive", text: "memory platform inactive", status: MemoryLifecycleStatus.Invalid), embedding, new float[] { 1f, 0f, 0f });
            var expired = WithEmbedding(TestData.Record("expired", text: "memory platform expired", expiresAt: TestData.Now), embedding, new float[] { 1f, 0f, 0f });
            await WriteAsync(store, new AuthorizedScopeSet([
                new ScopeSelector(TestData.Scope),
                new ScopeSelector(foreignScope)
            ]), [primary, foreign, inactive, expired]);

            var authorization = new FixedAuthorization();
            authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
            var policy = new TestEmbeddingPolicy { Result = new(contract, "embedding-v1") };
            var service = new MemoryQueryService(store, authorization, (ILexicalSearch)store, (IVectorSearch)store, null,
                policy, new TestClock(TestData.Now), TestData.Options);

            var result = await service.SearchAsync(new(
                TestData.Actor, TestData.Scope, "memory platform", queryEmbedding, null, null, 10,
                TestData.RetrievalVersion, TestData.ContractVersion, new float[] { 1f, 0f, 0f }), default);

            var hit = Assert.Single(result.Hits);
            Assert.Null(result.Error);
            Assert.Equal(primary.Id, hit.Record.Id);
            Assert.Equal(1, hit.Contribution.LexicalRank);
            Assert.Equal(1, hit.Contribution.VectorRank);
            Assert.Equal(RetrievalSourceStatus.Completed, result.Execution!.Vector);
        }
        finally
        {
            DeleteTemporaryPath(path);
        }
    }

    private static MemoryRecord WithEmbedding(
        MemoryRecord record,
        EmbeddingReference embedding,
        ReadOnlyMemory<float> vector) => record with { Embedding = embedding, EmbeddingVector = vector };

    private static async Task WriteAsync(
        LanceDbMemoryStore store,
        AuthorizedScopeSet scopes,
        IReadOnlyList<MemoryRecord> records)
    {
        await using var transaction = await store.BeginTransactionAsync(default);
        foreach (var record in records)
            Assert.True((await transaction.WriteRecordAsync(scopes, record, 0, default)).Applied);
        await transaction.CommitAsync(default);
    }

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"agmemory-phase6-{Guid.NewGuid():N}");

    private static void DeleteTemporaryPath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
