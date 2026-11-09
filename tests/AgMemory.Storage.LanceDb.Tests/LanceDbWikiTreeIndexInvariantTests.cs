using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using Apache.Arrow;
using lancedb;
using Xunit;

namespace AgMemory.Storage.LanceDb.Tests;

/// <summary>Verifies compiled hierarchy semantics independently from physical LanceDB storage order.</summary>
public sealed class LanceDbWikiTreeIndexInvariantTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly MemoryScope Scope = new(new("tree-invariant-tenant"), new("tree-invariant-project"));
    private static readonly MemorySearchEligibility Eligibility = new(new([new ScopeSelector(Scope)]), null, Now);

    /// <summary>Rewriting a ready index in reverse physical order cannot alter pages, node keys or document order.</summary>
    [Fact]
    public async Task ReversedPhysicalIndexRetainsExactHierarchyPages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-tree-physical-{Guid.NewGuid():N}");
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            await WriteAsync(store, Enumerable.Range(0, 65).Select(index => Record($"node-{index:D3}", $"Document {index:D3}")).ToArray());
            var baseline = await TraverseAsync(store);
            using var connection = new Connection();
            await connection.Connect(path);
            using var table = await connection.OpenTable("memory_reader_wiki_tree_index");
            using var original = await table.Query().ToArrow();
            var arrays = new List<IArrowArray>();
            foreach (var field in original.Schema.FieldsList)
            {
                var column = (StringArray)original.Column(field.Name);
                var reversed = new StringArray.Builder();
                for (var index = original.Length - 1; index >= 0; index--) reversed.Append(column.GetString(index));
                arrays.Add(reversed.Build());
            }
            using var reversedBatch = new RecordBatch(original.Schema, arrays, original.Length);
            await table.Delete("generation_key IS NOT NULL");
            await table.Add(reversedBatch);
            Assert.Equal(baseline, await TraverseAsync(store));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    /// <summary>Cycles terminate and shared children appear once under the first stable reachable parent.</summary>
    [Fact]
    public async Task CyclicAndSharedChildDocumentsAppearOnceWithStableParentContext()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-tree-cycles-{Guid.NewGuid():N}");
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var a = Record("a", "[[doc:wiki/b|B child]]\n[[doc:wiki/shared|Shared child]]");
            var b = Record("b", "[[doc:wiki/a|A cycle]]");
            var c = Record("c", "[[doc:wiki/shared|Shared from C]]");
            var shared = Record("shared", "Shared document");
            await WriteAsync(store, [shared, c, b, a]);
            foreach (var record in new[] { a, b, c, shared })
                await store.UpsertWikiMetadataAsync(new(Scope, record.Id, 1, record.Id.Value.ToUpperInvariant(), "wiki", record.Id.Value, []), default);
            var nodes = await TraverseAsync(store);
            var documents = nodes.Where(node => node.MemoryId is not null).ToArray();
            Assert.Equal(4, documents.Length);
            Assert.Equal(4, documents.Select(node => node.MemoryId).Distinct().Count());
            foreach (var node in nodes.Where(node => node.ParentKey is not null))
                Assert.Contains(nodes, parent => parent.NodeKey == node.ParentKey && parent.Position < node.Position);
            var sharedNode = Assert.Single(documents, node => node.MemoryId == "shared");
            var sharedParent = Assert.Single(nodes, node => node.NodeKey == sharedNode.ParentKey);
            Assert.Equal("c", sharedParent.MemoryId);
            Assert.Equal(nodes, await TraverseAsync(store));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    /// <summary>Reads every bounded page through the same public generation-bound adapter contract.</summary>
    private static async Task<IReadOnlyList<MemoryReaderWikiTreeIndexNode>> TraverseAsync(LanceDbMemoryStore store)
    {
        var nodes = new List<MemoryReaderWikiTreeIndexNode>();
        MemoryReaderCatalogCursor? cursor = null;
        do
        {
            var page = Assert.IsType<MemoryReaderWikiTreeIndexPage>(await store.ReadWikiTreeIndexPageAsync(Eligibility, cursor, default));
            Assert.InRange(page.Nodes.Count, 1, 32);
            nodes.AddRange(page.Nodes);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return nodes;
    }

    private static MemoryRecord Record(string id, string text) => new(new(id), Scope,
        MemoryRecordType.Fact, MemoryLifecycleStatus.Active, text, null, .7, .8, 5,
        Now, Now, 1, [], new("tree-invariant", null, null, null, null, null, null, []), null, null, $"dedup-{id}");

    /// <summary>Seeds all records atomically while preserving command validation.</summary>
    private static async Task WriteAsync(LanceDbMemoryStore store, IReadOnlyList<MemoryRecord> records)
    {
        await using var transaction = await store.BeginTransactionAsync(default);
        var writes = records.Select(record => new ConditionalRecordWrite(record, 0)).ToArray();
        Assert.All(await transaction.WriteRecordsAsync(Eligibility.AuthorizedScopes, writes, default), result => Assert.True(result.Applied));
        await transaction.CommitAsync(default);
    }
}
