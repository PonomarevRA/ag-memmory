using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.McpServer;
using Xunit;

namespace AgMemory.McpServer.Tests;

public sealed class McpUsageAuditTests
{
    private static readonly string AuditKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    [Theory]
    [InlineData("codex", UsageAuditClientLabel.Codex)]
    [InlineData("CURSOR", UsageAuditClientLabel.Cursor)]
    [InlineData("claude", UsageAuditClientLabel.Claude)]
    [InlineData("private-client-id", UsageAuditClientLabel.Unknown)]
    public async Task OneEventPerSuccessEmptyFailureAndDegradedInvocationContainsOnlyApprovedMetadata(string origin, UsageAuditClientLabel expectedLabel)
    {
        var store = new RecordingAuditStore();
        var observer = new LocalMcpUsageAuditObserver(LocalMcpUsageAuditConfiguration.Create(true, AuditKey), store, origin);
        Assert.Equal("answer", await observer.ExecuteAsync(UsageAuditOperation.Recall, "private query marker", () => Task.FromResult("answer"), _ => new(1, 6)));
        Assert.Empty(await observer.ExecuteAsync(UsageAuditOperation.Recall, "empty marker", () => Task.FromResult(Array.Empty<string>()), _ => new(0)));
        var original = new ArgumentException("private exception marker");
        var thrown = await Assert.ThrowsAsync<ArgumentException>(() => observer.ExecuteAsync<string>(UsageAuditOperation.Recall, "invalid marker", () => Task.FromException<string>(original), _ => new(0)));
        Assert.Same(original, thrown);
        Assert.Equal("degraded", await observer.ExecuteAsync(UsageAuditOperation.Status, "", () => Task.FromResult("degraded"), _ => new(0, Outcome: UsageAuditOutcome.Degraded, FailureClass: UsageAuditFailureClass.Storage)));

        Assert.Equal(4, store.Events.Count);
        Assert.Equal(4, store.Events.Select(usageEvent => usageEvent.EventId).Distinct().Count());
        Assert.All(store.Events, usageEvent => { usageEvent.Validate(); Assert.Equal(expectedLabel, usageEvent.ClientLabel); Assert.Null(usageEvent.AreaId); });
        Assert.Equal([UsageAuditOutcome.Succeeded, UsageAuditOutcome.Succeeded, UsageAuditOutcome.Failed, UsageAuditOutcome.Degraded], store.Events.Select(usageEvent => usageEvent.Outcome).ToArray());
        Assert.Equal(UsageAuditFailureClass.Validation, store.Events[2].FailureClass);
        var persisted = JsonSerializer.Serialize(store.Events);
        Assert.DoesNotContain("private query marker", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("private exception marker", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("private-client-id", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserverFailureAndSummaryFailureNeverChangeTheOperationResponseOrException()
    {
        var store = new RecordingAuditStore { Error = new IOException("private storage marker") };
        var observer = new LocalMcpUsageAuditObserver(LocalMcpUsageAuditConfiguration.Create(true, AuditKey), store, "codex");
        Assert.Equal(42, await observer.ExecuteAsync(UsageAuditOperation.Status, "", () => Task.FromResult(42), _ => new(1)));
        var original = new InvalidOperationException("operation failed");
        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => observer.ExecuteAsync<int>(UsageAuditOperation.Status, "", () => Task.FromException<int>(original), _ => new(0))));
        Assert.Equal(7, await observer.ExecuteAsync(UsageAuditOperation.Status, "", () => Task.FromResult(7), _ => throw new InvalidOperationException("summary failed")));
        Assert.Equal(2, store.AppendAttempts);
    }

    [Fact]
    public async Task AuditIgnoresOperationCancellationAndBoundsAnUnresponsiveStore()
    {
        var store = new RecordingAuditStore();
        var observer = new LocalMcpUsageAuditObserver(LocalMcpUsageAuditConfiguration.Create(true, AuditKey), store, "codex");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var original = new OperationCanceledException(canceled.Token);
        Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => observer.ExecuteAsync<int>(UsageAuditOperation.Recall, "cancel", () => Task.FromException<int>(original), _ => new(0))));
        Assert.Equal(UsageAuditFailureClass.Timeout, Assert.Single(store.Events).FailureClass);

        store.NeverComplete = true;
        var operation = observer.ExecuteAsync(UsageAuditOperation.Status, "", () => Task.FromResult(3), _ => new(1));
        Assert.Equal(3, await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, store.AppendAttempts);
    }

    [Fact]
    public async Task DisabledAuditDoesNotAppendAndEnabledConfigurationRequiresAnExplicitStrongKey()
    {
        var store = new RecordingAuditStore();
        var observer = new LocalMcpUsageAuditObserver(LocalMcpUsageAuditConfiguration.Disabled, store, "codex");
        Assert.Equal(1, await observer.ExecuteAsync(UsageAuditOperation.Status, "", () => Task.FromResult(1), _ => new(1)));
        Assert.Equal(0, store.AppendAttempts);
        Assert.Throws<ArgumentException>(() => LocalMcpUsageAuditConfiguration.Create(true, null));
        Assert.Throws<ArgumentException>(() => LocalMcpUsageAuditConfiguration.Create(true, "invalid base64"));
        Assert.Throws<ArgumentException>(() => LocalMcpUsageAuditConfiguration.Create(true, Convert.ToBase64String([1, 2, 3])));
        Assert.Same(LocalMcpUsageAuditConfiguration.Disabled, LocalMcpUsageAuditConfiguration.Create(false, "ignored"));
        Assert.Equal(0, (int)UsageAuditOperation.Recall);
        Assert.Equal(1, (int)UsageAuditOperation.ContextBuild);
        Assert.Equal(2, (int)UsageAuditOperation.Unknown);
    }

    [Fact]
    public async Task RuntimeAuditsAllThreeToolsWithoutRememberContentOrRawScope()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-mcp-audit-{Guid.NewGuid():N}");
        try
        {
            var store = new RecordingAuditStore();
            var configuration = LocalAgMemoryMcpConfiguration.Create(path, "private-actor", "private-tenant", originClient: "cursor")
                with { UsageAudit = LocalMcpUsageAuditConfiguration.Create(true, AuditKey) };
            await using (var runtime = new LocalAgMemoryMcpRuntime(configuration, store))
            {
                await runtime.RememberAsync("Private reusable content marker.", "Fact", .8, .9, null, default);
                var hits = await runtime.RecallAsync("content", 1, default);
                Assert.Single(hits);
                Assert.Equal(1, (await runtime.GetStatusAsync(default)).ActiveMemoryCount);
                await Assert.ThrowsAsync<ArgumentException>(() => runtime.RememberAsync("", "Fact", .8, .9, null, default));
            }
            Assert.Equal([UsageAuditOperation.Remember, UsageAuditOperation.Recall, UsageAuditOperation.Status, UsageAuditOperation.Remember], store.Events.Select(usageEvent => usageEvent.Operation).ToArray());
            Assert.Equal(0, store.Events[0].Query.LengthChars);
            var persisted = JsonSerializer.Serialize(store.Events);
            Assert.DoesNotContain("Private reusable content marker", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("private-actor", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("private-tenant", persisted, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task TimedOutAuditDoesNotBlockLaterRealMemoryOperations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-mcp-audit-slow-{Guid.NewGuid():N}");
        try
        {
            var slow = new SlowAuditStore();
            var configuration = LocalAgMemoryMcpConfiguration.Create(path, "actor", "tenant")
                with { UsageAudit = LocalMcpUsageAuditConfiguration.Create(true, AuditKey) };
            await using var runtime = new LocalAgMemoryMcpRuntime(configuration, slow);
            var receipt = await runtime.RememberAsync("The following recall must remain available.", "Fact", .8, .9, null, default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Created", receipt.Outcome);
            Assert.Single(await runtime.RecallAsync("following", 1, default).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, (await runtime.GetStatusAsync(default).WaitAsync(TimeSpan.FromSeconds(5))).ActiveMemoryCount);
            Assert.Equal(3, slow.AppendAttempts);
            slow.Complete();
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task OwnedAuditConnectionIsNotDisposedWhileADeadlineExpiredNativeAppendStillUsesIt()
    {
        var slow = new SlowAuditStore();
        var observer = new LocalMcpUsageAuditObserver(LocalMcpUsageAuditConfiguration.Create(true, AuditKey), slow, "codex", slow);
        Assert.Equal(1, await observer.ExecuteAsync(UsageAuditOperation.Status, "", () => Task.FromResult(1), _ => new(1)).WaitAsync(TimeSpan.FromSeconds(5)));
        await observer.DisposeAsync();
        Assert.False(slow.Disposed.Task.IsCompleted);
        slow.Complete();
        await slow.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, slow.AppendAttempts);
    }

    /// <summary>Models a native append that ignores cancellation; completion and connection disposal remain controllable.</summary>
    private sealed class SlowAuditStore : IUsageAuditStore, IAsyncDisposable
    {
        private readonly TaskCompletionSource<UsageAuditAppendResult> _append = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AppendAttempts { get; private set; }
        public Task<UsageAuditAppendResult> AppendAsync(UsageAuditEvent usageEvent, CancellationToken cancellationToken)
        {
            AppendAttempts++;
            return _append.Task;
        }
        public void Complete() => _append.TrySetResult(UsageAuditAppendResult.Appended);
        public Task<int> DeleteExpiredAsync(DateTimeOffset cutoffExclusiveUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }

    /// <summary>Captures append attempts without real storage, including failures that the observer must isolate.</summary>
    private sealed class RecordingAuditStore : IUsageAuditStore
    {
        public List<UsageAuditEvent> Events { get; } = [];
        public Exception? Error { get; init; }
        public bool NeverComplete { get; set; }
        public int AppendAttempts { get; private set; }
        public Task<UsageAuditAppendResult> AppendAsync(UsageAuditEvent usageEvent, CancellationToken cancellationToken)
        {
            AppendAttempts++;
            if (Error is not null) return Task.FromException<UsageAuditAppendResult>(Error);
            if (NeverComplete) return new TaskCompletionSource<UsageAuditAppendResult>().Task;
            Assert.False(cancellationToken.IsCancellationRequested);
            Events.Add(usageEvent);
            return Task.FromResult(UsageAuditAppendResult.Appended);
        }
        public Task<int> DeleteExpiredAsync(DateTimeOffset cutoffExclusiveUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
