using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    public async Task<UsageAuditAppendResult> AppendAsync(UsageAuditEvent usageEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usageEvent);
        usageEvent.Validate();
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            using var table = await OpenTableAsync(UsageAuditEventsTable, cancellationToken).ConfigureAwait(false);
            var batches = await table.Query().Where($"event_id = {Literal(usageEvent.EventId.ToString("D"))}").ToArrow().ConfigureAwait(false);
            var existing = ReadUsageAuditEvents(batches).ToArray();
            if (existing.Length > 1)
                throw new InvalidDataException("Usage audit event id uniqueness was violated.");
            if (existing.Length == 1)
            {
                if (existing[0] == usageEvent) return UsageAuditAppendResult.IdempotencyReplay;
                throw new UsageAuditEventConflictException(usageEvent.EventId);
            }

            await table.Add(BuildUsageAuditEventBatch([usageEvent])).ConfigureAwait(false);
            return UsageAuditAppendResult.Appended;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset cutoffExclusiveUtc, CancellationToken cancellationToken)
    {
        if (cutoffExclusiveUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Usage audit cleanup cutoffs must be UTC.", nameof(cutoffExclusiveUtc));
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            using var table = await OpenTableAsync(UsageAuditEventsTable, cancellationToken).ConfigureAwait(false);
            var predicate = $"occurred_at_utc < {Literal(Utc(cutoffExclusiveUtc))}";
            var expired = await table.Query().Where(predicate).ToArrow().ConfigureAwait(false);
            var count = ReadUsageAuditEvents(expired).Count();
            if (count > 0) await table.Delete(predicate).ConfigureAwait(false);
            return count;
        }
        finally
        {
            _gate.Release();
        }
    }
}
