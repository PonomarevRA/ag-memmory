using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace AgMemory.Web.Features.TechnicalLogs;

/// <summary>Captures native ILogger events without blocking on journal locks or disk; failures remain independent of application work.</summary>
public sealed class TechnicalLogProvider : ILoggerProvider, ISupportExternalScope, IAsyncDisposable
{
    // A pending bounded snapshot can exceed its final serialized event. Reserve conservatively before enqueueing.
    private const int QueueReservation = 131072;
    private readonly TechnicalLogOptions _options;
    private readonly Channel<TechnicalLogEvent> _queue;
    private readonly TechnicalLogJournal? _store;
    private readonly Task _worker;
    private readonly object _commitGate = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    private long _queuedBytes, _dropped, _reportedDropped;
    private int _queuedItems, _disposed;
    private string? _error;
    public Task Completion => _worker;
    internal TechnicalLogJournal? Journal => _store;

    public TechnicalLogProvider(string directory, TechnicalLogOptions options)
    {
        _options = options;
        try { options.Validate(); _store = new(Path.Combine(directory, "technical-logs"), options); }
        catch { _options = new(); _error = "configuration_unavailable"; }
        _queue = Channel.CreateBounded<TechnicalLogEvent>(new BoundedChannelOptions(_options.QueueCapacity)
        { SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
        _worker = Task.Run(WriteAsync);
    }

    public ILogger CreateLogger(string categoryName) => new NativeLogger(this, categoryName ?? "");
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    /// <summary>Preserves durable cross-process counts while unknown aggregate queue gauges stay null.</summary>
    public TechnicalLogStatus Status()
    {
        if (_store is null) throw new InvalidOperationException("journal_unavailable");
        lock (_commitGate)
        {
            var status = _store.Status();
            return status with { Health = VisibleHealth(status.Health) };
        }
    }

    internal TechnicalLogRead Read(TechnicalLogQuery query)
    {
        if (_store is null) throw new InvalidOperationException("journal_unavailable");
        lock (_commitGate)
        {
            var read = _store.Read(query);
            return read with { Status = read.Status with { Health = VisibleHealth(read.Status.Health) } };
        }
    }

    /// <summary>Includes known local losses not yet durable, without double-counting a concurrent successful commit.</summary>
    private TechnicalLogHealth VisibleHealth(TechnicalLogHealth durable)
    {
        var pending = Math.Max(0, Interlocked.Read(ref _dropped) - Interlocked.Read(ref _reportedDropped));
        return durable with
        {
            DroppedTotal = durable.DroppedTotal is null ? null : checked(durable.DroppedTotal.Value + pending),
            Status = _error is null ? durable.Status : "degraded", ErrorCode = _error ?? durable.ErrorCode
        };
    }

    /// <summary>Excludes protected gateway content, internal failures and framework request-access chatter.</summary>
    private bool Enabled(string category, LogLevel level) => _store is not null && Volatile.Read(ref _disposed) == 0 &&
        level != LogLevel.None && level >= _options.MinimumLevel &&
        !category.StartsWith("AgMemory.Web.Gateway", StringComparison.Ordinal) &&
        !category.StartsWith("AgMemory.Web.Features.TechnicalLogs", StringComparison.Ordinal) &&
        !category.StartsWith("Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.Ordinal) &&
        !(category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) && level < LogLevel.Warning) &&
        !TechnicalLogSuppression.Active.Value;

    /// <summary>Copies bounded scalar state and correlation; formatter, scope and redaction failures never escape ILogger.Log.</summary>
    private void Capture<TState>(string category, LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
    {
        if (!Enabled(category, level)) return;
        try
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            string? template = null, operation = null, session = null, task = null;
            var secrets = new List<string>();
            var omitSensitiveText = false;
            var omitObjectText = state is not string && state is not IEnumerable<KeyValuePair<string, object?>>;
            string SafeText(string? value, int maximum)
            {
                if (omitSensitiveText) return "[redacted sensitive state]";
                var bounded = value is null ? "" : value[..Math.Min(value.Length, 8192)];
                foreach (var secret in secrets) bounded = bounded.Replace(secret, "[redacted]", StringComparison.Ordinal);
                return TechnicalLogRedaction.Text(bounded, maximum);
            }
            void Copy(object? values)
            {
                if (values is not IEnumerable<KeyValuePair<string, object?>> pairs) return;
                var inspected = 0;
                foreach (var pair in pairs)
                {
                    if (++inspected > 32) { omitObjectText = true; break; }
                    if (TechnicalLogRedaction.IsSensitive(pair.Key) && pair.Value is not null)
                    {
                        if (pair.Value is not string sensitive) omitSensitiveText = true;
                        else if (sensitive.Length > 128) omitSensitiveText = true;
                        else if (sensitive.Length > 0 && secrets.Count < 32) secrets.Add(sensitive);
                    }
                    if (pair.Key == "{OriginalFormat}" && pair.Value is string original) { template = TechnicalLogRedaction.Text(original, 256); continue; }
                    if (pair.Value is string correlation)
                    {
                        if (pair.Key.Equals("OperationId", StringComparison.OrdinalIgnoreCase)) operation = TechnicalLogRedaction.Text(correlation, 128);
                        if (pair.Key.Equals("SessionId", StringComparison.OrdinalIgnoreCase)) session = TechnicalLogRedaction.Text(correlation, 128);
                        if (pair.Key.Equals("TaskId", StringComparison.OrdinalIgnoreCase)) task = TechnicalLogRedaction.Text(correlation, 128);
                    }
                    if (!TechnicalLogRedaction.TryScalar(pair.Key, pair.Value, out var scalar)) { omitObjectText = true; continue; }
                    if (properties.Count >= 16) continue;
                    properties[TechnicalLogRedaction.Text(pair.Key, 64)] = scalar;
                }
            }
            var scopeCount = 0;
            _scopes.ForEachScope((value, _) => { if (++scopeCount <= 16) Copy(value); }, 0);
            Copy(state);
            var activity = Activity.Current;
            var item = new TechnicalLogEvent
            {
                Category = TechnicalLogRedaction.Text(category, 128), Level = Level(level), EventName = id.Name is null ? null : TechnicalLogRedaction.Text(id.Name, 128),
                MessageTemplate = template is null ? null : SafeText(template, 256),
                // A native formatter may call arbitrary ToString or expose a typed sensitive value; omit that rendered text entirely.
                Message = omitSensitiveText ? "[redacted sensitive state]" : omitObjectText ? "[omitted non-scalar state]" : SafeText(formatter(state, error), 2048),
                Exception = error is null ? null : new(TechnicalLogRedaction.Text(error.GetType().FullName, 128), SafeText(error.Message, 1024),
                    error.StackTrace is null ? "" : SafeText(error.StackTrace, 8192)),
                TraceId = activity?.TraceId.ToString(), SpanId = activity?.SpanId.ToString(), OperationId = operation, SessionId = session, TaskId = task, Properties = properties
            };
            TryAppend(item);
        }
        catch { Interlocked.Increment(ref _dropped); }
    }

    /// <summary>Reserves queue bytes and uses TryWrite only; saturation drops rather than waiting.</summary>
    internal bool TryAppend(TechnicalLogEvent item)
    {
        if (Interlocked.Add(ref _queuedBytes, QueueReservation) > _options.QueueMaxBytes)
        { Interlocked.Add(ref _queuedBytes, -QueueReservation); Interlocked.Increment(ref _dropped); return false; }
        Interlocked.Increment(ref _queuedItems);
        if (_queue.Writer.TryWrite(item)) return true;
        Interlocked.Decrement(ref _queuedItems); Interlocked.Add(ref _queuedBytes, -QueueReservation); Interlocked.Increment(ref _dropped); return false;
    }

    /// <summary>Starts one batch timer after the first event; performs no idle polling, flush or recursive logging.</summary>
    private async Task WriteAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                var batch = new List<TechnicalLogEvent>();
                using var deadline = new CancellationTokenSource(_options.BatchDelayMs);
                while (batch.Count < _options.BatchBytes / 16384)
                {
                    if (_queue.Reader.TryRead(out var value))
                    {
                        Interlocked.Decrement(ref _queuedItems); Interlocked.Add(ref _queuedBytes, -QueueReservation);
                        try { batch.Add(Fit(value)); } catch { Interlocked.Increment(ref _dropped); }
                        continue;
                    }
                    try { if (!await _queue.Reader.WaitToReadAsync(deadline.Token).ConfigureAwait(false)) break; }
                    catch (OperationCanceledException) { break; }
                }
                if (batch.Count == 0) continue;
                try
                {
                    var dropped = Interlocked.Read(ref _dropped);
                    lock (_commitGate)
                    {
                        _store!.Append(batch, dropped - Interlocked.Read(ref _reportedDropped));
                        Interlocked.Exchange(ref _reportedDropped, dropped); _error = null;
                    }
                }
                catch { _error = "write_failed"; Interlocked.Add(ref _dropped, batch.Count); }
            }
        }
        finally { _store?.Dispose(); }
    }

    /// <summary>Truncates diagnostics before disk serialization, leaving room for sequence/time assignment.</summary>
    private static TechnicalLogEvent Fit(TechnicalLogEvent item)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(item, TechnicalLogJson.Options).Length <= 15872) return item;
        item = item with { Properties = new Dictionary<string, object?>(), Exception = item.Exception is null ? null : item.Exception with { StackTrace = TechnicalLogRedaction.Text(item.Exception.StackTrace, 1024) } };
        if (JsonSerializer.SerializeToUtf8Bytes(item, TechnicalLogJson.Options).Length <= 15872) return item;
        item = item with { Message = TechnicalLogRedaction.Text(item.Message, 512), MessageTemplate = TechnicalLogRedaction.Text(item.MessageTemplate, 128),
            Exception = item.Exception is null ? null : item.Exception with { StackTrace = "", Message = TechnicalLogRedaction.Text(item.Exception.Message, 256) } };
        if (JsonSerializer.SerializeToUtf8Bytes(item, TechnicalLogJson.Options).Length > 15872) throw new InvalidOperationException("oversize_event");
        return item;
    }

    private static string Level(LogLevel value) => value switch
    { LogLevel.Trace => "TRACE", LogLevel.Debug => "DEBUG", LogLevel.Information => "INFO", LogLevel.Warning => "WARN", LogLevel.Error => "ERROR", _ => "FATAL" };

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Bounds graceful shutdown to two seconds; a late writer keeps ownership of its own journal handle.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { _error = "shutdown_timeout"; }
    }

    private sealed class NativeLogger(TechnicalLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => owner.Enabled(category, logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => owner.Capture(category, logLevel, eventId, state, exception, formatter);
    }
}

/// <summary>Suppresses logs generated by reading this journal in the current HTTP execution context.</summary>
internal static class TechnicalLogSuppression
{
    public static AsyncLocal<bool> Active { get; } = new();
}
