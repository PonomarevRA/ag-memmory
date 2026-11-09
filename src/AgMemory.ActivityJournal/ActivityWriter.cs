using System.Threading.Channels;

namespace AgMemory.ActivityJournal;

/// <summary>Nonblocking producer queue. Serialization, cross-process coordination and fsync run only on its worker.</summary>
public sealed class ActivityWriter : IAsyncDisposable
{
    private const int EventBudget = 8192;
    private readonly Channel<StoredActivity> _queue;
    private readonly SegmentJournal _store;
    private readonly ActivityJournalOptions _options;
    private readonly Task _worker;
    private long _queuedBytes;
    private int _queuedItems;
    private long _dropped;
    private string? _error;
    private long _reportedDropped;
    public Task Completion => _worker;
    public ActivityHealth Health => new(_error is null ? "healthy" : "degraded", Interlocked.Read(ref _dropped),
        Volatile.Read(ref _queuedItems), Interlocked.Read(ref _queuedBytes), _error);

    public ActivityWriter(SegmentJournal store, ActivityJournalOptions options)
    {
        _store = store;
        _options = options;
        _queue = Channel.CreateBounded<StoredActivity>(new BoundedChannelOptions(options.QueueCapacity) { SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
        _worker = Task.Run(WriteAsync);
    }

    /// <summary>Reserves a conservative event byte budget and drops immediately rather than delaying a memory operation.</summary>
    public bool TryAppend(StoredActivity value)
    {
        if (!Valid(value)) { Interlocked.Increment(ref _dropped); return false; }
        if (Interlocked.Add(ref _queuedBytes, EventBudget) > _options.QueueMaxBytes)
        { Interlocked.Add(ref _queuedBytes, -EventBudget); Interlocked.Increment(ref _dropped); return false; }
        Interlocked.Increment(ref _queuedItems);
        if (_queue.Writer.TryWrite(value)) return true;
        Interlocked.Decrement(ref _queuedItems);
        Interlocked.Add(ref _queuedBytes, -EventBudget);
        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>Only the metadata producer shape is accepted, making the fixed byte reservation a conservative bound.</summary>
    private static bool Valid(StoredActivity value)
    {
        var e = value.Event;
        return value.Partition.Length == 64 && (value.TargetToken?.Length ?? 0) <= 64 &&
            e.EventId.Length <= 32 && e.SessionId.Length <= 32 && e.OperationId.Length <= 32 && e.Operation.Length <= 100 &&
            e.ClientKind.Length <= 16 && e.Query.Mode is "omitted" or "preview" && (e.Query.Preview?.Length ?? 0) <= 256 && e.Query.Fingerprint is null &&
            (e.Request?.Tool.Length ?? 0) <= 100 && (e.Request?.Summary.Length ?? 0) <= 128 &&
            (e.Response?.Summary.Length ?? 0) <= 128 && e.AgentId is null && e.Application == "agmemory" && (e.ErrorCode?.Length ?? 0) <= 32 &&
            e.Operation.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    /// <summary>Starts the batch deadline with the first item; there are no idle flushes or file reads.</summary>
    private async Task WriteAsync()
    {
        var maximum = Math.Max(1, Math.Min(_options.BatchBytes, (int)Math.Min(int.MaxValue, (_options.MaxSizeBytes - 8192) / 4)) / EventBudget);
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batch = new List<StoredActivity>(maximum);
            using var deadline = new CancellationTokenSource(_options.BatchDelayMs);
            while (batch.Count < maximum)
            {
                if (_queue.Reader.TryRead(out var value))
                {
                    Interlocked.Decrement(ref _queuedItems);
                    Interlocked.Add(ref _queuedBytes, -EventBudget);
                    batch.Add(value);
                    continue;
                }
                try { if (!await _queue.Reader.WaitToReadAsync(deadline.Token).ConfigureAwait(false)) break; }
                catch (OperationCanceledException) { break; }
            }
            try { var dropped = Interlocked.Read(ref _dropped); _store.Append(batch, dropped - _reportedDropped); _reportedDropped = dropped; _error = null; }
            catch (Exception error)
            {
                // Technical failures stay aggregate-only and never replace an MCP result or write to stdout.
                _error = error is InvalidOperationException ? "configuration_mismatch" : "write_failed";
                Interlocked.Add(ref _dropped, batch.Count);
            }
        }
    }

    /// <summary>Completes a bounded graceful drain. A blocked filesystem does not hold application shutdown indefinitely.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { _error = "shutdown_timeout"; }
    }
}
