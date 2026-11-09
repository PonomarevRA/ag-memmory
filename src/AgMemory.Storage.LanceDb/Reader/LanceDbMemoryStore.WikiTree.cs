using System.Globalization;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    /// <summary>Reads eligible wiki documents for one immutable generation to build a namespace tree.</summary>
    public async Task<IReadOnlyList<MemoryReaderWikiTreeDocument>> ReadWikiTreeDocumentsAsync(
        MemorySearchEligibility eligibility,
        string generationKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        if (string.IsNullOrWhiteSpace(generationKey) || generationKey.Length > 128 ||
            eligibility.AuthorizedScopes.Selectors.Count != 1) return [];

        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            if (!await IsAuthorizedReadyGenerationAsync(eligibility, generationKey, cancellationToken).ConfigureAwait(false)) return [];
            using var table = await OpenTableAsync(ReaderWikiDocumentsTable, cancellationToken).ConfigureAwait(false);
            using var batches = await table.Query()
                .Where($"generation_key = {Literal(generationKey)}")
                .Limit(MemoryReaderLimits.MaximumTreeNodes)
                .ToArrow()
                .ConfigureAwait(false);
            var rows = ReadReaderWikiDocumentRows(batches)
                .OrderBy(row => row.Namespace, StringComparer.Ordinal)
                .ThenBy(row => row.Title, StringComparer.Ordinal)
                .ThenBy(row => row.MemoryId, StringComparer.Ordinal)
                .ToArray();
            var sources = await ReadEligibleWikiSourcesAsync(eligibility, rows.Select(row => row.MemoryId).ToArray(), cancellationToken).ConfigureAwait(false);
            var result = new List<MemoryReaderWikiTreeDocument>(rows.Length);
            foreach (var row in rows)
            {
                if (result.Count == MemoryReaderLimits.MaximumTreeNodes) break;
                if (!long.TryParse(row.RecordVersion, CultureInfo.InvariantCulture, out var version) || version <= 0) continue;
                if (!sources.TryGetValue(row.MemoryId, out var source) || source.Version != version) continue;
                result.Add(new(new MemoryId(row.MemoryId), row.Title, row.Namespace, version, source.Type));
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads bounded child edges for the authorized ready generation, excluding expired endpoints.</summary>
    public async Task<IReadOnlyList<MemoryReaderWikiTreeChildEdge>> ReadWikiTreeChildEdgesAsync(
        MemorySearchEligibility eligibility,
        string generationKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        if (string.IsNullOrWhiteSpace(generationKey) || generationKey.Length > 128) return [];
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            if (!await IsAuthorizedReadyGenerationAsync(eligibility, generationKey, cancellationToken).ConfigureAwait(false)) return [];
            using var links = await OpenTableAsync(ReaderWikiRelationsTable, cancellationToken).ConfigureAwait(false);
            using var batches = await links.Query()
                .Where($"generation_key = {Literal(generationKey)} AND kind = {Literal(nameof(MemoryWikiRelationKind.Child))}")
                .Limit(MemoryReaderLimits.MaximumTreeNodes * 2)
                .ToArrow()
                .ConfigureAwait(false);
            var rows = ReadReaderWikiRelationRows(batches).ToArray();
            var memoryIds = rows.SelectMany(row => new[] { row.SourceMemoryId, row.TargetMemoryId })
                .Distinct(StringComparer.Ordinal).ToArray();
            var sources = await ReadEligibleWikiSourcesAsync(eligibility, memoryIds, cancellationToken).ConfigureAwait(false);
            var edges = new List<MemoryReaderWikiTreeChildEdge>(rows.Length);
            foreach (var row in rows)
            {
                if (!sources.ContainsKey(row.SourceMemoryId) || !sources.ContainsKey(row.TargetMemoryId)) continue;
                if (!int.TryParse(row.SharedEntityCount, CultureInfo.InvariantCulture, out var weight) || weight < 0) continue;
                edges.Add(new(row.SourceMemoryId, row.TargetMemoryId, row.Label, weight));
            }
            return edges;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>One eligible wiki leaf used while building a browser-safe namespace tree.</summary>
public sealed record MemoryReaderWikiTreeDocument(
    MemoryId MemoryId,
    string Title,
    string Namespace,
    long Version,
    MemoryRecordType Type = MemoryRecordType.Event);

public sealed record MemoryReaderWikiTreeChildEdge(
    string SourceMemoryId,
    string TargetMemoryId,
    string Label,
    int SharedEntityCount);
