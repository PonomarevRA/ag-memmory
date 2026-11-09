using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests.HotMemory;

public sealed class DecayTests
{
    [Fact]
    public void Score_HalvesImportanceAfterOneHalfLife()
    {
        var entry = new HotMemoryEntry("fact", HotMemoryEntryKind.WorkingFact, "bounded fact", .8, .7, TestData.Now);

        var score = HotMemoryDecayEvaluator.Score(entry, TestData.Now.AddMinutes(10), TimeSpan.FromMinutes(10));

        Assert.Equal(.4, score, 8);
    }
}
