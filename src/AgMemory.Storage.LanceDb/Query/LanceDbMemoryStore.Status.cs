using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    private static readonly string[] StatusColumns = ["record_type", "status", "expires_at_utc", "updated_at_utc"];

    /// <summary>
    /// Reads aggregate diagnostics for the exact authorized selectors without loading text, JSON or vectors.
    /// Keeps the existing global gate and initialization checks. Expiry is classified at one UTC instant after
    /// the selected-column scan, as in ListAsync followed by status aggregation; only small metadata is retained.
    /// </summary>
    public async Task<LanceDbMemoryStatus> ReadStatusAsync(AuthorizedScopeSet scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
            using var scan = table.Query().Select(StatusColumns).Where(BuildAuthorizedPredicate(scopes));
            using var reader = await scan.ToBatches(maxBatchLength: 256).ConfigureAwait(false);
            var rows = new List<StatusMetadata>();
            await foreach (var batch in reader.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                using (batch)
                {
                    for (var row = 0; row < batch.Length; row++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var type = ParseEnum<MemoryRecordType>(Required(batch, "record_type", row));
                        var status = ParseEnum<MemoryLifecycleStatus>(Required(batch, "status", row));
                        if (!Enum.IsDefined(type) || !Enum.IsDefined(status))
                            throw new InvalidDataException("The status projection contains invalid lifecycle metadata.");
                        var expires = Value(batch, "expires_at_utc", row);
                        var expiry = string.IsNullOrWhiteSpace(expires) || string.Equals(expires, "null", StringComparison.OrdinalIgnoreCase)
                            ? (DateTimeOffset?)null : ParseUtc(expires);
                        rows.Add(new(type, status, expiry, ParseUtc(Required(batch, "updated_at_utc", row))));
                    }
                }
            }
            return AggregateStatus(rows, DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Counts every retained lifecycle, with expiry at one post-scan instant and ordinal type labels.</summary>
    private static LanceDbMemoryStatus AggregateStatus(IReadOnlyList<StatusMetadata> rows, DateTimeOffset now)
    {
        var active = 0;
        var expired = 0;
        DateTimeOffset? latest = null;
        var counts = new Dictionary<MemoryRecordType, int>();
        foreach (var row in rows)
        {
            if (latest is null || row.UpdatedAt > latest) latest = row.UpdatedAt;
            if (row.Status != MemoryLifecycleStatus.Active) continue;
            if (row.ExpiresAt is { } expiry && expiry <= now)
            {
                expired++;
                continue;
            }
            active++;
            counts[row.Type] = counts.GetValueOrDefault(row.Type) + 1;
        }
        var byType = counts.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .Select(pair => new LanceDbMemoryTypeCount(pair.Key, pair.Value)).ToArray();
        return new(rows.Count, active, expired, rows.Count - active - expired, latest, byType);
    }

    private readonly record struct StatusMetadata(MemoryRecordType Type, MemoryLifecycleStatus Status, DateTimeOffset? ExpiresAt, DateTimeOffset UpdatedAt);
}

/// <summary>Safe store aggregates; contains no text, actor, scope, memory IDs or embedding payloads.</summary>
public sealed record LanceDbMemoryStatus(int TotalMemoryCount, int ActiveMemoryCount, int ExpiredMemoryCount,
    int InactiveMemoryCount, DateTimeOffset? LatestUpdateAt, IReadOnlyList<LanceDbMemoryTypeCount> ActiveByType);

/// <summary>One active type count, emitted in ordinal type-name order.</summary>
public sealed record LanceDbMemoryTypeCount(MemoryRecordType Type, int Count);
