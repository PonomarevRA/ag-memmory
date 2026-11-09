using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Core.Tests;
using Xunit;

namespace AgMemory.IntegrationTests;

public sealed class ScopedCommandIntegrationTests
{
    [Fact]
    public async Task ExactScopeWriteReplayAndConcurrency_AreAtomicAndIsolated()
    {
        var store = new InMemoryStore();
        var authorization = new FixedAuthorization();
        var redactor = new TestRedactor();
        authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        authorization.Allow(TestData.Actor, MemoryOperation.Lifecycle, TestData.Scope);
        var service = new MemoryCommandService(store, authorization, redactor, new TestEmbeddingProvider(),
            new TestEmbeddingPolicy(), new TestRetentionPolicy(), new TestClock(TestData.Now), new SequentialIds(), TestData.Options);
        var command = new RememberCommand(TestData.Envelope("integration"), new(
            MemoryRecordType.Fact, "only this exact selector", null, .5, .9, [], TestData.Provenance()));

        var created = await service.RememberAsync(command, default);
        var replay = await service.RememberAsync(command, default);
        var lifecycle = await service.ApplyLifecycleAsync(new(TestData.Envelope("life"), created.Memory!.Id,
            MemoryLifecycleAction.Invalidate, 1), default);
        var stale = await service.ApplyLifecycleAsync(new(TestData.Envelope("stale"), created.Memory.Id,
            MemoryLifecycleAction.Invalidate, 1), default);

        Assert.Equal(RememberOutcome.Created, created.Outcome);
        Assert.Equal(RememberOutcome.IdempotencyReplay, replay.Outcome);
        Assert.Single(store.Records);
        Assert.Single(store.Outbox, message => message.Kind == "remember");
        Assert.Equal(LifecycleOutcome.Applied, lifecycle.Outcome);
        Assert.Equal(LifecycleOutcome.StaleVersion, stale.Outcome);
        Assert.Equal(MemoryLifecycleStatus.Invalid, store.Records.Single().Status);
    }
}
