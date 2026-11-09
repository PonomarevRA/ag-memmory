using System.Globalization;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    /// <summary>Counts active, unexpired records using the server-authorized selector without loading their content.</summary>
    public async Task<long> CountEligibleAsync(MemorySearchEligibility eligibility, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
            return await table.CountRows(BuildEligibilityPredicate(eligibility)).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Rejects caller-supplied generations unless they are the current ready pointer for the exact authorized scope.</summary>
    private async Task<bool> IsAuthorizedReadyGenerationAsync(
        MemorySearchEligibility eligibility, string generationKey, CancellationToken cancellationToken)
    {
        if (eligibility.AuthorizedScopes.Selectors.Count != 1) return false;
        var ready = await ReadCatalogGenerationAsync(eligibility.AuthorizedScopes.Selectors[0].Scope, cancellationToken).ConfigureAwait(false);
        if (ready is null || !IsReadyCatalogGeneration(ready) || !string.Equals(ready.GenerationKey, generationKey, StringComparison.Ordinal)) return false;
        var facets = await ReadCompiledFacetsCoreAsync(generationKey, cancellationToken, includeFacetData: false).ConfigureAwait(false);
        return facets is not null && facets.IsValid(eligibility);
    }

    /// <summary>Reads only selected reader columns for a bounded identifier set, rechecking current lifecycle and expiry.</summary>
    private async Task<IReadOnlyDictionary<string, EligibleWikiSource>> ReadEligibleWikiSourcesAsync(
        MemorySearchEligibility eligibility, IReadOnlyCollection<string> memoryIds, CancellationToken cancellationToken,
        bool includePreview = false)
    {
        if (memoryIds.Count == 0) return new Dictionary<string, EligibleWikiSource>(StringComparer.Ordinal);
        using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
        var predicate = $"({BuildEligibilityPredicate(eligibility)}) AND id IN ({string.Join(", ", memoryIds.Select(Literal))})";
        // Tree and relation checks need only identity/version/type; content is selected solely for twenty-leaf previews.
        string[] columns = includePreview ? ["id", "record_type", "version", "canonical_text"] : ["id", "record_type", "version"];
        using var batches = await table.Query().Select(columns).Where(predicate)
            .Limit(memoryIds.Count + 1).ToArrow().ConfigureAwait(false);
        var result = new Dictionary<string, EligibleWikiSource>(StringComparer.Ordinal);
        for (var index = 0; index < batches.Length; index++)
        {
            var id = Value(batches, "id", index) ?? throw new InvalidDataException("The wiki source id is missing.");
            var type = ParseEnum<MemoryRecordType>(Value(batches, "record_type", index)!);
            var version = long.Parse(Value(batches, "version", index)!, CultureInfo.InvariantCulture);
            var preview = includePreview ? Value(batches, "canonical_text", index) ?? string.Empty : string.Empty;
            var record = new EligibleWikiSource(new(id), type, version, preview);
            if (!result.TryAdd(id, record))
                throw new InvalidDataException("The wiki source contains duplicate exact-scope records.");
        }
        return result;
    }

    private sealed record EligibleWikiSource(MemoryId MemoryId, MemoryRecordType Type, long Version, string CanonicalText);
}
