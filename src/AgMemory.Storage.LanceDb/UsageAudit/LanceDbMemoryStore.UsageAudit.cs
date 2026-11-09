using AgMemory.Contracts;
using lancedb;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    /// <summary>Appends or replays one event using an independent audit lease/connection so optional audit cannot monopolize primary memory operations.</summary>
    public async Task<UsageAuditAppendResult> AppendAsync(UsageAuditEvent usageEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usageEvent);
        usageEvent.Validate();
        ThrowIfDisposed();
        await EnsureAuditInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _auditGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            using var table = await _auditConnection!.OpenTable(UsageAuditEventsTable).ConfigureAwait(false);
            using var batches = await table.Query().Where($"event_id = {Literal(usageEvent.EventId.ToString("D"))}").ToArrow().ConfigureAwait(false);
            var existing = ReadUsageAuditEvents(batches).ToArray();
            if (existing.Length > 1)
                throw new InvalidDataException("Usage audit event id uniqueness was violated.");
            if (existing.Length == 1)
            {
                if (existing[0] == usageEvent) return UsageAuditAppendResult.IdempotencyReplay;
                throw new UsageAuditEventConflictException(usageEvent.EventId);
            }

            using var append = BuildUsageAuditEventBatch([usageEvent]);
            await table.Add(append).ConfigureAwait(false);
            return UsageAuditAppendResult.Appended;
        }
        finally
        {
            _auditGate.Release();
        }
    }

    /// <summary>Deletes expired audit events under the audit-only lease, preserving the exclusive UTC cutoff.</summary>
    public async Task<int> DeleteExpiredAsync(DateTimeOffset cutoffExclusiveUtc, CancellationToken cancellationToken)
    {
        if (cutoffExclusiveUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Usage audit cleanup cutoffs must be UTC.", nameof(cutoffExclusiveUtc));
        ThrowIfDisposed();
        await EnsureAuditInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _auditGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            using var table = await _auditConnection!.OpenTable(UsageAuditEventsTable).ConfigureAwait(false);
            var predicate = $"occurred_at_utc < {Literal(Utc(cutoffExclusiveUtc))}";
            using var expired = await table.Query().Where(predicate).ToArrow().ConfigureAwait(false);
            var count = ReadUsageAuditEvents(expired).Count();
            if (count > 0) await table.Delete(predicate).ConfigureAwait(false);
            return count;
        }
        finally
        {
            _auditGate.Release();
        }
    }

    /// <summary>Initializes schemas under the primary lease once, then creates a dedicated native connection for audit-only operations.</summary>
    private async Task EnsureAuditInitializedAsync(CancellationToken cancellationToken)
    {
        if (_auditConnection is not null) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            if (_auditConnection is not null) return;
            var connection = new Connection();
            try
            {
                await connection.Connect(Path.GetFullPath(_options.StoragePath)).ConfigureAwait(false);
                _auditConnection = connection;
            }
            catch { connection.Dispose(); throw; }
        }
        finally { _gate.Release(); }
    }

}
