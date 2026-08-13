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
            using var table = await OpenTableAsync(ReaderWikiDocumentsTable, cancellationToken).ConfigureAwait(false);
            var batches = await table.Query()
                .Where($"generation_key = {Literal(generationKey)}")
                .Limit(MemoryReaderLimits.MaximumTreeNodes)
                .ToArrow()
                .ConfigureAwait(false);
            var rows = ReadReaderWikiDocumentRows(batches)
                .OrderBy(row => row.Namespace, StringComparer.Ordinal)
                .ThenBy(row => row.Title, StringComparer.Ordinal)
                .ThenBy(row => row.MemoryId, StringComparer.Ordinal)
                .ToArray();
            var result = new List<MemoryReaderWikiTreeDocument>(rows.Length);
            foreach (var row in rows)
            {
                if (result.Count == MemoryReaderLimits.MaximumTreeNodes) break;
                if (!long.TryParse(row.RecordVersion, CultureInfo.InvariantCulture, out var version) || version <= 0) continue;
                var memoryId = new MemoryId(row.MemoryId);
                var current = await ReadCurrentWikiTargetCoreAsync(eligibility, memoryId, cancellationToken).ConfigureAwait(false);
                if (current is null || current.Version != version) continue;
                result.Add(new(memoryId, row.Title, row.Namespace, current.Type, version));
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads every explicit wiki child edge for one generation so the UI can nest linked pages.</summary>
    public async Task<IReadOnlyList<MemoryReaderWikiTreeChildEdge>> ReadWikiTreeChildEdgesAsync(
        string generationKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(generationKey) || generationKey.Length > 128) return [];
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            using var links = await OpenTableAsync(ReaderWikiRelationsTable, cancellationToken).ConfigureAwait(false);
            var batches = await links.Query()
                .Where($"generation_key = {Literal(generationKey)} AND kind = {Literal(nameof(MemoryWikiRelationKind.Child))}")
                .Limit(MemoryReaderLimits.MaximumTreeNodes * 2)
                .ToArrow()
                .ConfigureAwait(false);
            return ReadReaderWikiRelationRows(batches)
                .Select(row => int.TryParse(row.SharedEntityCount, CultureInfo.InvariantCulture, out var weight) && weight >= 0
                    ? new MemoryReaderWikiTreeChildEdge(row.SourceMemoryId, row.TargetMemoryId, row.Label, weight)
                    : null)
                .Where(edge => edge is not null)
                .Cast<MemoryReaderWikiTreeChildEdge>()
                .ToArray();
        }
        finally { _gate.Release(); }
    }
}

/// <summary>One eligible wiki leaf used while building a browser-safe namespace tree.</summary>
public sealed record MemoryReaderWikiTreeDocument(
    MemoryId MemoryId,
    string Title,
    string Namespace,
    MemoryRecordType Type,
    long Version);

public sealed record MemoryReaderWikiTreeChildEdge(
    string SourceMemoryId,
    string TargetMemoryId,
    string Label,
    int SharedEntityCount);
