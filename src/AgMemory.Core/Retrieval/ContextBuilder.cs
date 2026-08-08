using AgMemory.Contracts;

namespace AgMemory.Core;

internal sealed class ContextBuilder(
    SummaryFallbackReader summaryFallbackReader,
    MemoryCoreOptions options)
{
    public async Task<MemoryContext> BuildAsync(
        MemoryContextRequest request,
        SessionHotMemory? hot,
        IReadOnlyList<MemorySearchHit> searchHits,
        CancellationToken cancellationToken)
    {
        var candidates = new List<MemorySearchHit>();
        if (hot is not null && (request.Types is null || request.Types.Contains(MemoryRecordType.Summary)))
            candidates.Add(ToSearchHit(hot, request.RetrievalConfigurationVersion));
        candidates.AddRange(searchHits);

        var distinct = DistinctCandidates(candidates);
        if (distinct.Count == 0 && request.AllowSingleSummaryFallback)
        {
            var fallback = await summaryFallbackReader.ReadAsync(request, cancellationToken).ConfigureAwait(false);
            if (fallback.Error is not null) return Failure(request, fallback.Error);
            if (fallback.Summary is not null)
                distinct = [ToSearchHit(fallback.Summary, request.RetrievalConfigurationVersion)];
        }

        var selected = new List<MemorySearchHit>();
        var cost = 0;
        foreach (var hit in distinct)
        {
            // The current contract bounds the selected records' estimates, not rendering overhead.
            if (hit.Record.EstimatedTokenCost > request.TokenBudget - cost) continue;
            selected.Add(hit);
            cost += hit.Record.EstimatedTokenCost;
        }

        var citations = selected.Select(hit => new MemoryCitation(
            hit.Record.Id,
            hit.Record.Provenance.Evidence.Select(evidence => evidence.Identity)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(identity => identity, StringComparer.Ordinal)
                .ToArray())).ToArray();
        var content = string.Join("\n", selected.Select(hit => $"[{hit.Record.Id.Value}] {hit.Record.CanonicalText}"));
        return new(content, request.RequireCitations ? citations : [], cost, distinct.Count - selected.Count, null,
            options.SupportedContractVersion, request.RetrievalConfigurationVersion, request.ContextConfigurationVersion);
    }

    private static List<MemorySearchHit> DistinctCandidates(IEnumerable<MemorySearchHit> candidates)
    {
        var ids = new HashSet<MemoryId>();
        var deduplicationKeys = new HashSet<string>(StringComparer.Ordinal);
        var canonicalTexts = new HashSet<string>(StringComparer.Ordinal);
        var distinct = new List<MemorySearchHit>();
        foreach (var candidate in candidates)
        {
            var record = candidate.Record;
            var canonicalText = MemoryCommandService.Canonicalize(record.CanonicalText);
            if (!ids.Add(record.Id) || !deduplicationKeys.Add(record.DeduplicationKey) || !canonicalTexts.Add(canonicalText))
                continue;
            distinct.Add(candidate);
        }
        return distinct;
    }

    private MemoryContext Failure(MemoryContextRequest request, MemoryError error) => new(
        string.Empty, [], 0, 0, error, options.SupportedContractVersion,
        request.RetrievalConfigurationVersion, request.ContextConfigurationVersion);

    private MemorySearchHit ToSearchHit(SessionHotMemory hot, ContractVersion retrievalConfigurationVersion)
    {
        var content = HotMemoryStateCodec.TryRender(hot.Content, out var rendered) ? rendered : hot.Content;
        var record = new MemoryRecord(hot.Id, hot.Scope, MemoryRecordType.Summary, MemoryLifecycleStatus.Active,
            content, null, 1d, 1d, MemoryCommandService.EstimateTokenCost(content), hot.CreatedAt,
            hot.UpdatedAt, hot.Version, [], hot.Provenance, null, hot.ExpiresAt, $"hot:{hot.Id.Value}");
        return ToSearchHit(record, retrievalConfigurationVersion);
    }

    private MemorySearchHit ToSearchHit(MemoryRecord record, ContractVersion retrievalConfigurationVersion) =>
        new(record, 0, 0d, new(null, null, 0d, 0d, 0d, 0d), retrievalConfigurationVersion,
            options.RerankerConfigurationVersion);
}
