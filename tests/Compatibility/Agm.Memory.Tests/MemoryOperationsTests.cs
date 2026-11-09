using Agm.Memory.Abstractions;
using Xunit;

namespace Agm.Memory.Tests;

public sealed class MemoryOperationsTests
{
    [Fact]
    public void CanaryPass_PromotesCandidateAndRetainsRollback()
    {
        var gate = new MemoryCanaryGate();
        var decision = gate.Evaluate(Measurement("v1"), Measurement("v2"), new());
        var registry = new VersionedMemoryConfigurationRegistry<string>();
        registry.Register(new("v1", "baseline", DateTimeOffset.UnixEpoch));
        registry.Register(new("v2", "candidate", DateTimeOffset.UnixEpoch));

        Assert.True(decision.CanPromote);
        Assert.True(registry.TryPromote(decision));
        Assert.Equal("v2", registry.Snapshot().Active!.Version);
        Assert.Equal("v1", registry.Snapshot().Rollback!.Version);
        Assert.True(registry.TryRollback());
        Assert.Equal("v1", registry.Snapshot().Active!.Version);
    }

    [Fact]
    public void CanaryFailure_BlocksPromotionAndKeepsBaseline()
    {
        var baseline = Measurement("v1");
        var candidate = Measurement("v2") with
        {
            Quality = Quality(recall: 0.7, ndcg: 0.6, latencyMs: 500),
            SourceCoverage = 0.8,
            SupersededPrimaryRate = 0.1
        };
        var decision = new MemoryCanaryGate().Evaluate(baseline, candidate, new());
        var registry = new VersionedMemoryConfigurationRegistry<string>();
        registry.Register(new("v1", "baseline", DateTimeOffset.UnixEpoch));
        registry.Register(new("v2", "candidate", DateTimeOffset.UnixEpoch));

        Assert.False(decision.CanPromote);
        Assert.False(registry.TryPromote(decision));
        Assert.Equal("v1", registry.Snapshot().Active!.Version);
        Assert.Contains(MemoryCanaryFailure.RecallRegression, decision.Failures);
        Assert.Contains(MemoryCanaryFailure.NdcgRegression, decision.Failures);
        Assert.Contains(MemoryCanaryFailure.SourceCoverageBelowThreshold, decision.Failures);
        Assert.Contains(MemoryCanaryFailure.SupersededPrimaryRateExceeded, decision.Failures);
        Assert.Contains(MemoryCanaryFailure.P95LatencyBudgetExceeded, decision.Failures);
    }

    [Fact]
    public void RetryQueue_IsIdempotentDeterministicAndDeadLettersAfterExhaustion()
    {
        var queue = new DeterministicMemoryRetryQueue<string>(maximumAttempts: 2, baseDelay: TimeSpan.FromSeconds(5));
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal(MemoryRetryEnqueueOutcome.Enqueued, queue.Enqueue("event-1", "payload", now));
        Assert.Equal(MemoryRetryEnqueueOutcome.Duplicate, queue.Enqueue("event-1", "other", now));

        var first = queue.TryClaim(now)!;
        Assert.Equal(1, first.Attempt);
        Assert.Equal(MemoryRetryFailureOutcome.Rescheduled, queue.Fail("event-1", MemoryFailureKind.Dependency, now));
        Assert.Null(queue.TryClaim(now + TimeSpan.FromSeconds(4)));
        Assert.Equal(now + TimeSpan.FromSeconds(5), queue.Snapshot(now).NextDueAt);

        var second = queue.TryClaim(now + TimeSpan.FromSeconds(5))!;
        Assert.Equal(2, second.Attempt);
        Assert.Equal(MemoryRetryFailureOutcome.DeadLettered,
            queue.Fail("event-1", MemoryFailureKind.Dependency, now + TimeSpan.FromSeconds(5)));
        Assert.Single(queue.GetDeadLetters());
        Assert.Equal(1, queue.Snapshot(now + TimeSpan.FromSeconds(5)).DeadLetterCount);
        Assert.Equal(MemoryRetryEnqueueOutcome.Duplicate, queue.Enqueue("event-1", "again", now));
    }

    [Fact]
    public void Telemetry_IsBoundedLowCardinalityAndAggregatesMetrics()
    {
        var telemetry = new BoundedMemoryOperationsTelemetry(maximumReceipts: 2);
        telemetry.Record(Receipt(MemoryOperationKind.Retrieval, MemoryOperationStage.Lexical, 10, 3));
        telemetry.Record(Receipt(MemoryOperationKind.Retrieval, MemoryOperationStage.Lexical, 20, 4));
        telemetry.Record(Receipt(MemoryOperationKind.Ingestion, MemoryOperationStage.Persist, 30, 2,
            MemoryOperationOutcome.Failed));

        var snapshot = telemetry.Snapshot();
        Assert.Equal(3, snapshot.ReceiptCount);
        Assert.Equal(1, snapshot.DroppedReceiptCount);
        Assert.Equal(2, snapshot.RecentReceipts.Count);
        var lexical = Assert.Single(snapshot.Stages, item => item.Stage == MemoryOperationStage.Lexical);
        Assert.Equal(2, lexical.Count);
        Assert.Equal(7, lexical.ItemCount);
        Assert.Equal(TimeSpan.FromMilliseconds(30), lexical.TotalDuration);
        var persist = Assert.Single(snapshot.Stages, item => item.Stage == MemoryOperationStage.Persist);
        Assert.Equal(1, persist.FailureCount);
    }

    [Fact]
    public void TraceReceipt_HasNoTenantOrRawTextAndPrivacyRedactionIsTotal()
    {
        const string tenantSecret = "tenant-acme-secret";
        var receipt = Receipt(MemoryOperationKind.Retrieval, MemoryOperationStage.Complete, 5, 1);

        var serializedShape = string.Join('|', receipt.GetType().GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("Tenant", serializedShape, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Query", serializedShape, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Content", serializedShape, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MemoryPrivacy.Redacted, MemoryPrivacy.RedactRawText(tenantSecret));
        Assert.DoesNotContain(tenantSecret, MemoryPrivacy.RedactRawText(tenantSecret));
        Assert.Throws<ArgumentException>(() => MemoryPrivacy.ValidateOpaqueIdentifier("tenant raw text", "value"));
    }

    [Fact]
    public void Telemetry_RejectsUnboundedTraceStages()
    {
        var telemetry = new BoundedMemoryOperationsTelemetry(maximumStagesPerReceipt: 1);
        var receipt = new MemoryTraceReceipt(
            MemoryTraceId.Create(), MemoryOperationKind.Retrieval, MemoryOperationOutcome.Succeeded,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [
                new(MemoryOperationStage.Validate, MemoryOperationOutcome.Succeeded, TimeSpan.Zero),
                new(MemoryOperationStage.Complete, MemoryOperationOutcome.Succeeded, TimeSpan.Zero)
            ]);

        Assert.Throws<ArgumentException>(() => telemetry.Record(receipt));
    }

    [Fact]
    public void RetryQueue_ReportsReadyLagAndCapacityWithoutStartingExtraWork()
    {
        var queue = new DeterministicMemoryRetryQueue<string>(maximumPending: 1);
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal(MemoryRetryEnqueueOutcome.Enqueued, queue.Enqueue("one", "payload", now));
        Assert.Equal(MemoryRetryEnqueueOutcome.CapacityExceeded, queue.Enqueue("two", "payload", now));

        var snapshot = queue.Snapshot(now + TimeSpan.FromSeconds(3));
        Assert.Equal(1, snapshot.ReadyCount);
        Assert.Equal(TimeSpan.FromSeconds(3), snapshot.OldestReadyAge);
    }

    [Fact]
    public void RetryQueue_DoesNotCompleteAnUnclaimedItem()
    {
        var queue = new DeterministicMemoryRetryQueue<string>();
        queue.Enqueue("pending", "payload", DateTimeOffset.UnixEpoch);

        Assert.False(queue.Complete("pending"));
        Assert.Equal(1, queue.Snapshot(DateTimeOffset.UnixEpoch).ReadyCount);
    }

    private static MemoryCanaryMeasurement Measurement(string version) =>
        new(version, Quality(), 1, 0);

    private static MemoryQualityMetrics Quality(double recall = 0.9, double ndcg = 0.85, int latencyMs = 100) =>
        new(recall, ndcg, 1, 1, TimeSpan.FromMilliseconds(latencyMs), 100);

    private static MemoryTraceReceipt Receipt(
        MemoryOperationKind operation,
        MemoryOperationStage stage,
        int milliseconds,
        int itemCount,
        MemoryOperationOutcome outcome = MemoryOperationOutcome.Succeeded) => new(
            MemoryTraceId.Create(), operation, outcome,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(milliseconds),
            [new(stage, outcome, TimeSpan.FromMilliseconds(milliseconds), itemCount)],
            SourceCount: itemCount,
            SupersededPrimaryCount: 0,
            Failure: outcome == MemoryOperationOutcome.Succeeded ? MemoryFailureKind.None : MemoryFailureKind.Dependency);
}
