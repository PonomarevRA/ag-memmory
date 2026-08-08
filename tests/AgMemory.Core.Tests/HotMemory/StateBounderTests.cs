using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests.HotMemory;

public sealed class StateBounderTests
{
    [Fact]
    public void Bind_IsDeterministicAndEnforcesPerKindOverallAndTokenBounds()
    {
        var policy = Policy(maximumEntries: 3, perKind: 1, tokenBudget: 14);
        var state = new HotMemoryState([
            Entry("goal-old", HotMemoryEntryKind.CurrentGoal, "old goal", .8, TestData.Now.AddMinutes(-20)),
            Entry("goal-current", HotMemoryEntryKind.CurrentGoal, "current goal", .9, TestData.Now),
            Entry("entity-a", HotMemoryEntryKind.ActiveEntity, "entity a", .7, TestData.Now),
            Entry("entity-b", HotMemoryEntryKind.ActiveEntity, "entity b", .7, TestData.Now),
            Entry("fact", HotMemoryEntryKind.WorkingFact, "this content cannot fit", .9, TestData.Now)
        ]);

        var bounded = HotMemoryStateBounder.Bind(state, policy, TestData.Now);

        Assert.Equal(["goal-current", "entity-a"], bounded.Entries.Select(entry => entry.Key));
        Assert.True(bounded.Entries.Sum(entry => MemoryCommandService.EstimateTokenCost(HotMemoryStateCodec.RenderEntry(entry))) <= 14);
    }

    [Fact]
    public void Entry_RejectsMultilineRawLogShape()
    {
        var entry = Entry("log", HotMemoryEntryKind.WorkingFact, "line one\nline two", .5, TestData.Now);

        Assert.Throws<ArgumentException>(entry.Validate);
    }

    private static HotMemoryEntry Entry(string key, HotMemoryEntryKind kind, string content, double importance, DateTimeOffset capturedAt) =>
        new(key, kind, content, importance, .8, capturedAt);

    internal static HotMemoryPolicy Policy(int maximumEntries = 5, int perKind = 2, int tokenBudget = 64) => new(
        TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), maximumEntries, perKind, tokenBudget, 80, .3,
        new HashSet<HotMemoryEntryKind> { HotMemoryEntryKind.WorkingFact });
}
