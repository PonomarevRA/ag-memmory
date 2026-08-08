using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests.HotMemory;

public sealed class SessionHotMemoryCoordinatorTests
{
    [Fact]
    public async Task Update_BoundsRedactsAndRendersStructuredHotMemory()
    {
        var fixture = HotMemoryFixture.Create();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.AppendHotMemory, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.ReadHotMemory, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.BuildContext, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
        var command = fixture.Update("hot-update", new([
            Entry("goal", HotMemoryEntryKind.CurrentGoal, $"deliver {TestRedactor.Secret} safely", .9),
            Entry("fact-a", HotMemoryEntryKind.WorkingFact, "bounded working fact", .8),
            Entry("fact-b", HotMemoryEntryKind.WorkingFact, "lower priority fact", .2)
        ]));

        var result = await fixture.Coordinator.UpdateAsync(command, default);
        var context = await fixture.Query.BuildContextAsync(new(
            TestData.Actor, TestData.Scope, "goal", null, null, 5, 30, true,
            TestData.RetrievalVersion, TestData.ContextVersion, TestData.ContractVersion), default);

        Assert.Equal(HotMemoryOutcome.Created, result.Outcome);
        Assert.Equal(["goal", "fact-a"], result.State!.Entries.Select(entry => entry.Key));
        Assert.DoesNotContain(TestRedactor.Secret, result.Memory!.Content, StringComparison.Ordinal);
        Assert.StartsWith("agmemory-hot-v1:", result.Memory.Content, StringComparison.Ordinal);
        Assert.Contains("Current goal: deliver [redacted] safely", context.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("agmemory-hot-v1:", context.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_EnforcesTtlAndExactScope_AndExpiredStateIsUnreadable()
    {
        var fixture = HotMemoryFixture.Create();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.AppendHotMemory, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.ReadHotMemory, TestData.Scope);
        var foreign = TestData.Scope with { TenantId = new("tenant-b") };

        var tooLong = await fixture.Coordinator.UpdateAsync(
            fixture.Update("too-long", new([Entry("goal", HotMemoryEntryKind.CurrentGoal, "goal", .8)]),
                TestData.Now.AddHours(2)), default);
        var foreignResult = await fixture.Coordinator.UpdateAsync(
            fixture.Update("foreign", new([Entry("goal", HotMemoryEntryKind.CurrentGoal, "goal", .8)]),
                TestData.Now.AddMinutes(5), foreign), default);
        var created = await fixture.Coordinator.UpdateAsync(
            fixture.Update("expires", new([Entry("goal", HotMemoryEntryKind.CurrentGoal, "goal", .8)]),
                TestData.Now.AddMinutes(5)), default);
        fixture.Clock.UtcNow = TestData.Now.AddMinutes(5);
        var expired = await fixture.Coordinator.ReadStateAsync(new(TestData.Actor, TestData.Scope, TestData.ContractVersion), default);

        Assert.Equal(HotMemoryOutcome.Failed, tooLong.Outcome);
        Assert.Equal(MemoryErrorCode.InvalidArgument, tooLong.Error!.Code);
        Assert.Equal(HotMemoryOutcome.Failed, foreignResult.Outcome);
        Assert.Equal(MemoryErrorCode.Unauthorized, foreignResult.Error!.Code);
        Assert.Equal(HotMemoryOutcome.Created, created.Outcome);
        Assert.Null(expired);
    }

    private static HotMemoryEntry Entry(string key, HotMemoryEntryKind kind, string content, double importance) =>
        new(key, kind, content, importance, .8, TestData.Now);
}

internal sealed class HotMemoryFixture
{
    private HotMemoryFixture(
        InMemoryStore store,
        FixedAuthorization authorization,
        TestClock clock,
        MemoryCommandService commands,
        MemoryQueryService query,
        SessionHotMemoryCoordinator coordinator)
    {
        Store = store;
        Authorization = authorization;
        Clock = clock;
        Commands = commands;
        Query = query;
        Coordinator = coordinator;
    }

    public InMemoryStore Store { get; }
    public FixedAuthorization Authorization { get; }
    public TestClock Clock { get; }
    public MemoryCommandService Commands { get; }
    public MemoryQueryService Query { get; }
    public SessionHotMemoryCoordinator Coordinator { get; }

    public static HotMemoryFixture Create()
    {
        var store = new InMemoryStore();
        var authorization = new FixedAuthorization();
        var clock = new TestClock(TestData.Now);
        var commands = new MemoryCommandService(store, authorization, new TestRedactor(), new TestEmbeddingProvider(),
            new TestEmbeddingPolicy(), new TestRetentionPolicy(), clock, new SequentialIds(), TestData.Options);
        var query = new MemoryQueryService(store, authorization, new TestLexicalSearch(), new TestVectorSearch(), null,
            new TestEmbeddingPolicy(), clock, TestData.Options);
        var coordinator = new SessionHotMemoryCoordinator(commands, query, StateBounderTests.Policy(perKind: 1), clock, TestData.Options);
        return new(store, authorization, clock, commands, query, coordinator);
    }

    public UpdateHotMemoryStateCommand Update(
        string key,
        HotMemoryState state,
        DateTimeOffset? expiresAt = null,
        MemoryScope? scope = null) => new(
        TestData.Envelope(key, scope), state, TestData.Provenance(), expiresAt ?? TestData.Now.AddMinutes(5), 0);
}
