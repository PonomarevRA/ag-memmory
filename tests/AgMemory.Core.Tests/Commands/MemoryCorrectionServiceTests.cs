using System.Reflection;
using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests.Commands;

public sealed class MemoryCorrectionServiceTests
{
    [Fact]
    public async Task PartialNativeCommitIsFailureUntilSameRequestReconcilesIt()
    {
        var inner = new InMemoryStore();
        var source = TestData.Record("original");
        inner.Seed(source);
        var authorization = new FixedAuthorization();
        authorization.Allow(TestData.Actor, MemoryOperation.Lifecycle, TestData.Scope);
        authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        var store = new PartialFailureStore(inner, source.Id);
        var service = new MemoryCorrectionService(store, authorization, new TestRedactor(), new TestClock(TestData.Now));
        var input = new MemoryRecordInput(MemoryRecordType.Fact, "replacement claim", null, .6, .5, [], TestData.Provenance());
        var command = new CorrectMemoryCommand(TestData.Envelope("recover"), source.Id, 1, input);

        await Assert.ThrowsAsync<IOException>(() => service.CorrectAsync(command, default));
        Assert.DoesNotContain(inner.Records, record => record.Status == MemoryLifecycleStatus.Active);
        Assert.Single(inner.Records); // The partial native commit is deliberately observable, never reported as success.

        var recovered = await service.CorrectAsync(command, default);
        Assert.Null(recovered.Error);
        Assert.Equal("Applied", recovered.Outcome);
        Assert.Equal("replacement claim", recovered.Replacement!.CanonicalText);
        Assert.Single(inner.Records, record => record.Status == MemoryLifecycleStatus.Active);
        var replay = await service.CorrectAsync(command, default);
        Assert.Equal("Replay", replay.Outcome);
        Assert.Equal(recovered.Replacement.Id, replay.Replacement!.Id);
        Assert.Equal(2, inner.Records.Count);
    }

    [Fact]
    public async Task UnauthorizedCorrectionDoesNotStartTransaction()
    {
        var store = new InMemoryStore();
        var service = new MemoryCorrectionService(store, new FixedAuthorization(), new TestRedactor(), new TestClock(TestData.Now));
        var result = await service.CorrectAsync(new(TestData.Envelope(), new("hidden"), 1, null), default);
        Assert.Equal(MemoryErrorCode.Unauthorized, result.Error!.Code);
        Assert.Equal(0, store.BeginCalls);
    }

    [Fact]
    public async Task ExpiredSourceAndReplacementRequireNoTransactionMutation()
    {
        var store = new InMemoryStore();
        var source = TestData.Record("expired", expiresAt: TestData.Now.AddSeconds(-1));
        store.Seed(source);
        var authorization = new FixedAuthorization();
        authorization.Allow(TestData.Actor, MemoryOperation.Lifecycle, TestData.Scope);
        authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        var service = new MemoryCorrectionService(store, authorization, new TestRedactor(), new TestClock(TestData.Now));
        var result = await service.CorrectAsync(new(TestData.Envelope("expired"), source.Id, 1, null), default);
        Assert.Equal(MemoryErrorCode.StaleVersion, result.Error!.Code);
        Assert.Equal(source, Assert.Single(store.Records));
        var replacement = new MemoryRecordInput(MemoryRecordType.Fact, "expired replacement", null, .6, .5, [], TestData.Provenance(), TestData.Now.AddSeconds(-1));
        var expiredReplacement = await service.CorrectAsync(new(TestData.Envelope("expired-input"), source.Id, 1, replacement), default);
        Assert.Equal(MemoryErrorCode.InvalidArgument, expiredReplacement.Error!.Code);
        Assert.Equal(source, Assert.Single(store.Records));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("status")]
    [InlineData("version")]
    [InlineData("evidence")]
    [InlineData("scope")]
    [InlineData("type")]
    [InlineData("dedup")]
    public async Task RecoveryRejectsReplacementChangedAfterPartialCommit(string mutation)
    {
        var inner = new InMemoryStore();
        var source = TestData.Record("partial-source");
        inner.Seed(source);
        var authorization = new FixedAuthorization();
        authorization.Allow(TestData.Actor, MemoryOperation.Lifecycle, TestData.Scope);
        authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        var service = new MemoryCorrectionService(new PartialFailureStore(inner, source.Id, includeReplacement: true),
            authorization, new TestRedactor(), new TestClock(TestData.Now));
        var command = new CorrectMemoryCommand(TestData.Envelope("partial-both"), source.Id, 1,
            new(MemoryRecordType.Fact, "intended replacement", null, .6, .5, [], TestData.Provenance()));
        await Assert.ThrowsAsync<IOException>(() => service.CorrectAsync(command, default));
        var persisted = Assert.Single(inner.Records, record => record.Id != source.Id);
        var changed = mutation switch
        {
            "content" => persisted with { CanonicalText = "different claim" },
            "status" => persisted with { Status = MemoryLifecycleStatus.Invalid },
            "version" => persisted with { Version = persisted.Version + 1 },
            "evidence" => persisted with { Provenance = TestData.Provenance("changed-evidence") },
            "scope" => persisted with { Scope = new(new("foreign")) },
            "type" => persisted with { Type = MemoryRecordType.Task },
            "dedup" => persisted with { DeduplicationKey = "changed-dedup" },
            _ => throw new InvalidOperationException()
        };
        inner.Seed(changed);
        var rejected = await service.CorrectAsync(command, default);
        Assert.Equal(MemoryErrorCode.Conflict, rejected.Error!.Code);
        Assert.Null(rejected.Replacement);
        Assert.Equal("Failed", rejected.Outcome);
        Assert.Equal(changed, inner.Records.Single(record => record.Id == persisted.Id));
        Assert.Empty(inner.Outbox);
    }

    [Fact]
    public async Task RecoveryReturnsTheActualUnchangedPersistedReplacement()
    {
        var inner = new InMemoryStore();
        var source = TestData.Record("partial-source");
        inner.Seed(source);
        var authorization = new FixedAuthorization();
        authorization.Allow(TestData.Actor, MemoryOperation.Lifecycle, TestData.Scope);
        authorization.Allow(TestData.Actor, MemoryOperation.Remember, TestData.Scope);
        var clock = new TestClock(TestData.Now);
        var service = new MemoryCorrectionService(new PartialFailureStore(inner, source.Id, includeReplacement: true), authorization, new TestRedactor(), clock);
        var command = new CorrectMemoryCommand(TestData.Envelope("partial-both"), source.Id, 1,
            new(MemoryRecordType.Fact, "intended replacement", null, .6, .5, [], TestData.Provenance()));
        await Assert.ThrowsAsync<IOException>(() => service.CorrectAsync(command, default));
        var persisted = Assert.Single(inner.Records, record => record.Id != source.Id);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var recovered = await service.CorrectAsync(command, default);
        Assert.Null(recovered.Error);
        Assert.Same(persisted, recovered.Replacement);
        Assert.Equal(TestData.Now, recovered.Replacement!.CreatedAt);
    }

    private sealed class PartialFailureStore(InMemoryStore inner, MemoryId sourceId, bool includeReplacement = false) : IMemoryStore
    {
        private bool _fail = true;
        public async Task<IMemoryStoreTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            var transaction = await inner.BeginTransactionAsync(cancellationToken);
            if (!_fail) return transaction;
            _fail = false;
            var proxy = DispatchProxy.Create<IMemoryStoreTransaction, PartialCommitProxy>();
            ((PartialCommitProxy)(object)proxy).Inner = transaction;
            ((PartialCommitProxy)(object)proxy).Commit = async () =>
            {
                // Emulate adapter failure after the source row persists but before replacement/receipt.
                var scopes = new AuthorizedScopeSet([new ScopeSelector(TestData.Scope)]);
                var staged = await transaction.FindRecordAsync(scopes, sourceId, default);
                inner.Seed(staged!);
                if (includeReplacement)
                    foreach (var written in ((PartialCommitProxy)(object)proxy).Writes) inner.Seed(written);
                throw new IOException("Injected native failure");
            };
            return proxy;
        }
        public Task<MemoryRecord?> GetAsync(AuthorizedScopeSet scopes, MemoryId id, CancellationToken cancellationToken) => inner.GetAsync(scopes, id, cancellationToken);
        public Task<IReadOnlyList<MemoryRecord>> ListAsync(AuthorizedScopeSet scopes, CancellationToken cancellationToken) => inner.ListAsync(scopes, cancellationToken);
        public Task<SessionHotMemory?> GetHotMemoryAsync(AuthorizedScopeSet scopes, MemoryScope exactScope, CancellationToken cancellationToken) => inner.GetHotMemoryAsync(scopes, exactScope, cancellationToken);
    }

    public class PartialCommitProxy : DispatchProxy
    {
        public IMemoryStoreTransaction Inner { get; set; } = null!;
        public Func<Task> Commit { get; set; } = null!;
        public List<MemoryRecord> Writes { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IMemoryStoreTransaction.CommitAsync)) return Commit();
            if (targetMethod.Name == nameof(IMemoryStoreTransaction.WriteRecordAsync)) Writes.Add((MemoryRecord)args![1]!);
            return targetMethod.Invoke(Inner, args);
        }
    }
}
