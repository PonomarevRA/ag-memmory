using AgMemory.Contracts;
using AgMemory.Core;

namespace AgMemory.McpServer;

/// <summary>Opt-in process policy; no HMAC key or client label is accepted from tool arguments.</summary>
public sealed class LocalMcpUsageAuditConfiguration
{
    public const string EnabledVariable = "AGMEMORY_USAGE_AUDIT_ENABLED";
    public const string HmacKeyVariable = "AGMEMORY_USAGE_AUDIT_HMAC_KEY";
    public static LocalMcpUsageAuditConfiguration Disabled { get; } = new(null);
    internal UsageAuditEventFactory? Factory { get; }

    private LocalMcpUsageAuditConfiguration(UsageAuditEventFactory? factory) => Factory = factory;

    /// <summary>Disabled audit needs no key; enabled audit requires an explicit Base64 key of at least 128 bits.</summary>
    public static LocalMcpUsageAuditConfiguration Create(bool enabled, string? hmacKeyBase64)
    {
        if (!enabled) return Disabled;
        byte[] key;
        try { key = Convert.FromBase64String(hmacKeyBase64 ?? ""); }
        catch (FormatException) { throw new ArgumentException("Enabled MCP usage audit requires a valid Base64 HMAC key.", nameof(hmacKeyBase64)); }
        if (key.Length < 16) throw new ArgumentException("Enabled MCP usage audit requires an HMAC key of at least 128 bits.", nameof(hmacKeyBase64));
        return new(new UsageAuditEventFactory(key));
    }

    /// <summary>Rejects invalid explicit enablement instead of silently changing the process audit policy.</summary>
    public static LocalMcpUsageAuditConfiguration FromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable(EnabledVariable);
        if (string.IsNullOrWhiteSpace(configured)) return Disabled;
        if (!bool.TryParse(configured, out var enabled)) throw new InvalidOperationException("MCP usage audit enablement must be true or false.");
        return Create(enabled, Environment.GetEnvironmentVariable(HmacKeyVariable));
    }
}

/// <summary>Counts only safe operation results; neither memory identifiers nor content belong in an audit event.</summary>
internal sealed record McpUsageAuditResult(int Count, int ContextChars = 0,
    UsageAuditOutcome Outcome = UsageAuditOutcome.Succeeded, UsageAuditFailureClass? FailureClass = null);

/// <summary>Observes one completed invocation without changing its response, exception or stdout protocol.</summary>
internal sealed class LocalMcpUsageAuditObserver(LocalMcpUsageAuditConfiguration configuration, IUsageAuditStore store, string originClient, IAsyncDisposable? ownedStore = null) : IAsyncDisposable
{
    private readonly object _pendingGate = new();
    private readonly HashSet<Task> _pendingAppends = [];
    private bool _disposed;
    private static readonly TimeSpan AuditTimeout = TimeSpan.FromMilliseconds(500);
    private readonly UsageAuditClientLabel _client = originClient.ToLowerInvariant() switch
    {
        "codex" => UsageAuditClientLabel.Codex,
        "cursor" => UsageAuditClientLabel.Cursor,
        "claude" => UsageAuditClientLabel.Claude,
        _ => UsageAuditClientLabel.Unknown
    };

    /// <summary>Invokes the operation once, then makes one bounded, independent audit append attempt.</summary>
    public async Task<T> ExecuteAsync<T>(UsageAuditOperation operation, string? query, Func<Task<T>> invoke, Func<T, McpUsageAuditResult> summarize)
    {
        var eventId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        T result;
        try { result = await invoke().ConfigureAwait(false); }
        catch (Exception error)
        {
            var failure = error switch
            {
                ArgumentException => UsageAuditFailureClass.Validation,
                OperationCanceledException => UsageAuditFailureClass.Timeout,
                UnauthorizedAccessException => UsageAuditFailureClass.Authorization,
                _ => UsageAuditFailureClass.Storage
            };
            await AppendSafelyAsync(eventId, occurredAt, operation, query, () => new(0, Outcome: UsageAuditOutcome.Failed, FailureClass: failure)).ConfigureAwait(false);
            throw;
        }
        // Summary creation is part of the observer, so its own failure must not turn a successful operation into an error.
        await AppendSafelyAsync(eventId, occurredAt, operation, query, () => summarize(result)).ConfigureAwait(false);
        return result;
    }

    /// <summary>Omits areaId because MCP has no shared P1.1 membership authority; all observer failures remain silent.</summary>
    private async Task AppendSafelyAsync(Guid eventId, DateTimeOffset occurredAt, UsageAuditOperation operation, string? query, Func<McpUsageAuditResult> summarize)
    {
        if (configuration.Factory is not { } factory) return;
        try
        {
            var summary = summarize();
            var usageEvent = factory.Create(eventId, occurredAt, UsageAuditSource.Mcp, operation, _client, null,
                query ?? "", summary.Count, summary.ContextChars, summary.Outcome, summary.FailureClass);
            using var deadline = new CancellationTokenSource(AuditTimeout);
            Task<UsageAuditAppendResult> append;
            lock (_pendingGate)
            {
                if (_disposed) return;
                append = store.AppendAsync(usageEvent, deadline.Token);
                _pendingAppends.Add(append);
            }
            _ = RemoveCompletedAppendAsync(append);
            await append.WaitAsync(AuditTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Audit is optional observability. No exception details, input text or keys are logged to stdio.
        }
    }

    /// <summary>Observes late append faults and releases completed tasks without retaining an unbounded audit history.</summary>
    private async Task RemoveCompletedAppendAsync(Task append)
    {
        try { await append.ConfigureAwait(false); }
        catch { /* A late optional audit failure must not become an unobserved task exception. */ }
        finally { lock (_pendingGate) _pendingAppends.Remove(append); }
    }

    /// <summary>Releases an owned audit connection only after native append calls stop using it; shutdown never waits for a stalled append.</summary>
    public ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_pendingGate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            pending = _pendingAppends.ToArray();
        }
        if (ownedStore is null) return ValueTask.CompletedTask;
        var cleanup = DisposeAfterAppendsAsync(pending, ownedStore);
        return cleanup.IsCompleted ? new ValueTask(cleanup) : ValueTask.CompletedTask;
    }

    /// <summary>Keeps the audit connection alive for outstanding native work and suppresses failures during optional cleanup.</summary>
    private static async Task DisposeAfterAppendsAsync(Task[] pending, IAsyncDisposable resource)
    {
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch { /* Append errors were already isolated from the invocation. */ }
        try { await resource.DisposeAsync().ConfigureAwait(false); }
        catch { /* Optional audit cleanup must not fail memory shutdown. */ }
    }

}
