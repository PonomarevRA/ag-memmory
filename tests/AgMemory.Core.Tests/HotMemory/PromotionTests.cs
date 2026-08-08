using AgMemory.Contracts;
using Xunit;

namespace AgMemory.Core.Tests.HotMemory;

public sealed class PromotionTests
{
    [Fact]
    public async Task Promote_EligibleWorkingFactUsesRememberAndIsIdempotent()
    {
        var fixture = HotMemoryFixture.Create();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.AppendHotMemory, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.ReadHotMemory, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        var update = await fixture.Coordinator.UpdateAsync(fixture.Update("hot", new([
            new("fact", HotMemoryEntryKind.WorkingFact, "promotable bounded fact", .9, .8, TestData.Now)
        ])), default);
        var command = new HotMemoryPromotionCommand(TestData.Envelope("promote"), "fact", update.Memory!.Version);

        var promoted = await fixture.Coordinator.PromoteAsync(command, default);
        var replay = await fixture.Coordinator.PromoteAsync(command, default);

        Assert.Equal(HotMemoryPromotionOutcome.Promoted, promoted.Outcome);
        Assert.Equal(MemoryRecordType.Fact, promoted.Memory!.Type);
        Assert.Equal(HotMemoryPromotionOutcome.IdempotencyReplay, replay.Outcome);
        Assert.Single(fixture.Store.Records);
        Assert.Single(fixture.Store.Outbox, message => message.Kind == "remember");
    }

    [Fact]
    public async Task Promote_LeavesNonFactHotEntriesIneligible()
    {
        var fixture = HotMemoryFixture.Create();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.AppendHotMemory, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.ReadHotMemory, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        var update = await fixture.Coordinator.UpdateAsync(fixture.Update("hot-question", new([
            new("question", HotMemoryEntryKind.OpenQuestion, "what is the safe limit", .9, .8, TestData.Now)
        ])), default);

        var result = await fixture.Coordinator.PromoteAsync(new(
            TestData.Envelope("promote-question"), "question", update.Memory!.Version), default);

        Assert.Equal(HotMemoryPromotionOutcome.Ineligible, result.Outcome);
        Assert.Empty(fixture.Store.Records);
    }
}
