using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using Xunit;

namespace AgMemory.Storage.LanceDb.Tests;

/// <summary>Proves complete, bounded traversal and generation/scope isolation for the compiled hierarchy.</summary>
public sealed class LanceDbWikiTreeIndexTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly MemoryScope Scope = new(new("tree-tenant"), new("tree-project"));

    /// <summary>Reverse insertion cannot truncate or reorder a tree larger than the legacy 256-document cap.</summary>
    [Fact]
    public async Task PagesVisitEveryDocumentOnceInStableOrderBeyond256()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-tree-{Guid.NewGuid():N}");
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            await WriteAsync(store, Enumerable.Range(0, 300).Reverse().Select(index => Record(index)).ToArray());
            var eligibility = new MemorySearchEligibility(new([new ScopeSelector(Scope)]), null, Now);
            MemoryReaderCatalogCursor? cursor = null;
            var nodes = new List<MemoryReaderWikiTreeIndexNode>();
            string? generation = null;
            do
            {
                var page = Assert.IsType<MemoryReaderWikiTreeIndexPage>(await store.ReadWikiTreeIndexPageAsync(eligibility, cursor, default));
                generation ??= page.GenerationKey;
                Assert.Equal(generation, page.GenerationKey);
                Assert.InRange(page.Nodes.Count, 1, 32);
                nodes.AddRange(page.Nodes);
                cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Equal(Enumerable.Range(0, 300).Select(index => $"tree-{index:D3}"),
                nodes.Where(node => node.MemoryId is not null).Select(node => node.MemoryId));
            Assert.Equal(nodes.Count, nodes.Select(node => node.NodeKey).Distinct().Count());
            Assert.Equal(Enumerable.Range(0, nodes.Count), nodes.Select(node => node.Position));
            foreach (var node in nodes.Where(node => node.ParentKey is not null))
                Assert.Contains(nodes, parent => parent.NodeKey == node.ParentKey && parent.Position < node.Position);

            var foreign = eligibility with { AuthorizedScopes = new([new ScopeSelector(Scope with { ProjectId = new("foreign") })]) };
            Assert.Null(await store.ReadWikiTreeIndexPageAsync(foreign, new(generation!, 32), default));
            await store.UpsertWikiMetadataAsync(new(Scope, new("tree-000"), 1, "Changed title", "wiki", "changed", []), default);
            Assert.Null(await store.ReadWikiTreeIndexPageAsync(eligibility, new(generation!, 32), default));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    /// <summary>Expiry rebuilds the snapshot and rejects a continuation even without a durable mutation.</summary>
    [Fact]
    public async Task ExpiryInvalidatesContinuationAndRemovesExpiredDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-tree-expiry-{Guid.NewGuid():N}");
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var records = Enumerable.Range(0, 40).Select(Record).ToArray();
            records[39] = records[39] with { ExpiresAt = Now.AddMinutes(1) };
            await WriteAsync(store, records);
            var eligibility = new MemorySearchEligibility(new([new ScopeSelector(Scope)]), null, Now);
            var first = Assert.IsType<MemoryReaderWikiTreeIndexPage>(await store.ReadWikiTreeIndexPageAsync(eligibility, null, default));
            Assert.NotNull(first.NextCursor);
            var later = eligibility with { AsOfUtc = Now.AddMinutes(1) };
            Assert.Null(await store.ReadWikiTreeIndexPageAsync(later, first.NextCursor, default));
            var nodes = new List<MemoryReaderWikiTreeIndexNode>();
            MemoryReaderCatalogCursor? cursor = null;
            do
            {
                var page = Assert.IsType<MemoryReaderWikiTreeIndexPage>(await store.ReadWikiTreeIndexPageAsync(later, cursor, default));
                nodes.AddRange(page.Nodes);
                cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Equal(39, nodes.Count(node => node.MemoryId is not null));
            Assert.DoesNotContain(nodes, node => node.MemoryId == "tree-039");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    private static MemoryRecord Record(int index) => new(new($"tree-{index:D3}"), Scope,
        MemoryRecordType.Fact, MemoryLifecycleStatus.Active, $"Document {index:D3}", null, .7, .8, 5,
        Now, Now, 1, [], new("tree-proof", null, null, null, null, null, null, []), null, null, $"tree-dedup-{index}");

    /// <summary>Seeds an atomic corpus without introducing a separate transaction for every record.</summary>
    private static async Task WriteAsync(LanceDbMemoryStore store, IReadOnlyList<MemoryRecord> records)
    {
        await using var transaction = await store.BeginTransactionAsync(default);
        var results = await transaction.WriteRecordsAsync(new([new ScopeSelector(Scope)]),
            records.Select(record => new ConditionalRecordWrite(record, 0)).ToArray(), default);
        Assert.All(results, result => Assert.True(result.Applied));
        await transaction.CommitAsync(default);
    }
}
