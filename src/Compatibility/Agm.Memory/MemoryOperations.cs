using Agm.Memory.Abstractions;

namespace Agm.Memory;

public sealed class BoundedMemoryOperationsTelemetry(
    int maximumReceipts = 256,
    int maximumStagesPerReceipt = 16) : IMemoryOperationsTelemetry
{
    private readonly object gate = new();
    private readonly Queue<MemoryTraceReceipt> receipts = new();
    private readonly Dictionary<(MemoryOperationKind, MemoryOperationStage), MutableStageMetrics> metrics = new();
    private long receiptCount;
    private long droppedReceiptCount;

    public void Record(MemoryTraceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (maximumReceipts < 1) throw new ArgumentOutOfRangeException(nameof(maximumReceipts));
        if (maximumStagesPerReceipt < 1) throw new ArgumentOutOfRangeException(nameof(maximumStagesPerReceipt));
        if (!MemoryTraceId.TryParse(receipt.TraceId.Value, out _)) throw new ArgumentException("Trace ID is invalid.", nameof(receipt));
        if (receipt.CompletedAt < receipt.StartedAt) throw new ArgumentException("Receipt completion precedes its start.", nameof(receipt));
        if (receipt.Stages.Count > maximumStagesPerReceipt) throw new ArgumentException("Receipt contains too many stages.", nameof(receipt));
        if (receipt.SourceCount < 0 || receipt.SupersededPrimaryCount < 0) throw new ArgumentOutOfRangeException(nameof(receipt));
        foreach (var stage in receipt.Stages)
            if (stage.Duration < TimeSpan.Zero || stage.ItemCount < 0)
                throw new ArgumentException("Stage values must not be negative.", nameof(receipt));

        lock (gate)
        {
            receiptCount++;
            foreach (var stage in receipt.Stages)
            {
                var key = (receipt.Operation, stage.Stage);
                if (!metrics.TryGetValue(key, out var value)) metrics[key] = value = new();
                value.Count++;
                value.ItemCount += stage.ItemCount;
                value.TotalTicks += stage.Duration.Ticks;
                value.MaximumTicks = Math.Max(value.MaximumTicks, stage.Duration.Ticks);
                if (stage.Outcome != MemoryOperationOutcome.Succeeded) value.FailureCount++;
            }
            while (receipts.Count >= maximumReceipts)
            {
                receipts.Dequeue();
                droppedReceiptCount++;
            }
            receipts.Enqueue(receipt);
        }
    }

    public MemoryTelemetrySnapshot Snapshot()
    {
        lock (gate)
        {
            var stageSnapshots = metrics
                .OrderBy(item => item.Key.Item1)
                .ThenBy(item => item.Key.Item2)
                .Select(item => new MemoryStageMetrics(
                    item.Key.Item1, item.Key.Item2, item.Value.Count, item.Value.FailureCount,
                    item.Value.ItemCount, TimeSpan.FromTicks(item.Value.TotalTicks),
                    TimeSpan.FromTicks(item.Value.MaximumTicks)))
                .ToArray();
            return new MemoryTelemetrySnapshot(receiptCount, droppedReceiptCount, stageSnapshots, receipts.ToArray());
        }
    }

    private sealed class MutableStageMetrics
    {
        public long Count;
        public long FailureCount;
        public long ItemCount;
        public long TotalTicks;
        public long MaximumTicks;
    }
}

public sealed class MemoryCanaryGate : IMemoryCanaryGate
{
    public MemoryCanaryDecision Evaluate(
        MemoryCanaryMeasurement baseline,
        MemoryCanaryMeasurement candidate,
        MemoryCanaryCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(criteria);
        ValidateMeasurement(baseline);
        ValidateMeasurement(candidate);
        ValidateCriteria(criteria);

        var failures = new List<MemoryCanaryFailure>();
        if (candidate.Quality.MeanRecall < baseline.Quality.MeanRecall - criteria.MaximumRecallDrop)
            failures.Add(MemoryCanaryFailure.RecallRegression);
        if (candidate.Quality.MeanNdcg < baseline.Quality.MeanNdcg - criteria.MaximumNdcgDrop)
            failures.Add(MemoryCanaryFailure.NdcgRegression);
        if (candidate.SourceCoverage < criteria.MinimumSourceCoverage)
            failures.Add(MemoryCanaryFailure.SourceCoverageBelowThreshold);
        if (candidate.SupersededPrimaryRate > criteria.MaximumSupersededPrimaryRate)
            failures.Add(MemoryCanaryFailure.SupersededPrimaryRateExceeded);
        if (candidate.Quality.P95Latency > criteria.EffectiveP95LatencyBudget)
            failures.Add(MemoryCanaryFailure.P95LatencyBudgetExceeded);

        var canPromote = failures.Count == 0;
        return new MemoryCanaryDecision(
            baseline.ConfigurationVersion,
            candidate.ConfigurationVersion,
            canPromote,
            canPromote ? candidate.ConfigurationVersion : baseline.ConfigurationVersion,
            baseline.ConfigurationVersion,
            failures);
    }

    private static void ValidateMeasurement(MemoryCanaryMeasurement measurement)
    {
        MemoryPrivacy.ValidateOpaqueIdentifier(measurement.ConfigurationVersion, nameof(measurement.ConfigurationVersion));
        ArgumentNullException.ThrowIfNull(measurement.Quality);
        ValidateFraction(measurement.Quality.MeanRecall, nameof(measurement.Quality.MeanRecall));
        ValidateFraction(measurement.Quality.MeanNdcg, nameof(measurement.Quality.MeanNdcg));
        if (measurement.Quality.P95Latency < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(measurement.Quality.P95Latency));
        ValidateFraction(measurement.SourceCoverage, nameof(measurement.SourceCoverage));
        ValidateFraction(measurement.SupersededPrimaryRate, nameof(measurement.SupersededPrimaryRate));
    }

    private static void ValidateCriteria(MemoryCanaryCriteria criteria)
    {
        ValidateFraction(criteria.MaximumRecallDrop, nameof(criteria.MaximumRecallDrop));
        ValidateFraction(criteria.MaximumNdcgDrop, nameof(criteria.MaximumNdcgDrop));
        ValidateFraction(criteria.MinimumSourceCoverage, nameof(criteria.MinimumSourceCoverage));
        ValidateFraction(criteria.MaximumSupersededPrimaryRate, nameof(criteria.MaximumSupersededPrimaryRate));
        if (criteria.EffectiveP95LatencyBudget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(criteria));
    }

    private static void ValidateFraction(double value, string name)
    {
        if (double.IsNaN(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(name);
    }
}

public sealed class VersionedMemoryConfigurationRegistry<TConfiguration>(int maximumVersions = 16)
    : IMemoryConfigurationRegistry<TConfiguration>
{
    private readonly object gate = new();
    private readonly Dictionary<string, MemoryConfigurationVersion<TConfiguration>> versions = new(StringComparer.Ordinal);
    private string? activeVersion;
    private string? rollbackVersion;

    public void Register(MemoryConfigurationVersion<TConfiguration> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (maximumVersions < 2) throw new ArgumentOutOfRangeException(nameof(maximumVersions));
        MemoryPrivacy.ValidateOpaqueIdentifier(configuration.Version, nameof(configuration.Version));
        lock (gate)
        {
            if (versions.ContainsKey(configuration.Version)) throw new InvalidOperationException("Configuration version already exists.");
            if (versions.Count >= maximumVersions) throw new InvalidOperationException("Configuration registry capacity exceeded.");
            versions.Add(configuration.Version, configuration);
            activeVersion ??= configuration.Version;
        }
    }

    public bool TryPromote(MemoryCanaryDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        lock (gate)
        {
            if (!decision.CanPromote || activeVersion != decision.BaselineVersion ||
                decision.RollbackVersion != activeVersion || decision.ActiveVersion != decision.CandidateVersion ||
                decision.Failures.Count != 0 || !versions.ContainsKey(decision.CandidateVersion)) return false;
            rollbackVersion = activeVersion;
            activeVersion = decision.CandidateVersion;
            return true;
        }
    }

    public bool TryRollback()
    {
        lock (gate)
        {
            if (rollbackVersion is null || !versions.ContainsKey(rollbackVersion)) return false;
            (activeVersion, rollbackVersion) = (rollbackVersion, activeVersion);
            return true;
        }
    }

    public MemoryConfigurationRegistrySnapshot<TConfiguration> Snapshot()
    {
        lock (gate)
        {
            return new(
                activeVersion is null ? null : versions[activeVersion],
                rollbackVersion is null ? null : versions[rollbackVersion],
                versions.Keys.Order(StringComparer.Ordinal).ToArray());
        }
    }
}

public sealed class DeterministicMemoryRetryQueue<TPayload>(
    int maximumPending = 256,
    int maximumAttempts = 5,
    int maximumDeadLetters = 256,
    TimeSpan? baseDelay = null) : IMemoryRetryQueue<TPayload>
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Queue<MemoryDeadLetter<TPayload>> deadLetters = new();
    private readonly Queue<string> completedKeys = new();
    private readonly HashSet<string> completed = new(StringComparer.Ordinal);
    private long sequence;

    public MemoryRetryEnqueueOutcome Enqueue(string idempotencyKey, TPayload payload, DateTimeOffset now)
    {
        ValidateOptions();
        MemoryPrivacy.ValidateOpaqueIdentifier(idempotencyKey, nameof(idempotencyKey));
        lock (gate)
        {
            if (entries.ContainsKey(idempotencyKey) || completed.Contains(idempotencyKey) ||
                deadLetters.Any(item => item.Item.IdempotencyKey == idempotencyKey)) return MemoryRetryEnqueueOutcome.Duplicate;
            if (entries.Count >= maximumPending) return MemoryRetryEnqueueOutcome.CapacityExceeded;
            entries.Add(idempotencyKey, new(new(idempotencyKey, payload, 0, now, now), sequence++));
            return MemoryRetryEnqueueOutcome.Enqueued;
        }
    }

    public MemoryRetryItem<TPayload>? TryClaim(DateTimeOffset now)
    {
        ValidateOptions();
        lock (gate)
        {
            var entry = entries.Values
                .Where(item => !item.InFlight && item.Item.NextAttemptAt <= now)
                .OrderBy(item => item.Item.NextAttemptAt)
                .ThenBy(item => item.Sequence)
                .FirstOrDefault();
            if (entry is null) return null;
            entry.InFlight = true;
            entry.Item = entry.Item with { Attempt = entry.Item.Attempt + 1 };
            return entry.Item;
        }
    }

    public MemoryRetryFailureOutcome Fail(string idempotencyKey, MemoryFailureKind failure, DateTimeOffset now)
    {
        MemoryPrivacy.ValidateOpaqueIdentifier(idempotencyKey, nameof(idempotencyKey));
        lock (gate)
        {
            if (!entries.TryGetValue(idempotencyKey, out var entry) || !entry.InFlight)
                return MemoryRetryFailureOutcome.NotFound;
            if (entry.Item.Attempt >= maximumAttempts)
            {
                entries.Remove(idempotencyKey);
                while (deadLetters.Count >= maximumDeadLetters) deadLetters.Dequeue();
                deadLetters.Enqueue(new(entry.Item, failure, now));
                return MemoryRetryFailureOutcome.DeadLettered;
            }
            entry.InFlight = false;
            entry.Item = entry.Item with { NextAttemptAt = now + DelayFor(entry.Item.Attempt) };
            return MemoryRetryFailureOutcome.Rescheduled;
        }
    }

    public bool Complete(string idempotencyKey)
    {
        MemoryPrivacy.ValidateOpaqueIdentifier(idempotencyKey, nameof(idempotencyKey));
        lock (gate)
        {
            if (!entries.TryGetValue(idempotencyKey, out var entry) || !entry.InFlight) return false;
            entries.Remove(idempotencyKey);
            while (completedKeys.Count >= maximumPending) completed.Remove(completedKeys.Dequeue());
            completed.Add(idempotencyKey);
            completedKeys.Enqueue(idempotencyKey);
            return true;
        }
    }

    public MemoryRetryQueueSnapshot Snapshot(DateTimeOffset now)
    {
        lock (gate)
        {
            var ready = entries.Values.Where(item => !item.InFlight && item.Item.NextAttemptAt <= now).ToArray();
            var scheduled = entries.Values.Where(item => !item.InFlight && item.Item.NextAttemptAt > now).ToArray();
            var oldest = ready.Length == 0 ? TimeSpan.Zero : now - ready.Min(item => item.Item.NextAttemptAt);
            return new(
                ready.Length,
                scheduled.Length,
                entries.Values.Count(item => item.InFlight),
                deadLetters.Count,
                oldest < TimeSpan.Zero ? TimeSpan.Zero : oldest,
                scheduled.Length == 0 ? null : scheduled.Min(item => item.Item.NextAttemptAt));
        }
    }

    public IReadOnlyList<MemoryDeadLetter<TPayload>> GetDeadLetters()
    {
        lock (gate) return deadLetters.ToArray();
    }

    private TimeSpan DelayFor(int attempt)
    {
        var delay = baseDelay ?? TimeSpan.FromSeconds(1);
        var multiplier = 1L << Math.Min(attempt - 1, 30);
        var ticks = Math.Min(TimeSpan.MaxValue.Ticks, delay.Ticks * (double)multiplier);
        return TimeSpan.FromTicks((long)ticks);
    }

    private void ValidateOptions()
    {
        if (maximumPending < 1) throw new ArgumentOutOfRangeException(nameof(maximumPending));
        if (maximumAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        if (maximumDeadLetters < 1) throw new ArgumentOutOfRangeException(nameof(maximumDeadLetters));
        if ((baseDelay ?? TimeSpan.FromSeconds(1)) <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(baseDelay));
    }

    private sealed class Entry(MemoryRetryItem<TPayload> item, long sequence)
    {
        public MemoryRetryItem<TPayload> Item = item;
        public long Sequence { get; } = sequence;
        public bool InFlight;
    }
}
