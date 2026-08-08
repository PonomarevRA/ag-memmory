using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests;

public sealed class CoreCommandTests
{
    [Fact]
    public async Task Remember_RedactsBeforeAtomicWrite_AndExactReplayDoesNotWriteAgain()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        var command = new RememberCommand(TestData.Envelope(), new(
            MemoryRecordType.Fact, $"Use {TestRedactor.Secret} only in vault", $"Reason {TestRedactor.Secret}",
            .6, .9, ["security", "security"], TestData.Provenance()));

        var created = await fixture.Service.RememberAsync(command, default);
        var replay = await fixture.Service.RememberAsync(command, default);

        Assert.Equal(RememberOutcome.Created, created.Outcome);
        Assert.Equal(RememberOutcome.IdempotencyReplay, replay.Outcome);
        Assert.Equal(created.Memory!.Id, replay.Memory!.Id);
        Assert.Single(fixture.Store.Records);
        Assert.Single(fixture.Store.Outbox);
        Assert.DoesNotContain(TestRedactor.Secret, fixture.Store.Records.Single().CanonicalText, StringComparison.Ordinal);
        Assert.DoesNotContain(TestRedactor.Secret, fixture.Store.Records.Single().Reason, StringComparison.Ordinal);
        Assert.All(fixture.Store.Outbox, message => Assert.DoesNotContain(TestRedactor.Secret, message.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remember_RedactionRejectionLeavesStoreAndOutboxUntouched()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        fixture.Redactor.Reject = true;

        var result = await fixture.Service.RememberAsync(new(TestData.Envelope(), new(
            MemoryRecordType.Fact, TestRedactor.Secret, null, .5, .5, [], TestData.Provenance())), default);

        Assert.Equal(RememberOutcome.Failed, result.Outcome);
        Assert.Equal(MemoryErrorCode.RedactionRejected, result.Error!.Code);
        Assert.Empty(fixture.Store.Records);
        Assert.Empty(fixture.Store.Outbox);
        Assert.Equal(0, fixture.Store.BeginCalls);
    }

    [Fact]
    public async Task ReusedIdempotencyKeyWithChangedRedactedPayload_IsRejectedWithoutOverwrite()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        var original = new RememberCommand(TestData.Envelope("same"), new(
            MemoryRecordType.Fact, "first statement", null, .5, .5, [], TestData.Provenance()));
        var changed = original with { Record = original.Record with { CanonicalText = "second statement" } };

        var first = await fixture.Service.RememberAsync(original, default);
        var result = await fixture.Service.RememberAsync(changed, default);

        Assert.Equal(RememberOutcome.Created, first.Outcome);
        Assert.Equal(RememberOutcome.Failed, result.Outcome);
        Assert.Equal(MemoryErrorCode.IdempotencyKeyConflict, result.Error!.Code);
        Assert.Equal("first statement", fixture.Store.Records.Single().CanonicalText);
    }

    [Fact]
    public async Task Embedding_IsNeverInvokedUnlessExplicitlyRequired_AndFailsClosedWhenUnconfigured()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        var regular = new RememberCommand(TestData.Envelope("plain"), new(
            MemoryRecordType.Fact, "plain", null, .5, .5, [], TestData.Provenance()));
        var vector = new RememberCommand(TestData.Envelope("vector"), regular.Record with { EmbeddingMode = EmbeddingMode.Required });

        await fixture.Service.RememberAsync(regular, default);
        var missingPolicy = await fixture.Service.RememberAsync(vector, default);
        Assert.Equal(0, fixture.EmbeddingProvider.Calls);
        fixture.EmbeddingPolicy.Result = new(new("fake", "model", "v1", 2, "l2", TestData.ContractVersion), "embedding-v1");
        var configured = await fixture.Service.RememberAsync(vector, default);
        Assert.Equal(1, fixture.EmbeddingProvider.Calls);
        fixture.EmbeddingProvider.ReturnMismatch = true;
        var mismatch = await fixture.Service.RememberAsync(vector with { Envelope = TestData.Envelope("mismatch") }, default);

        Assert.Equal(2, fixture.EmbeddingProvider.Calls);
        Assert.Equal(MemoryErrorCode.PolicyNotConfigured, missingPolicy.Error!.Code);
        Assert.Equal(RememberOutcome.Reinforced, configured.Outcome);
        Assert.NotNull(configured.Memory!.Embedding);
        Assert.Equal(MemoryErrorCode.EmbeddingContractMismatch, mismatch.Error!.Code);
    }

    [Fact]
    public async Task LifecycleAndHotMemory_UseOptimisticVersionsAndZeroCannotOverwrite()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Lifecycle, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.AppendHotMemory, TestData.Scope);
        var record = TestData.Record("record-1");
        fixture.Store.Seed(record);
        var lifecycle = new LifecycleCommand(TestData.Envelope("life"), record.Id, MemoryLifecycleAction.Invalidate, 1);
        var applied = await fixture.Service.ApplyLifecycleAsync(lifecycle, default);
        var stale = await fixture.Service.ApplyLifecycleAsync(lifecycle with { Envelope = TestData.Envelope("stale"), ExpectedVersion = 1 }, default);
        var hot = new AppendHotMemoryCommand(TestData.Envelope("hot"), "current run", TestData.Provenance(), TestData.Now.AddMinutes(10), "capture", 0);
        var created = await fixture.Service.AppendHotMemoryAsync(hot, default);
        var conflict = await fixture.Service.AppendHotMemoryAsync(hot with { Envelope = TestData.Envelope("hot-conflict") }, default);

        Assert.Equal(LifecycleOutcome.Applied, applied.Outcome);
        Assert.Equal(2, applied.Memory!.Version);
        Assert.Equal(LifecycleOutcome.StaleVersion, stale.Outcome);
        Assert.Equal(HotMemoryOutcome.Created, created.Outcome);
        Assert.Equal(HotMemoryOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, fixture.Store.HotMemory.Single().Version);
    }

    [Fact]
    public async Task Forget_FailsClosedWhenRetentionPolicyIsNotConfigured()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Forget, TestData.Scope);
        var record = TestData.Record("forget");
        fixture.Store.Seed(record);

        var result = await fixture.Service.ForgetAsync(new(TestData.Envelope("forget"), record.Id, 1), default);

        Assert.Equal(ForgetOutcome.Failed, result.Outcome);
        Assert.Equal(MemoryErrorCode.PolicyNotConfigured, result.Error!.Code);
        Assert.Single(fixture.Store.Records);
        Assert.Empty(fixture.Store.Outbox);
    }

    private static CommandFixture Fixture()
    {
        var store = new InMemoryStore();
        var authorization = new FixedAuthorization();
        var redactor = new TestRedactor();
        var embeddings = new TestEmbeddingProvider();
        var embeddingPolicy = new TestEmbeddingPolicy();
        var retention = new TestRetentionPolicy();
        var service = new MemoryCommandService(store, authorization, redactor, embeddings, embeddingPolicy, retention,
            new TestClock(TestData.Now), new SequentialIds(), TestData.Options);
        return new(service, store, authorization, redactor, embeddings, embeddingPolicy);
    }

    private sealed record CommandFixture(
        MemoryCommandService Service,
        InMemoryStore Store,
        FixedAuthorization Authorization,
        TestRedactor Redactor,
        TestEmbeddingProvider EmbeddingProvider,
        TestEmbeddingPolicy EmbeddingPolicy);
}
