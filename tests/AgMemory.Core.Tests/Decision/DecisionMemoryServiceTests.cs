using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests.Decision;

public sealed class DecisionMemoryServiceTests
{
    [Fact]
    public async Task RecordDecision_RedactsPersistsTraceAndReplaysThroughRemember()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        var command = Command("decision-replay", Details() with
        {
            Context = $"protect {TestRedactor.Secret} during rollout",
            Reason = $"because {TestRedactor.Secret} is restricted"
        });

        var created = await fixture.Service.RecordDecisionAsync(command, default);
        var replay = await fixture.Service.RecordDecisionAsync(command, default);

        Assert.Equal(RememberOutcome.Created, created.Outcome);
        Assert.Equal(MemoryRecordType.Decision, created.Memory!.Type);
        Assert.Equal(RememberOutcome.IdempotencyReplay, replay.Outcome);
        Assert.Equal(created.Memory.Id, replay.Memory!.Id);
        Assert.DoesNotContain(TestRedactor.Secret, created.Memory.CanonicalText, StringComparison.Ordinal);
        Assert.DoesNotContain(TestRedactor.Secret, created.Memory.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(TestRedactor.Secret, created.Memory.DecisionDetails!.Context, StringComparison.Ordinal);
        Assert.DoesNotContain(TestRedactor.Secret, created.Memory.DecisionDetails.Reason, StringComparison.Ordinal);
        Assert.Single(fixture.Store.Records);
        Assert.Single(fixture.Store.Outbox);
        Assert.All(fixture.Store.Outbox, message => Assert.DoesNotContain(TestRedactor.Secret, message.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordDecision_RejectsIncompleteAndTranscriptShapedTraceBeforeIngress()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        var incomplete = await fixture.Service.RecordDecisionAsync(Command("empty-options", Details() with { Options = [] }), default);
        var transcript = await fixture.Service.RecordDecisionAsync(Command("multiline", Details() with
        {
            Context = "assistant: first turn\nuser: second turn"
        }), default);

        Assert.Equal(RememberOutcome.Failed, incomplete.Outcome);
        Assert.Equal(MemoryErrorCode.InvalidArgument, incomplete.Error!.Code);
        Assert.Equal(RememberOutcome.Failed, transcript.Outcome);
        Assert.Equal(MemoryErrorCode.InvalidArgument, transcript.Error!.Code);
        Assert.Equal(0, fixture.Redactor.Calls);
        Assert.Equal(0, fixture.Store.BeginCalls);
        Assert.Empty(fixture.Store.Records);
    }

    [Fact]
    public async Task RecordDecision_PreservesExactScopeAuthorization()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        var foreign = TestData.Scope with { TenantId = new("tenant-b") };

        var result = await fixture.Service.RecordDecisionAsync(Command("foreign", Details(), foreign), default);

        Assert.Equal(RememberOutcome.Failed, result.Outcome);
        Assert.Equal(MemoryErrorCode.Unauthorized, result.Error!.Code);
        Assert.Equal(0, fixture.Redactor.Calls);
        Assert.Equal(0, fixture.Store.BeginCalls);
    }

    [Fact]
    public async Task RecordDecision_IsRetrievedAndCitedAsDecision()
    {
        var fixture = Fixture();
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.Search, TestData.Scope);
        fixture.Authorization.Allow(TestData.Actor, MemoryOperation.BuildContext, TestData.Scope);
        var created = await fixture.Service.RecordDecisionAsync(Command("retrieve", Details()), default);
        var memory = Assert.IsType<MemoryRecord>(created.Memory);
        fixture.Lexical.Candidates = [new(memory, 1, .9, null)];

        var context = await fixture.Query.BuildContextAsync(new(
            TestData.Actor, TestData.Scope, "retry decision", null,
            new HashSet<MemoryRecordType> { MemoryRecordType.Decision }, 5, 64, true,
            TestData.RetrievalVersion, TestData.ContextVersion, TestData.ContractVersion), default);

        Assert.Null(context.Error);
        Assert.Equal([memory.Id], context.Citations.Select(citation => citation.MemoryId));
        Assert.Contains(memory.CanonicalText, context.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void DecisionContracts_ContainNoReasoningOrTranscriptPayload()
    {
        var names = typeof(DecisionDetails).GetProperties().Select(property => property.Name)
            .Concat(typeof(RecordDecisionCommand).GetProperties().Select(property => property.Name));

        Assert.DoesNotContain(names, name => name.Contains("thought", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("transcript", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("conversation", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("rawlog", StringComparison.OrdinalIgnoreCase));
    }

    private static DecisionDetails Details() => new(
        "Retry policy rollout", "Production clients need a bounded retry policy", ["retry", "fail fast"],
        "Use bounded retries", "Avoid retry storms", ["Limits load", "Improves reliability"], "Rollout successful");

    private static RecordDecisionCommand Command(string key, DecisionDetails trace, MemoryScope? scope = null) => new(
        TestData.Envelope(key, scope), trace, .8, .9, ["retry-policy"], TestData.Provenance());

    private static DecisionFixture Fixture()
    {
        var store = new InMemoryStore();
        var authorization = new FixedAuthorization();
        var redactor = new TestRedactor();
        var clock = new TestClock(TestData.Now);
        var commands = new MemoryCommandService(store, authorization, redactor, new TestEmbeddingProvider(),
            new TestEmbeddingPolicy(), new TestRetentionPolicy(), clock, new SequentialIds(), TestData.Options);
        var lexical = new TestLexicalSearch();
        var query = new MemoryQueryService(store, authorization, lexical, new TestVectorSearch(), null,
            new TestEmbeddingPolicy(), clock, TestData.Options);
        return new(new DecisionMemoryService(commands, TestData.Options), query, lexical, store, authorization, redactor);
    }

    private sealed record DecisionFixture(
        DecisionMemoryService Service,
        MemoryQueryService Query,
        TestLexicalSearch Lexical,
        InMemoryStore Store,
        FixedAuthorization Authorization,
        TestRedactor Redactor);
}
