using AgMemory.Contracts;
using Xunit;

namespace AgMemory.Contracts.Tests;

public sealed class ContractValueTests
{
    [Fact]
    public void GuidIdentifiers_AreCanonicalOpaqueStrings()
    {
        var id = new ScopeId("A0B1C2D3-E4F5-6789-A0B1-C2D3E4F56789");

        Assert.Equal("a0b1c2d3-e4f5-6789-a0b1-c2d3e4f56789", id.Value);
        Assert.Equal(id, new ScopeId(id.ToString()));
    }

    [Fact]
    public void ExactScopeSelector_DoesNotTreatMissingDimensionsAsParents()
    {
        var run = new MemoryScope(new("tenant"), new("project"), new("workspace"), new("chat"), new("run"));
        var workspace = run with { ChatId = null, RunId = null };
        var selectors = new AuthorizedScopeSet([new ScopeSelector(run), new ScopeSelector(run)]);

        Assert.Single(selectors.Selectors);
        Assert.True(selectors.Contains(run));
        Assert.False(selectors.Contains(workspace));
    }

    [Fact]
    public void InvalidCanonicalRecord_RejectsNonFiniteValuesAndMalformedDecision()
    {
        var scope = new MemoryScope(new("tenant"));
        var provenance = new MemoryProvenance("test", null, null, null, null, null, null, [new("evidence")]);
        var invalid = new MemoryRecord(new("memory"), scope, MemoryRecordType.Fact, MemoryLifecycleStatus.Active,
            "text", null, double.NaN, .5, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1,
            ["entity"], provenance, null, null, "dedup");
        var missingDecision = new MemoryRecord(new("decision"), scope, MemoryRecordType.Decision, MemoryLifecycleStatus.Active,
            "text", null, .5, .5, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1,
            ["entity"], provenance, null, null, "dedup");

        Assert.Throws<ArgumentOutOfRangeException>(invalid.Validate);
        Assert.Throws<ArgumentException>(missingDecision.Validate);
        Assert.Throws<ArgumentException>(() => new SourceEvidenceRef(" ").Validate());
    }
}
