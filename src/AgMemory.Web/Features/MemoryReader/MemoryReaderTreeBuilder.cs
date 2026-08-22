using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;

namespace AgMemory.Web.Features.MemoryReader;

/// <summary>Builds a bounded wiki tree from namespaces, record types and explicit child links.</summary>
public static class MemoryReaderTreeBuilder
{
    public static MemoryReaderTreePage Build(
        IReadOnlyList<MemoryReaderTreeDocumentEntry> documents,
        IReadOnlyList<MemoryReaderWikiTreeChildEdge> childEdges)
    {
        if (documents.Count == 0) return MemoryReaderTreePage.Empty;

        var byMemoryId = documents.ToDictionary(document => document.MemoryId, StringComparer.Ordinal);
        var childTargets = new HashSet<string>(childEdges.Select(edge => edge.TargetMemoryId), StringComparer.Ordinal);
        var relationChildren = childEdges
            .GroupBy(edge => edge.SourceMemoryId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(edge => edge.TargetMemoryId, StringComparer.Ordinal).ThenBy(edge => edge.Label, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        var roots = new SortedDictionary<string, TreeBuilderNode>(StringComparer.Ordinal);
        var nodeCount = 0;
        foreach (var document in documents.OrderBy(entry => entry.Namespace, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Type.ToString(), StringComparer.Ordinal)
                     .ThenBy(entry => entry.Title, StringComparer.Ordinal))
        {
            if (nodeCount >= MemoryReaderLimits.MaximumTreeNodes) break;
            if (childTargets.Contains(document.MemoryId)) continue;

            var segments = document.Namespace.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0 || segments.Length > MemoryReaderLimits.MaximumTreeNamespaceDepth) continue;

            var parentMap = roots;
            TreeBuilderNode? namespaceNode = null;
            var path = string.Empty;
            for (var index = 0; index < segments.Length; index++)
            {
                path = index == 0 ? segments[index] : string.Concat(path, "/", segments[index]);
                if (!parentMap.TryGetValue(path, out var node))
                {
                    if (nodeCount >= MemoryReaderLimits.MaximumTreeNodes) break;
                    node = new TreeBuilderNode("namespace", segments[index], path, null);
                    parentMap.Add(path, node);
                    nodeCount++;
                }
                namespaceNode = node;
                parentMap = node.Children;
            }

            if (namespaceNode is null || nodeCount >= MemoryReaderLimits.MaximumTreeNodes) continue;
            var typeKey = string.Concat(path, "\u001f", document.Type);
            if (!parentMap.TryGetValue(typeKey, out var typeNode))
            {
                if (nodeCount >= MemoryReaderLimits.MaximumTreeNodes) break;
                typeNode = new TreeBuilderNode("type", document.Type.ToString(), typeKey, null);
                parentMap.Add(typeKey, typeNode);
                nodeCount++;
            }

            AddDocument(typeNode, document, ref nodeCount);
            AttachRelationChildren(typeNode.Children[string.Concat("doc:", document.MemoryId)], document, relationChildren, byMemoryId, ref nodeCount);
        }

        ApplyCounts(roots.Values);
        return new("available", Project(roots.Values));
    }

    private static void AddDocument(TreeBuilderNode parent, MemoryReaderTreeDocumentEntry document, ref int nodeCount)
    {
        if (nodeCount >= MemoryReaderLimits.MaximumTreeNodes) return;
        var documentKey = string.Concat("doc:", document.MemoryId);
        if (parent.Children.ContainsKey(documentKey)) return;
        parent.Children.Add(documentKey, new TreeBuilderNode("document", document.Title, document.Namespace, document.Href));
        nodeCount++;
    }

    private static void AttachRelationChildren(
        TreeBuilderNode documentNode,
        MemoryReaderTreeDocumentEntry document,
        IReadOnlyDictionary<string, MemoryReaderWikiTreeChildEdge[]> relationChildren,
        IReadOnlyDictionary<string, MemoryReaderTreeDocumentEntry> byMemoryId,
        ref int nodeCount)
    {
        if (!relationChildren.TryGetValue(document.MemoryId, out var edges)) return;
        foreach (var edge in edges)
        {
            if (nodeCount >= MemoryReaderLimits.MaximumTreeNodes) break;
            if (!byMemoryId.TryGetValue(edge.TargetMemoryId, out var target)) continue;
            var childKey = string.Concat("doc:", target.MemoryId);
            if (documentNode.Children.ContainsKey(childKey)) continue;
            var child = new TreeBuilderNode("document", string.IsNullOrWhiteSpace(edge.Label) ? target.Title : edge.Label, target.Namespace, target.Href)
            {
                LinkWeight = edge.SharedEntityCount
            };
            documentNode.Children.Add(childKey, child);
            nodeCount++;
            AttachRelationChildren(child, target, relationChildren, byMemoryId, ref nodeCount);
        }
    }

    private static void ApplyCounts(IEnumerable<TreeBuilderNode> nodes)
    {
        foreach (var node in nodes)
        {
            ApplyCounts(node.Children.Values);
            node.ItemCount = node.Children.Count == 0 && node.Kind == "document"
                ? 1
                : node.Children.Values.Sum(child => child.ItemCount ?? 0);
        }
    }

    private static IReadOnlyList<MemoryReaderTreeNode> Project(IEnumerable<TreeBuilderNode> nodes) =>
        nodes.Select(node => new MemoryReaderTreeNode(
            node.Kind,
            node.Label,
            node.Kind is "namespace" or "type" ? node.Locator : null,
            node.Href,
            node.LinkWeight,
            node.ItemCount,
            Project(node.Children.Values))).ToArray();

    private sealed class TreeBuilderNode(string kind, string label, string locator, string? href)
    {
        public string Kind { get; } = kind;
        public string Label { get; } = label;
        public string Locator { get; } = locator;
        public string? Href { get; } = href;
        public int? LinkWeight { get; set; }
        public int? ItemCount { get; set; }
        public SortedDictionary<string, TreeBuilderNode> Children { get; } = new(StringComparer.Ordinal);
    }
}

public sealed record MemoryReaderTreeDocumentEntry(
    string MemoryId,
    string Href,
    string Title,
    string Namespace,
    MemoryRecordType Type);

public sealed record MemoryReaderTreeNode(
    string Kind,
    string Label,
    string? Locator,
    string? Href,
    int? LinkWeight,
    int? ItemCount,
    IReadOnlyList<MemoryReaderTreeNode> Children);

public sealed record MemoryReaderTreePage(string Status, IReadOnlyList<MemoryReaderTreeNode> Roots, string? GenerationKey = null)
{
    public static MemoryReaderTreePage NotReady { get; } = new("catalog-not-ready", []);
    public static MemoryReaderTreePage Empty { get; } = new("available", []);
}
