using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using AgMemory.Web.Features.MemoryReader;
using Xunit;

namespace AgMemory.Web.Tests;

public sealed class MemoryReaderTreeBuilderTests
{
    [Fact]
    public void Build_GroupsDocumentsByNamespaceTypeAndRelationChildren()
    {
        var page = MemoryReaderTreeBuilder.Build([
            new("fact", "/memory-reader/f", "Fact title", "engineering/core", MemoryRecordType.Fact),
            new("outcome", "/memory-reader/o", "Outcome title", "engineering", MemoryRecordType.Outcome)
        ], [
            new MemoryReaderWikiTreeChildEdge("fact", "outcome", "Outcome", 2)
        ]);

        Assert.Equal("available", page.Status);
        var engineering = Assert.Single(page.Roots, node => node.Label == "engineering");
        var core = Assert.Single(engineering.Children, node => node.Label == "core");
        var factType = Assert.Single(core.Children, node => node.Label == "Fact");
        var fact = Assert.Single(factType.Children, node => node.Label == "Fact title");
        Assert.Equal("/memory-reader/f", fact.Href);
        var nestedOutcome = Assert.Single(fact.Children, node => node.Label == "Outcome");
        Assert.Equal(2, nestedOutcome.LinkWeight);
        Assert.Equal("/memory-reader/o", nestedOutcome.Href);
    }

    [Fact]
    public void Build_GroupsInboxEventsUnderTypeFolder()
    {
        var page = MemoryReaderTreeBuilder.Build([
            new("one", "/memory-reader/1", "First", "inbox", MemoryRecordType.Event),
            new("two", "/memory-reader/2", "Second", "inbox", MemoryRecordType.Event)
        ], []);

        var inbox = Assert.Single(page.Roots);
        Assert.Equal("inbox", inbox.Label);
        Assert.Equal(2, inbox.ItemCount);
        var eventFolder = Assert.Single(inbox.Children, node => node.Label == "Event");
        Assert.Equal(2, eventFolder.ItemCount);
        Assert.Equal(2, eventFolder.Children.Count);
    }
}
