using System.Globalization;
using System.Text.Json;
using AgMemory.Contracts;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    private const string ReaderWikiTreeIndexTable = "memory_reader_wiki_tree_index";
    private const int WikiTreePageSize = 32;

    /// <summary>Declares a derived preorder index; durable records and metadata retain their existing schemas.</summary>
    private static LanceDbTableSchemaDefinition ReaderWikiTreeIndexDefinition() => new(
        ReaderWikiTreeIndexTable, CurrentStorageSchemaVersion, CreateWikiTreeIndexSchema());

    private static Schema CreateWikiTreeIndexSchema() => new Schema.Builder()
        .Field(new Field("row_key", StringType.Default, nullable: false))
        .Field(new Field("generation_key", StringType.Default, nullable: false))
        .Field(new Field("position_key", StringType.Default, nullable: false))
        .Field(new Field("node_json", StringType.Default, nullable: false)).Build();

    /// <summary>Compiles each visible document once into deterministic hierarchy order before publishing Ready.</summary>
    private async Task BuildWikiTreeIndexCoreAsync(
        PersistedReaderCatalogGenerationRow generation,
        IReadOnlyDictionary<string, PersistedReaderWikiDocumentRow> documents,
        IReadOnlyDictionary<string, MemoryRecordType> types,
        IReadOnlyCollection<PersistedReaderWikiRelationRow> relations,
        CancellationToken cancellationToken)
    {
        var generationKey = generation.GenerationKey;
        var ordered = documents.Values.OrderBy(row => row.Namespace, StringComparer.Ordinal)
            .ThenBy(row => types.GetValueOrDefault(row.MemoryId).ToString(), StringComparer.Ordinal)
            .ThenBy(row => row.Title, StringComparer.Ordinal).ThenBy(row => row.MemoryId, StringComparer.Ordinal).ToArray();
        var edges = new Dictionary<string, List<PersistedReaderWikiRelationRow>>(StringComparer.Ordinal);
        var childTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in relations)
        {
            if (relation.Kind != nameof(MemoryWikiRelationKind.Child) || !documents.ContainsKey(relation.SourceMemoryId) ||
                !documents.ContainsKey(relation.TargetMemoryId) || relation.SourceMemoryId == relation.TargetMemoryId) continue;
            if (!edges.TryGetValue(relation.SourceMemoryId, out var list)) edges[relation.SourceMemoryId] = list = [];
            list.Add(relation);
            childTargets.Add(relation.TargetMemoryId);
        }
        foreach (var list in edges.Values)
            list.Sort((left, right) => CompareWikiTreeDocuments(documents[left.TargetMemoryId], documents[right.TargetMemoryId]));

        var roots = new List<WikiTreeBuildNode>();
        var folders = new Dictionary<string, WikiTreeBuildNode>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        // Roots precede cycle recovery. A shared child is placed under the first ordered reachable parent;
        // all remaining links stay available through the document inspector without exponential tree expansion.
        foreach (var document in ordered)
            if (!childTargets.Contains(document.MemoryId)) AddWikiTreeBranch(document, roots, folders, visited, documents, types, edges);
        foreach (var document in ordered)
            if (!visited.Contains(document.MemoryId)) AddWikiTreeBranch(document, roots, folders, visited, documents, types, edges);

        SortWikiTreeSiblings(roots);
        var nodes = FlattenWikiTreeIndex(roots);
        foreach (var portion in nodes.Chunk(PersistenceBatchRows))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var batch = new RecordBatch(CreateWikiTreeIndexSchema(),
            [Strings(portion.Select(node => $"{generationKey}:{WikiTreePositionKey(node.Position)}")),
                Strings(portion.Select(_ => generationKey)), Strings(portion.Select(node => WikiTreePositionKey(node.Position))),
                Strings(portion.Select(node => JsonSerializer.Serialize(node, JsonOptions)))], portion.Length);
            using (var table = await OpenTableAsync(ReaderWikiTreeIndexTable, cancellationToken).ConfigureAwait(false))
                await table.MergeInsert("row_key").WhenMatchedUpdateAll().WhenNotMatchedInsertAll().Execute(batch).ConfigureAwait(false);
            await YieldCatalogBuildLeaseAsync(generation, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sorts folders recovered from cycles alongside ordinary roots, independent of insertion order.</summary>
    private static void SortWikiTreeSiblings(List<WikiTreeBuildNode> roots)
    {
        var pending = new Stack<List<WikiTreeBuildNode>>();
        pending.Push(roots);
        while (pending.TryPop(out var siblings))
        {
            siblings.Sort((left, right) =>
            {
                var comparison = string.CompareOrdinal(left.Kind, right.Kind);
                if (comparison == 0) comparison = string.CompareOrdinal(left.Label, right.Label);
                return comparison == 0 ? string.CompareOrdinal(left.MemoryId ?? left.Locator, right.MemoryId ?? right.Locator) : comparison;
            });
            foreach (var node in siblings)
                if (node.Children.Count > 0) pending.Push(node.Children);
        }
    }

    /// <summary>Orders siblings by namespace, title and identifier rather than the table's physical row order.</summary>
    private static int CompareWikiTreeDocuments(PersistedReaderWikiDocumentRow left, PersistedReaderWikiDocumentRow right)
    {
        var value = string.CompareOrdinal(left.Namespace, right.Namespace);
        if (value == 0) value = string.CompareOrdinal(left.Title, right.Title);
        return value == 0 ? string.CompareOrdinal(left.MemoryId, right.MemoryId) : value;
    }

    /// <summary>Attaches an iterative branch with one placement per document, terminating cycles without truncation.</summary>
    private static void AddWikiTreeBranch(
        PersistedReaderWikiDocumentRow document, List<WikiTreeBuildNode> roots,
        Dictionary<string, WikiTreeBuildNode> folders, HashSet<string> visited,
        IReadOnlyDictionary<string, PersistedReaderWikiDocumentRow> documents,
        IReadOnlyDictionary<string, MemoryRecordType> types,
        IReadOnlyDictionary<string, List<PersistedReaderWikiRelationRow>> edges)
    {
        if (visited.Contains(document.MemoryId)) return;
        List<WikiTreeBuildNode> siblings = roots;
        var namespacePath = "";
        foreach (var segment in document.Namespace.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            namespacePath = namespacePath.Length == 0 ? segment : $"{namespacePath}/{segment}";
            var key = $"namespace:{namespacePath}";
            if (!folders.TryGetValue(key, out var folder))
            {
                folder = new("namespace", segment, namespacePath);
                folders.Add(key, folder);
                siblings.Add(folder);
            }
            siblings = folder.Children;
        }
        var type = types.GetValueOrDefault(document.MemoryId);
        var typeKey = $"type:{namespacePath}:{type}";
        if (!folders.TryGetValue(typeKey, out var typeFolder))
        {
            typeFolder = new("type", type.ToString(), typeKey);
            folders.Add(typeKey, typeFolder);
            siblings.Add(typeFolder);
        }
        var pending = new Stack<(PersistedReaderWikiDocumentRow Document, List<WikiTreeBuildNode> Siblings, string? Label, int? Weight)>();
        pending.Push((document, typeFolder.Children, null, null));
        while (pending.TryPop(out var item))
        {
            if (!visited.Add(item.Document.MemoryId)) continue;
            var node = new WikiTreeBuildNode("document", item.Label ?? item.Document.Title, null)
            {
                MemoryId = item.Document.MemoryId,
                Version = long.Parse(item.Document.RecordVersion, CultureInfo.InvariantCulture),
                LinkWeight = item.Weight
            };
            item.Siblings.Add(node);
            if (!edges.TryGetValue(item.Document.MemoryId, out var children)) continue;
            for (var index = children.Count - 1; index >= 0; index--)
            {
                var edge = children[index];
                var weight = int.TryParse(edge.SharedEntityCount, CultureInfo.InvariantCulture, out var count) ? count : 0;
                pending.Push((documents[edge.TargetMemoryId], node.Children, string.IsNullOrWhiteSpace(edge.Label) ? null : edge.Label, weight));
            }
        }
    }

    /// <summary>Assigns stable snapshot keys and parent context without recursion or a global 256-node limit.</summary>
    private static IReadOnlyList<MemoryReaderWikiTreeIndexNode> FlattenWikiTreeIndex(List<WikiTreeBuildNode> roots)
    {
        var result = new List<MemoryReaderWikiTreeIndexNode>();
        var pending = new Stack<(WikiTreeBuildNode Node, string? ParentKey, int Depth)>();
        for (var index = roots.Count - 1; index >= 0; index--) pending.Push((roots[index], null, 0));
        while (pending.TryPop(out var item))
        {
            var position = result.Count;
            var key = $"n{position}";
            var node = item.Node;
            result.Add(new(position, key, item.ParentKey, item.Depth, node.Kind, node.Label, node.Locator,
                node.MemoryId, node.Version, node.LinkWeight, node.Children.Count == 0 && node.Kind == "document" ? 1 : 0));
            for (var index = node.Children.Count - 1; index >= 0; index--)
                pending.Push((node.Children[index], key, item.Depth + 1));
        }
        // Match the existing tree's leaf counts, accumulated in reverse preorder.
        for (var index = result.Count - 1; index >= 0; index--)
        {
            var node = result[index];
            if (node.ParentKey is null) continue;
            var parentIndex = int.Parse(node.ParentKey.AsSpan(1), CultureInfo.InvariantCulture);
            var parent = result[parentIndex];
            result[parentIndex] = parent with { ItemCount = checked(parent.ItemCount + node.ItemCount) };
        }
        return result;
    }

    /// <summary>Reads at most 33 indexed nodes; continuations bind to the exact-scope, unexpired generation.</summary>
    public async Task<MemoryReaderWikiTreeIndexPage?> ReadWikiTreeIndexPageAsync(
        MemorySearchEligibility eligibility, MemoryReaderCatalogCursor? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        if (eligibility.AuthorizedScopes.Selectors.Count != 1 || cursor is { NextLeafPosition: < 0 } ||
            cursor is { GenerationKey.Length: > 128 } || cursor?.NextLeafPosition > int.MaxValue - WikiTreePageSize - 1) return null;
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var ready = await GetReadyCatalogGenerationCoreAsync(eligibility, cancellationToken).ConfigureAwait(false);
            if (ready is null || cursor is not null && cursor.GenerationKey != ready.GenerationKey) return null;
            var start = cursor?.NextLeafPosition ?? 0;
            using var table = await OpenTableAsync(ReaderWikiTreeIndexTable, cancellationToken).ConfigureAwait(false);
            // The inclusive position window determines the page BEFORE Limit, independent of physical row order.
            using var batch = await table.Query().Where($"generation_key = {Literal(ready.GenerationKey)} AND position_key >= {Literal(WikiTreePositionKey(start))} AND position_key <= {Literal(WikiTreePositionKey(start + WikiTreePageSize))}")
                .Limit(WikiTreePageSize + 1).ToArrow().ConfigureAwait(false);
            var nodes = new List<MemoryReaderWikiTreeIndexNode>(batch.Length);
            var payloads = (StringArray)batch.Column(3);
            for (var index = 0; index < batch.Length; index++)
                nodes.Add(JsonSerializer.Deserialize<MemoryReaderWikiTreeIndexNode>(payloads.GetString(index)!, JsonOptions)
                    ?? throw new InvalidDataException("A compiled tree node is invalid."));
            nodes.Sort((left, right) => left.Position.CompareTo(right.Position));
            for (var index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                if (node.Position != start + index || node.NodeKey != $"n{node.Position}" || node.Depth < 0 ||
                    node.ParentKey is not null && (!node.ParentKey.StartsWith('n') ||
                        !int.TryParse(node.ParentKey.AsSpan(1), out var parent) || parent < 0 || parent >= node.Position)) return null;
            }
            var hasMore = nodes.Count > WikiTreePageSize;
            if (hasMore) nodes.RemoveAt(WikiTreePageSize);
            var ids = nodes.Where(node => node.MemoryId is not null).Select(node => node.MemoryId!).ToArray();
            var sources = await ReadEligibleWikiSourcesAsync(eligibility, ids, cancellationToken).ConfigureAwait(false);
            nodes.RemoveAll(node => node.MemoryId is not null && (!sources.TryGetValue(node.MemoryId, out var source) || source.Version != node.Version));
            return new(ready.GenerationKey, nodes, hasMore ? new(ready.GenerationKey, start + WikiTreePageSize) : null);
        }
        finally { _gate.Release(); }
    }

    private static string WikiTreePositionKey(int position) => position.ToString("D10", CultureInfo.InvariantCulture);

    /// <summary>Reanchors a bounded tree response after route resolution without reloading its nodes or facet payloads.</summary>
    public async Task<bool> IsWikiTreeGenerationCurrentAsync(MemorySearchEligibility eligibility,
        string generationKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        if (eligibility.AuthorizedScopes.Selectors.Count != 1 || string.IsNullOrWhiteSpace(generationKey)) return false;
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var ready = await ReadCatalogGenerationAsync(eligibility.AuthorizedScopes.Selectors[0].Scope, cancellationToken).ConfigureAwait(false);
            if (ready is null || !IsReadyCatalogGeneration(ready) || ready.GenerationKey != generationKey) return false;
            var snapshot = await ReadCompiledFacetsCoreAsync(generationKey, cancellationToken, includeFacetData: false).ConfigureAwait(false);
            return snapshot is not null && snapshot.IsValid(eligibility);
        }
        finally { _gate.Release(); }
    }

    private sealed class WikiTreeBuildNode(string kind, string label, string? locator)
    {
        public string Kind { get; } = kind;
        public string Label { get; } = label;
        public string? Locator { get; } = locator;
        public string? MemoryId { get; init; }
        public long Version { get; init; }
        public int? LinkWeight { get; init; }
        public List<WikiTreeBuildNode> Children { get; } = [];
    }
}

/// <summary>One server-only preorder node; durable identity is resolved to an opaque route before browser delivery.</summary>
public sealed record MemoryReaderWikiTreeIndexNode(int Position, string NodeKey, string? ParentKey, int Depth,
    string Kind, string Label, string? Locator, string? MemoryId, long Version, int? LinkWeight, int ItemCount);

/// <summary>Bounded compiled tree portion and generation-bound next position.</summary>
public sealed record MemoryReaderWikiTreeIndexPage(string GenerationKey, IReadOnlyList<MemoryReaderWikiTreeIndexNode> Nodes,
    MemoryReaderCatalogCursor? NextCursor);
