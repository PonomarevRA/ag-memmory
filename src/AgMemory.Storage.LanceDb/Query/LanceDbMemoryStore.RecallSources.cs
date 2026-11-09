using System.Globalization;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    /// <summary>
    /// Rechecks up to64 ranked candidates in one current canonical projection, preserving provider order and whole text.
    /// This avoids up to64 full-record reads during budget packing. Scope/type/active/expiry and unchanged text are mandatory.
    /// </summary>
    public async Task<IReadOnlyList<MemoryRecallSource>> ReadCurrentRecallSourcesAsync(MemorySearchEligibility eligibility,
        IReadOnlyList<MemoryLexicalRecallItem> candidates, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(eligibility); ArgumentNullException.ThrowIfNull(candidates);
        if (eligibility.AuthorizedScopes.Selectors.Count != 1 || eligibility.RequiredLifecycleStatus != MemoryLifecycleStatus.Active || candidates.Count > 64)
            throw new ArgumentException("Current recall sources require one exact active scope and at most64 candidates.");
        eligibility.AuthorizedScopes.Selectors[0].Scope.Validate();
        token.ThrowIfCancellationRequested(); ThrowIfDisposed();
        if (candidates.Count == 0) return [];
        var ranked = new Dictionary<string, MemoryLexicalRecallItem>(StringComparer.Ordinal);
        foreach (var candidate in candidates) { candidate.Id.Validate(nameof(candidates)); ranked[candidate.Id.Value] = candidate; }
        var now = DateTimeOffset.UtcNow;
        var currentEligibility = eligibility with { AsOfUtc = eligibility.AsOfUtc > now ? eligibility.AsOfUtc : now };
        var ids = string.Join(",", ranked.Keys.Select(Literal));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(token).ConfigureAwait(false);
            using var table = await OpenTableAsync(RecordsTable, token).ConfigureAwait(false);
            using var scan = table.Query().Select(["id", "record_type", "canonical_text", "version", "updated_at_utc", "expires_at_utc"])
                .Where($"({BuildEligibilityPredicate(currentEligibility)}) AND id IN ({ids})");
            using var reader = await scan.ToBatches(maxBatchLength: 64).ConfigureAwait(false);
            var sources = new Dictionary<string, MemoryRecallSource>(StringComparer.Ordinal);
            await foreach (var batch in reader.WithCancellation(token).ConfigureAwait(false))
            using (batch)
            {
                for (var row = 0; row < batch.Length; row++)
                {
                    token.ThrowIfCancellationRequested();
                    var id = Required(batch, "id", row);
                    var content = Required(batch, "canonical_text", row);
                    if (!ranked.TryGetValue(id, out var candidate) || candidate.Content != content) continue;
                    var type = ParseEnum<MemoryRecordType>(Required(batch, "record_type", row));
                    var revision = long.Parse(Required(batch, "version", row), CultureInfo.InvariantCulture);
                    if (!Enum.IsDefined(type) || revision <= 0) throw new InvalidDataException("Invalid current recall source metadata.");
                    var expires = Value(batch, "expires_at_utc", row);
                    sources[id] = new(candidate.Id, type, content, revision, ParseUtc(Required(batch, "updated_at_utc", row)),
                        expires is null ? null : ParseUtc(expires));
                }
            }
            var ordered = new List<MemoryRecallSource>(sources.Count);
            foreach (var candidate in candidates) if (sources.TryGetValue(candidate.Id.Value, out var source)) ordered.Add(source);
            return ordered;
        }
        finally { _gate.Release(); }
    }
}
