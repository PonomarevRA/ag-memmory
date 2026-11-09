using System.Text.Json;
using Apache.Arrow;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    private static readonly string[] DerivedGenerationTables =
    [ReaderCatalogLeavesTable, ReaderWikiDocumentsTable, ReaderWikiRelationsTable, ReaderWikiTreeIndexTable,
        ReaderCatalogFacetsTable, ReaderCatalogBuildRunsTable];

    /// <summary>
    /// Removes retired derived rows after the configured grace interval under the cross-process store lease.
    /// Unknown legacy generations are first observed as retired and receive a complete grace interval.
    /// Current Ready/Building/Invalid pointers, durable records and curated metadata are never deleted.
    /// All cooperating reader operations hold the same lease, preventing deletion during an in-flight read.
    /// </summary>
    public async Task<ReaderCatalogCollectionResult> CollectReaderCatalogGarbageAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return await CollectReaderCatalogGarbageCoreAsync(force: true, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Streams generation identifiers and records retirement times atomically; the lease excludes concurrent pointer publication.</summary>
    private async Task<ReaderCatalogCollectionResult> CollectReaderCatalogGarbageCoreAsync(bool force, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_options.StoragePath, ".agmemory-catalog-retirement.json");
        var state = File.Exists(path)
            ? JsonSerializer.Deserialize<RetirementLedger>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException("The catalog retirement ledger is invalid.")
            : new RetirementLedger(DateTimeOffset.MinValue, new(StringComparer.Ordinal));
        var now = DateTimeOffset.UtcNow;
        if (!force && now - state.LastCollection < _options.ReaderCatalogCollectionInterval)
            return new(0, state.Retired.Count);
        var active = new HashSet<string>(StringComparer.Ordinal);
        var scopePointers = new List<PersistedReaderCatalogGenerationRow>();
        using (var pointers = await OpenTableAsync(ReaderCatalogGenerationsTable, cancellationToken).ConfigureAwait(false))
        {
            using var scan = pointers.Query().Select(ReaderCatalogGenerationColumnNames);
            using var reader = await scan.ToBatches(maxBatchLength: PersistenceBatchRows).ConfigureAwait(false);
            await foreach (var batch in reader.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                using (batch)
                    foreach (var pointer in ReadCatalogGenerationRows(batch)) scopePointers.Add(pointer);
            }
        }
        foreach (var pointer in scopePointers)
        {
            var isAbandoned = false;
            if (pointer.State == "Building" && now - ParseUtc(pointer.CreatedAtUtc) >= _options.ReaderCatalogRetirementGrace)
            {
                var producer = new StorageLeaseGate(_options.StoragePath, $".agmemory-catalog-{Hash(pointer.ScopeKey)}.lock");
                if (producer.TryWait())
                {
                    try
                    {
                        // A live sliced builder retains this scope lease even while primary ownership is yielded.
                        // Only a pointer with no live producer may be retired after the grace interval.
                        var invalid = pointer with { GenerationKey = NewRouteKey(), State = "Invalid", CreatedAtUtc = Utc(now), ReadyAtUtc = null };
                        await PersistCatalogGenerationAsync(invalid, cancellationToken).ConfigureAwait(false);
                        active.Add(invalid.GenerationKey);
                        state.Retired.TryAdd(pointer.GenerationKey, now);
                        isAbandoned = true;
                    }
                    finally { producer.Release(); }
                }
            }
            if (!isAbandoned) active.Add(pointer.GenerationKey);
        }
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in DerivedGenerationTables)
        {
            using var table = await OpenTableAsync(name, cancellationToken).ConfigureAwait(false);
            using var scan = table.Query().Select(["generation_key"]);
            using var reader = await scan.ToBatches(maxBatchLength: PersistenceBatchRows).ConfigureAwait(false);
            await foreach (var batch in reader.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                using (batch)
                    for (var row = 0; row < batch.Length; row++)
                    {
                        var generation = Required(batch, "generation_key", row);
                        if (!active.Contains(generation)) candidates.Add(generation);
                    }
            }
        }
        foreach (var generation in active) state.Retired.Remove(generation);
        foreach (var generation in candidates) state.Retired.TryAdd(generation, now);
        var obsolete = new List<string>();
        foreach (var retirement in state.Retired)
            if (!active.Contains(retirement.Key) && now - retirement.Value >= _options.ReaderCatalogRetirementGrace)
                obsolete.Add(retirement.Key);
        foreach (var portion in obsolete.Chunk(PersistenceBatchRows))
        {
            var predicate = $"generation_key IN ({string.Join(", ", portion.Select(Literal))})";
            foreach (var name in DerivedGenerationTables)
            {
                using var table = await OpenTableAsync(name, cancellationToken).ConfigureAwait(false);
                await table.Delete(predicate).ConfigureAwait(false);
            }
            foreach (var generation in portion) state.Retired.Remove(generation);
        }
        var updated = state with { LastCollection = now };
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated, JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(obsolete.Count, state.Retired.Count);
    }

    private sealed record RetirementLedger(DateTimeOffset LastCollection, Dictionary<string, DateTimeOffset> Retired);
}

/// <summary>Counts retired generations, not durable records or individual derived rows.</summary>
public sealed record ReaderCatalogCollectionResult(int CollectedGenerations, int PendingGenerations);
