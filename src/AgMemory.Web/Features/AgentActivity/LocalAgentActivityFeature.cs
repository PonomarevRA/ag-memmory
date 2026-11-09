using AgMemory.ActivityJournal;
using AgMemory.Core;
using AgMemory.Web.Features.MemoryGraph;

namespace AgMemory.Web.Features.AgentActivity;

/// <summary>Fixed-authority reader of the independent activity journal; browser filters never select an authority or data path.</summary>
public sealed class LocalAgentActivityFeature : IDisposable
{
    private readonly SegmentJournal? _store;
    private readonly string? _partition;
    private readonly LocalMemoryGraphFeature _graph;
    private readonly ActivityJournalOptions _options;
    private readonly string? _configurationError;
    private readonly SemaphoreSlim _targetGate = new(1, 1);
    private IReadOnlyDictionary<string, ActivityTarget> _targets = new Dictionary<string, ActivityTarget>();
    private DateTimeOffset _targetsExpire;
    private long _lastWriteSequence;
    public long Revision => _store?.Revision ?? 0;
    public bool IsConfigured => _partition is not null;

    public LocalAgentActivityFeature(MemoryGraphHostOptions graphOptions, string dataDirectory, LocalMemoryGraphFeature graph)
    {
        _graph = graph;
        _options = new();
        var configuration = graphOptions.TryCreate(dataDirectory);
        if (configuration is null) return;
        _partition = ActivityIdentity.Partition(configuration.Actor, configuration.Scope, configuration.StoragePath);
        try
        {
            _options = ActivityJournalOptions.Load(dataDirectory);
            _store = new(Path.Combine(dataDirectory, "agent-activity"), _options);
        }
        catch { _configurationError = "configuration_invalid"; }
    }

    public ActivityStatus Status()
    {
        try { if (_store is not null) return _store.Status(_partition); }
        catch { return Unavailable("read_failed"); }
        return Unavailable(_configurationError ?? "unavailable");
    }

    /// <summary>Returns a bounded authorized window and reprojects target tokens through the current authorized graph.</summary>
    public async Task<(ActivityRead Read, IReadOnlyList<ActivityEvent> Events)> ReadAsync(int limit, long? after, long? before,
        string? session, string? operation, string? query, CancellationToken cancellationToken)
    {
        if (_store is null || _partition is null) throw new InvalidOperationException("unavailable");
        var read = await Task.Run(() => _store.Read(_partition, limit, after, before, session, operation, query), cancellationToken).ConfigureAwait(false);
        return (read, await ProjectAsync(read.Events, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reads one committed v1 window, applying the new literal filters before page sampling.</summary>
    internal async Task<(ActivityRead Read, IReadOnlyList<ActivityEvent> Events)> ReadHttpAsync(ActivityHttpQuery query, CancellationToken cancellationToken)
    {
        if (_store is null || _partition is null) throw new InvalidOperationException("unavailable");
        var read = await Task.Run(() => _store.Read(_partition, query.Limit, query.After, query.Before, query.Session,
            query.Operation, filter: query.Matches, windowFilter: query.IsCurrent), cancellationToken).ConfigureAwait(false);
        if (!query.IsCurrent(read.Status)) return (read, []);
        return (read, await ProjectAsync(read.Events, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reads metadata only for the widget; no graph, source body or target projection is loaded.</summary>
    internal Task<ActivityRead> ReadWidgetEventsAsync(CancellationToken cancellationToken)
    {
        if (_store is null || _partition is null) throw new InvalidOperationException("unavailable");
        return Task.Run(() => _store.Read(_partition, 64, null, null, null, null, null), cancellationToken);
    }

    /// <summary>Shares the existing authorized target projection between old browser readers and the new HTTP adapter.</summary>
    private async Task<IReadOnlyList<ActivityEvent>> ProjectAsync(IReadOnlyList<StoredActivity> stored, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, ActivityTarget> targets = new Dictionary<string, ActivityTarget>();
        if (stored.Any(item => item.TargetToken is not null))
            targets = await TargetsAsync(stored, cancellationToken).ConfigureAwait(false);
        var events = new List<ActivityEvent>(stored.Count);
        foreach (var item in stored)
        {
            var target = item.TargetToken is not null && targets.TryGetValue(item.TargetToken, out var found) ? found : new ActivityTarget();
            events.Add(item.Event with { Target = target });
        }
        return events;
    }

    /// <summary>Reuses one bounded authorized projection for a second, invalidating immediately on observed canonical writes.</summary>
    private async Task<IReadOnlyDictionary<string, ActivityTarget>> TargetsAsync(IReadOnlyList<StoredActivity> events, CancellationToken cancellationToken)
    {
        await _targetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var invalidated = false;
            foreach (var item in events)
            {
                var operation = item.Event.Operation;
                var write = operation.Contains("Remember", StringComparison.Ordinal) || operation.Contains("Correct", StringComparison.Ordinal) ||
                    operation.Contains("Handoff", StringComparison.Ordinal) || operation.Contains("Decision", StringComparison.Ordinal);
                var sequence = ActivityJson.Sequence(item.Event.Sequence);
                if (write && item.Event.Phase == "finished" && sequence > _lastWriteSequence) { invalidated = true; _lastWriteSequence = sequence; }
            }
            if (invalidated || DateTimeOffset.UtcNow >= _targetsExpire)
            {
                _targets = await _graph.ReadActivityTargetsAsync(_partition!, cancellationToken).ConfigureAwait(false);
                _targetsExpire = DateTimeOffset.UtcNow.AddSeconds(1);
            }
            return _targets;
        }
        finally { _targetGate.Release(); }
    }

    public ActivityStatus Clear() => _partition is not null ? _store?.ClearPartition(_partition) ?? Unavailable("unavailable") : Unavailable("unavailable");
    public void Dispose() => _store?.Dispose();
    private ActivityStatus Unavailable(string code) => new("agent-activity/v1", "", _options.MaxSizeBytes, 0, "0", "0",
        new("degraded", 0, 0, 0, code), new("", "0"));
}
