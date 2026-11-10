using AgMemory.Contracts;
using Apache.Arrow;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    private static readonly string[] StatusColumns = ["record_type", "status", "expires_at_utc", "updated_at_utc"];

    /// <summary>
    /// Reads aggregate diagnostics for the exact authorized selectors without loading text, JSON or vectors.
    /// Counts nonexpiring records while scanning. Only active records with explicit expiries are retained
    /// for classification at one UTC instant after the scan.
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
            var status = new StatusAccumulator();
            await foreach (var batch in reader.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                using (batch)
                {
                    var types = (StringArray)batch.Column("record_type");
                    var lifecycles = (StringArray)batch.Column("status");
                    var expiries = (StringArray)batch.Column("expires_at_utc");
                    var updates = (StringArray)batch.Column("updated_at_utc");
                    for (var row = 0; row < batch.Length; row++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var type = ParseEnum<MemoryRecordType>(RequiredStatusValue(types, row, "record_type"));
                        var lifecycle = ParseEnum<MemoryLifecycleStatus>(RequiredStatusValue(lifecycles, row, "status"));
                        if (!Enum.IsDefined(type) || !Enum.IsDefined(lifecycle))
                            throw new InvalidDataException("The status projection contains invalid lifecycle metadata.");
                        var expires = expiries.GetString(row);
                        var expiry = string.IsNullOrWhiteSpace(expires) || string.Equals(expires, "null", StringComparison.OrdinalIgnoreCase)
                            ? (DateTimeOffset?)null : ParseUtc(expires);
                        status.Add(type, lifecycle, expiry, ParseUtc(RequiredStatusValue(updates, row, "updated_at_utc")));
                    }
                }
            }
            return status.Complete(DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    private static string RequiredStatusValue(StringArray column, int row, string name) =>
        column.GetString(row) ?? throw new InvalidDataException($"Required LanceDB column '{name}' is null.");

    /// <summary>Accumulates scoped metadata while deferring expiry checks to the post-scan timestamp.</summary>
    private sealed class StatusAccumulator
    {
        private readonly Dictionary<MemoryRecordType, int> _counts = new();
        private List<(MemoryRecordType Type, DateTimeOffset Expiry)>? _expiring;
        private int _total;
        private int _active;
        private DateTimeOffset? _latest;

        public void Add(MemoryRecordType type, MemoryLifecycleStatus lifecycle, DateTimeOffset? expiry, DateTimeOffset updatedAt)
        {
            _total++;
            if (_latest is null || updatedAt > _latest) _latest = updatedAt;
            if (lifecycle != MemoryLifecycleStatus.Active) return;
            if (expiry is { } expiresAt)
                (_expiring ??= new()).Add((type, expiresAt));
            else
                CountActive(type);
        }

        public LanceDbMemoryStatus Complete(DateTimeOffset now)
        {
            var expired = 0;
            if (_expiring is not null)
            {
                foreach (var (type, expiry) in _expiring)
                {
                    if (expiry <= now) expired++;
                    else CountActive(type);
                }
            }
            var byType = _counts.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
                .Select(pair => new LanceDbMemoryTypeCount(pair.Key, pair.Value)).ToArray();
            return new(_total, _active, expired, _total - _active - expired, _latest, byType);
        }

        private void CountActive(MemoryRecordType type)
        {
            _active++;
            _counts[type] = _counts.GetValueOrDefault(type) + 1;
        }
    }
}

/// <summary>Safe store aggregates; contains no text, actor, scope, memory IDs or embedding payloads.</summary>
public sealed record LanceDbMemoryStatus(int TotalMemoryCount, int ActiveMemoryCount, int ExpiredMemoryCount,
    int InactiveMemoryCount, DateTimeOffset? LatestUpdateAt, IReadOnlyList<LanceDbMemoryTypeCount> ActiveByType);

/// <summary>One active type count, emitted in ordinal type-name order.</summary>
public sealed record LanceDbMemoryTypeCount(MemoryRecordType Type, int Count);
