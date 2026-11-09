using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    /// <summary>
    /// Reads a bounded graph source after LanceDB has applied exact scope, active lifecycle and expiry filters.
    /// Query-time entity derivation remains in Core; this adapter does not persist graph relationships.
    /// </summary>
    public async Task<IReadOnlyList<MemoryGraphSourceRecord>> ReadAsync(
        MemoryGraphSourceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Eligibility);
        if (request.Limit <= 0) throw new ArgumentOutOfRangeException(nameof(request));

        ThrowIfDisposed();
        var limit = Math.Min(request.Limit, MemoryGraphLimits.MaximumSourceRecords);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var records = await ReadGraphSourceRecordsAsync(
                BuildEligibilityPredicate(request.Eligibility),
                cancellationToken,
                limit).ConfigureAwait(false);
            return records
                .Where(request.IsEligible)
                .OrderByDescending(record => record.Importance)
                .ThenByDescending(record => record.Confidence)
                .ThenBy(record => record.MemoryId.Value, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }
}
