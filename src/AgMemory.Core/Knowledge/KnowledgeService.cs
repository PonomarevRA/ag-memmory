using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Authority-local knowledge workflow. CAS commits domain changes and replay receipts as one bounded state.</summary>
public sealed partial class KnowledgeService(IKnowledgeStore store, string profileKey, Func<DateTimeOffset>? utcNow = null)
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaximumRequests = 4096;
    private const int MaximumEntries = 1024;
    private const int MaximumHistoryEntries = 4096;
    private const int MaximumWarmEntries = 128;
    public Task<KnowledgeState> GetAsync(CancellationToken cancellationToken = default) => store.ReadAsync(profileKey, cancellationToken);

    /// <summary>Applies a retry-safe mutation. Request replay precedes optimistic revision checks; conflicting reuse never mutates state.</summary>
    private async Task<KnowledgeMutationResult> MutateAsync(string operation, string requestKey, object payload,
        Func<KnowledgeState, DateTimeOffset, KnowledgeMutationResult> change, CancellationToken cancellationToken)
    {
        Text(requestKey, 160);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { operation, payload }, Json))));
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var existing = await store.ReadAsync(profileKey, cancellationToken).ConfigureAwait(false);
            var replay = store is IKnowledgeReplayStore ledger
                ? await ledger.ReadRequestAsync(profileKey, requestKey, cancellationToken).ConfigureAwait(false)
                : existing.Requests.GetValueOrDefault(requestKey);
            if (replay is not null)
            {
                if (replay.Fingerprint != fingerprint) throw new InvalidOperationException("request_conflict");
                return new("replayed", replay.ResultKey, replay.ResultRevision);
            }
            if (store is not IKnowledgeReplayStore && existing.Requests.Count >= MaximumRequests)
                throw new InvalidOperationException("knowledge_capacity_exceeded");
            // Isolate the proposed state because in-memory and file adapters may retain the read snapshot.
            var next = JsonSerializer.Deserialize<KnowledgeState>(JsonSerializer.Serialize(existing, Json), Json)!;
            var now = _utcNow();
            var result = change(next, now);
            next.Requests.Add(requestKey, new(requestKey, fingerprint, operation, result.Key, result.Revision, now));
            CheckBounds(next);
            next = next with { Version = existing.Version + 1 };
            if (await store.CompareExchangeAsync(profileKey, existing.Version, next, cancellationToken).ConfigureAwait(false)) return result;
        }
        throw new InvalidOperationException("knowledge_conflict");
    }

    /// <summary>Rejects unbounded documents rather than silently discarding receipts and accepting old retries as new events.</summary>
    private static void CheckBounds(KnowledgeState state)
    {
        if (state.Metadata.Count > MaximumEntries || state.Procedures.Count > MaximumEntries || state.ProcedureHistory.Count > MaximumHistoryEntries ||
            state.Occurrences.Count > MaximumEntries || state.Applications.Count > MaximumEntries || state.Presentations.Count > MaximumHistoryEntries ||
            state.Feedback.Count > MaximumHistoryEntries || state.Warm.Count > MaximumWarmEntries)
            throw new InvalidOperationException("knowledge_capacity_exceeded");
    }

    /// <summary>Validates bounded nonempty text at the domain boundary; transport redaction remains the adapter's responsibility.</summary>
    private static void Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) throw new ArgumentException("invalid_argument");
    }

    private static void Revision(long actual, long expected)
    {
        if (actual != expected) throw new InvalidOperationException("revision_conflict");
    }
    private static string HistoryKey(string key, long revision) => JsonSerializer.Serialize(new object[] { key, revision });
}
