using Agm.Memory;
using Xunit;

namespace Agm.Memory.Tests;

public sealed class MemoryCoreTests
{
    [Fact]
    public void Extractor_RedactsSecretsAndCapsLength()
    {
        var extractor = new LocalWorkspaceMemoryExtractor();
        var result = extractor.Extract("token=super-secret-value\nUseful line about architecture");
        Assert.NotNull(result);
        Assert.DoesNotContain("super-secret-value", result);
        Assert.Contains("Useful line about architecture", result);
    }

    [Fact]
    public void Consolidator_RespectsMaximumCharacters()
    {
        var consolidator = new LocalWorkspaceMemoryConsolidator();
        var result = consolidator.Consolidate("old-a\nold-b", "new-c", 10);
        Assert.True(result.Length <= 10);
    }

    [Fact]
    public void Fitter_KeepsTailWithinBudget()
    {
        var fitted = MemoryContentFitter.Fit("one\ntwo\nthree\nfour", 10);
        Assert.True(fitted.Length <= 10);
        Assert.Contains("four", fitted);
    }
}
