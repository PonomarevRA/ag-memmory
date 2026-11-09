using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests.Commands;

public sealed class MemoryCommandServiceCharacterizationTests
{
    [Fact]
    public async Task Remember_DenialRunsNoRedactionEmbeddingOrTransaction()
    {
        var fixture = Fixture();
        fixture.Authorization.Deny(TestData.Actor, MemoryOperation.Remember);
        fixture.EmbeddingPolicy.Result = new(new("test", "model", "v1", 2, "l2", TestData.ContractVersion), "policy-v1");
        var command = new RememberCommand(TestData.Envelope("denied"), new(
            MemoryRecordType.Fact, "denied memory", null, .5, .5, [], TestData.Provenance(),
            EmbeddingMode: EmbeddingMode.Required));

        var result = await fixture.Service.RememberAsync(command, default);

        Assert.Equal(RememberOutcome.Failed, result.Outcome);
        Assert.Equal(MemoryErrorCode.Unauthorized, result.Error!.Code);
        Assert.Equal(0, fixture.Redactor.Calls);
        Assert.Equal(0, fixture.EmbeddingProvider.Calls);
        Assert.Equal(0, fixture.Store.BeginCalls);
        Assert.Empty(fixture.Store.Outbox);
    }

    [Fact]
    public async Task AppendHotMemory_ExactReplayDoesNotAppendOrEnqueueAgain()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.AppendHotMemory, TestData.Scope);
        var command = new AppendHotMemoryCommand(
            TestData.Envelope("hot-replay"), "bounded current goal", TestData.Provenance(),
            TestData.Now.AddMinutes(5), "goal-capture", 0);

        var created = await fixture.Service.AppendHotMemoryAsync(command, default);
        var replay = await fixture.Service.AppendHotMemoryAsync(command, default);

        Assert.Equal(HotMemoryOutcome.Created, created.Outcome);
        Assert.Equal(HotMemoryOutcome.IdempotencyReplay, replay.Outcome);
        Assert.Equal("bounded current goal", replay.Memory!.Content);
        Assert.Single(fixture.Store.HotMemory);
        Assert.Single(fixture.Store.Outbox);
    }

    private static CommandFixture Fixture()
    {
        var store = new InMemoryStore();
        var authorization = new FixedAuthorization();
        var redactor = new TestRedactor();
        var embeddings = new TestEmbeddingProvider();
        var embeddingPolicy = new TestEmbeddingPolicy();
        var service = new MemoryCommandService(store, authorization, redactor, embeddings, embeddingPolicy,
            new TestRetentionPolicy(), new TestClock(TestData.Now), new SequentialIds(), TestData.Options);
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
