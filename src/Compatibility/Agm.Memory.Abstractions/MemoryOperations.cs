namespace Agm.Memory.Abstractions;

public enum MemoryOperationKind { Retrieval, Ingestion }
public enum MemoryOperationStage { Validate, Lexical, Vector, Fusion, Graph, Reranker, Context, Persist, Index, Complete }
public enum MemoryOperationOutcome { Succeeded, Failed, Cancelled, Rejected }
public enum MemoryFailureKind { None, Validation, Capacity, Dependency, Timeout, Privacy, Unknown }

public sealed record MemoryStageReceipt(
    MemoryOperationStage Stage,
    MemoryOperationOutcome Outcome,
    TimeSpan Duration,
    int ItemCount = 0);

/// <summary>A privacy-safe receipt: it intentionally has no tenant, project, query, content, or free-form labels.</summary>
public sealed record MemoryTraceReceipt(
    MemoryTraceId TraceId,
    MemoryOperationKind Operation,
    MemoryOperationOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<MemoryStageReceipt> Stages,
    int SourceCount = 0,
    int SupersededPrimaryCount = 0,
    MemoryFailureKind Failure = MemoryFailureKind.None);

public sealed record MemoryStageMetrics(
    MemoryOperationKind Operation,
    MemoryOperationStage Stage,
    long Count,
    long FailureCount,
    long ItemCount,
    TimeSpan TotalDuration,
    TimeSpan MaximumDuration);

public sealed record MemoryTelemetrySnapshot(
    long ReceiptCount,
    long DroppedReceiptCount,
    IReadOnlyList<MemoryStageMetrics> Stages,
    IReadOnlyList<MemoryTraceReceipt> RecentReceipts);

public interface IMemoryOperationsTelemetry
{
    void Record(MemoryTraceReceipt receipt);
    MemoryTelemetrySnapshot Snapshot();
}

public sealed record MemoryCanaryMeasurement(
    string ConfigurationVersion,
    MemoryQualityMetrics Quality,
    double SourceCoverage,
    double SupersededPrimaryRate);

public sealed record MemoryCanaryCriteria(
    double MaximumRecallDrop = 0,
    double MaximumNdcgDrop = 0,
    double MinimumSourceCoverage = 1,
    double MaximumSupersededPrimaryRate = 0,
    TimeSpan? P95LatencyBudget = null)
{
    public TimeSpan EffectiveP95LatencyBudget => P95LatencyBudget ?? TimeSpan.FromMilliseconds(250);
}

public enum MemoryCanaryFailure
{
    RecallRegression,
    NdcgRegression,
    SourceCoverageBelowThreshold,
    SupersededPrimaryRateExceeded,
    P95LatencyBudgetExceeded
}

public sealed record MemoryCanaryDecision(
    string BaselineVersion,
    string CandidateVersion,
    bool CanPromote,
    string ActiveVersion,
    string RollbackVersion,
    IReadOnlyList<MemoryCanaryFailure> Failures);

public interface IMemoryCanaryGate
{
    MemoryCanaryDecision Evaluate(
        MemoryCanaryMeasurement baseline,
        MemoryCanaryMeasurement candidate,
        MemoryCanaryCriteria criteria);
}

public sealed record MemoryConfigurationVersion<TConfiguration>(
    string Version,
    TConfiguration Configuration,
    DateTimeOffset RegisteredAt);

public sealed record MemoryConfigurationRegistrySnapshot<TConfiguration>(
    MemoryConfigurationVersion<TConfiguration>? Active,
    MemoryConfigurationVersion<TConfiguration>? Rollback,
    IReadOnlyList<string> RegisteredVersions);

public interface IMemoryConfigurationRegistry<TConfiguration>
{
    void Register(MemoryConfigurationVersion<TConfiguration> configuration);
    bool TryPromote(MemoryCanaryDecision decision);
    bool TryRollback();
    MemoryConfigurationRegistrySnapshot<TConfiguration> Snapshot();
}

public enum MemoryRetryEnqueueOutcome { Enqueued, Duplicate, CapacityExceeded }
public enum MemoryRetryFailureOutcome { Rescheduled, DeadLettered, NotFound }

public sealed record MemoryRetryItem<TPayload>(
    string IdempotencyKey,
    TPayload Payload,
    int Attempt,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset NextAttemptAt);

public sealed record MemoryDeadLetter<TPayload>(
    MemoryRetryItem<TPayload> Item,
    MemoryFailureKind Failure,
    DateTimeOffset FailedAt);

public sealed record MemoryRetryQueueSnapshot(
    int ReadyCount,
    int ScheduledCount,
    int InFlightCount,
    int DeadLetterCount,
    TimeSpan OldestReadyAge,
    DateTimeOffset? NextDueAt);

public interface IMemoryRetryQueue<TPayload>
{
    MemoryRetryEnqueueOutcome Enqueue(string idempotencyKey, TPayload payload, DateTimeOffset now);
    MemoryRetryItem<TPayload>? TryClaim(DateTimeOffset now);
    MemoryRetryFailureOutcome Fail(string idempotencyKey, MemoryFailureKind failure, DateTimeOffset now);
    bool Complete(string idempotencyKey);
    MemoryRetryQueueSnapshot Snapshot(DateTimeOffset now);
    IReadOnlyList<MemoryDeadLetter<TPayload>> GetDeadLetters();
}

public static class MemoryPrivacy
{
    public const string Redacted = "[redacted]";

    public static string RedactRawText(string? value) => string.IsNullOrEmpty(value) ? string.Empty : Redacted;

    public static void ValidateOpaqueIdentifier(string value, string parameterName, int maximumLength = 96)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':'))
            throw new ArgumentException("Identifier must be a bounded opaque ASCII token.", parameterName);
    }
}
