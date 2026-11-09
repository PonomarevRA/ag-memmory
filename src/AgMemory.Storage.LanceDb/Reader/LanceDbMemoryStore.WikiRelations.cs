using System.Globalization;
using Apache.Arrow;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    /// <summary>
    /// Reads metadata and every relation category while the current source version and one ready generation are held
    /// stable by the store gate. A missing snapshot is intentionally indistinguishable from a changed document.
    /// </summary>
    public async Task<(MemoryWikiMetadata? Metadata, IReadOnlyList<MemoryWikiRelationRecord> Children,
        IReadOnlyList<MemoryWikiRelationRecord> Related, IReadOnlyList<MemoryWikiRelationRecord> Backlinks)?>
        ReadWikiDocumentSnapshotAsync(
            MemorySearchEligibility eligibility,
            string routeKey,
            long expectedVersion,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        if (string.IsNullOrWhiteSpace(routeKey) || routeKey.Length > 128 || expectedVersion <= 0 ||
            eligibility.AuthorizedScopes.Selectors.Count != 1) return null;
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var routes = await ReadReaderRoutesAsync($"route_key = {Literal(routeKey)}", cancellationToken).ConfigureAwait(false);
            if (routes.Count != 1) return null;
            var source = new MemoryId(routes[0].MemoryId);
            var current = await ReadCurrentWikiTargetCoreAsync(eligibility, source, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Version != expectedVersion) return null;

            var scope = eligibility.AuthorizedScopes.Selectors[0].Scope;
            var generation = await GetReadyCatalogGenerationCoreAsync(eligibility, cancellationToken).ConfigureAwait(false);
            if (generation is null || !IsReadyCatalogGeneration(generation)) return null;
            var document = await ReadWikiDocumentCoreAsync(generation.GenerationKey, source, cancellationToken).ConfigureAwait(false);
            if (document is null || !long.TryParse(document.RecordVersion, CultureInfo.InvariantCulture, out var documentVersion) ||
                documentVersion != expectedVersion) return null;

            var metadata = await ReadWikiMetadataCoreAsync(scope, source, expectedVersion, cancellationToken).ConfigureAwait(false);
            var children = await ReadWikiRelationsCoreAsync(eligibility, generation.GenerationKey, source, MemoryWikiRelationKind.Child, cancellationToken).ConfigureAwait(false);
            var related = await ReadWikiRelationsCoreAsync(eligibility, generation.GenerationKey, source, MemoryWikiRelationKind.Related, cancellationToken).ConfigureAwait(false);
            var backlinks = await ReadWikiRelationsCoreAsync(eligibility, generation.GenerationKey, source, MemoryWikiRelationKind.Backlink, cancellationToken).ConfigureAwait(false);
            return new(metadata, children, related, backlinks);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads bounded relations only from the exact authorized ready generation and current eligible source.</summary>
    public async Task<IReadOnlyList<MemoryWikiRelationRecord>> ReadWikiRelationsAsync(
        MemorySearchEligibility eligibility,
        string generationKey,
        MemoryId sourceMemoryId,
        MemoryWikiRelationKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        if (string.IsNullOrWhiteSpace(generationKey) || generationKey.Length > 128) return [];
        sourceMemoryId.Validate(nameof(sourceMemoryId));
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            if (!await IsAuthorizedReadyGenerationAsync(eligibility, generationKey, cancellationToken).ConfigureAwait(false)) return [];
            if (await ReadCurrentWikiTargetCoreAsync(eligibility, sourceMemoryId, cancellationToken).ConfigureAwait(false) is null) return [];
            return await ReadWikiRelationsCoreAsync(eligibility, generationKey, sourceMemoryId, kind, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Resolves capped relation targets in batches and omits expired or changed target versions.</summary>
    private async Task<IReadOnlyList<MemoryWikiRelationRecord>> ReadWikiRelationsCoreAsync(
        MemorySearchEligibility eligibility,
        string generationKey,
        MemoryId sourceMemoryId,
        MemoryWikiRelationKind kind,
        CancellationToken cancellationToken)
    {
        if (eligibility.AuthorizedScopes.Selectors.Count != 1) return [];
        using var links = await OpenTableAsync(ReaderWikiRelationsTable, cancellationToken).ConfigureAwait(false);
        using var batches = await links.Query().Where($"generation_key = {Literal(generationKey)} AND source_memory_id = {Literal(sourceMemoryId.Value)} AND kind = {Literal(kind.ToString())}")
            .Limit(kind == MemoryWikiRelationKind.Backlink ? MemoryWikiLimits.MaximumBacklinksPerPage :
                kind == MemoryWikiRelationKind.Child ? MemoryWikiLimits.MaximumChildrenPerDocument : MemoryWikiLimits.MaximumRelatedPerDocument).ToArrow().ConfigureAwait(false);
        var rows = ReadReaderWikiRelationRows(batches)
            .OrderBy(row => row.TargetMemoryId, StringComparer.Ordinal)
            .ThenBy(row => row.Label, StringComparer.Ordinal)
            .ToArray();
        var targetIds = rows.Select(row => row.TargetMemoryId).Distinct(StringComparer.Ordinal).ToArray();
        var sources = await ReadEligibleWikiSourcesAsync(eligibility, targetIds, cancellationToken).ConfigureAwait(false);
        var documents = await ReadWikiDocumentsCoreAsync(generationKey, targetIds, cancellationToken).ConfigureAwait(false);
        var result = new List<MemoryWikiRelationRecord>(rows.Length);
        foreach (var row in rows)
        {
            documents.TryGetValue(row.TargetMemoryId, out var document);
            if (document is null || !Enum.TryParse<MemoryWikiRelationKind>(row.Kind, out var parsed) ||
                !long.TryParse(document.RecordVersion, CultureInfo.InvariantCulture, out var documentVersion)) continue;
            if (!sources.TryGetValue(row.TargetMemoryId, out var current) || current.Version != documentVersion) continue;
            if (!int.TryParse(row.SharedEntityCount, CultureInfo.InvariantCulture, out var sharedEntityCount) || sharedEntityCount < 0) continue;
            result.Add(new(parsed, new(row.TargetMemoryId), row.Label, document.Title, document.Namespace, documentVersion, sharedEntityCount));
        }
        return result;
    }

    private async Task<MemoryReaderSourceRecord?> ReadCurrentWikiTargetCoreAsync(
        MemorySearchEligibility eligibility,
        MemoryId memoryId,
        CancellationToken cancellationToken)
    {
        using var records = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
        using var batches = await records.Query()
            .Select(ReaderSourceColumnNames)
            .Where($"({BuildEligibilityPredicate(eligibility)}) AND id = {Literal(memoryId.Value)}")
            .Limit(2)
            .ToArrow()
            .ConfigureAwait(false);
        var matches = ReadMemoryReaderSourceRecords(batches).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("A reader relation target has duplicate exact-scope records.");
        return matches.SingleOrDefault();
    }

    private async Task<PersistedReaderWikiDocumentRow?> ReadWikiDocumentCoreAsync(string generationKey, MemoryId memoryId, CancellationToken cancellationToken)
    {
        using var documents = await OpenTableAsync(ReaderWikiDocumentsTable, cancellationToken).ConfigureAwait(false);
        using var batches = await documents.Query().Where($"generation_key = {Literal(generationKey)} AND memory_id = {Literal(memoryId.Value)}").Limit(2).ToArrow().ConfigureAwait(false);
        var rows = ReadReaderWikiDocumentRows(batches).ToArray();
        if (rows.Length > 1) throw new InvalidDataException("A wiki generation has duplicate document leaves.");
        return rows.SingleOrDefault();
    }

    /// <summary>Loads one bounded relation target set in a single query instead of reopening the document table per edge.</summary>
    private async Task<IReadOnlyDictionary<string, PersistedReaderWikiDocumentRow>> ReadWikiDocumentsCoreAsync(
        string generationKey, IReadOnlyCollection<string> memoryIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PersistedReaderWikiDocumentRow>(StringComparer.Ordinal);
        if (memoryIds.Count == 0) return result;
        using var documents = await OpenTableAsync(ReaderWikiDocumentsTable, cancellationToken).ConfigureAwait(false);
        using var batches = await documents.Query()
            .Where($"generation_key = {Literal(generationKey)} AND memory_id IN ({string.Join(", ", memoryIds.Select(Literal))})")
            .Limit(memoryIds.Count + 1).ToArrow().ConfigureAwait(false);
        foreach (var row in ReadReaderWikiDocumentRows(batches))
        {
            if (!result.TryAdd(row.MemoryId, row)) throw new InvalidDataException("A wiki generation has duplicate document leaves.");
        }
        return result;
    }

    private static IEnumerable<PersistedReaderWikiDocumentRow> ReadReaderWikiDocumentRows(RecordBatch batch)
    {
        for (var index = 0; index < batch.Length; index++) yield return PersistedReaderWikiDocumentRow.Read(batch, index);
    }

    private static IEnumerable<PersistedReaderWikiRelationRow> ReadReaderWikiRelationRows(RecordBatch batch)
    {
        for (var index = 0; index < batch.Length; index++) yield return PersistedReaderWikiRelationRow.Read(batch, index);
    }
}
