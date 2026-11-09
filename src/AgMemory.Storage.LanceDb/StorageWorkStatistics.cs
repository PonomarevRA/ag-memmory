using System.Diagnostics.Metrics;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    private static readonly Meter CatalogMeter = new("AgMemory.Storage.LanceDb.Catalog", "1.0");
    private static readonly Counter<long> CompletedInvalidations = CatalogMeter.CreateCounter<long>("agmemory.storage.catalog.invalidated");
    private long _snapshotQueries;
    private long _recordMergeBatches;
    private long _scopeInvalidations;
    private long _catalogSourcePortions;
    private long _catalogMetadataQueries;
    private long _lexicalRowsScanned;
    private long _lexicalRecordsHydrated;
    private long _catalogLeaseSlices;
    private long _catalogBuildAborts;

    /// <summary>Counts physical work initiated by this instance; snapshots contain no content, scope or durable IDs.</summary>
    public StorageWorkStatistics WorkStatistics => new(
        Interlocked.Read(ref _snapshotQueries), Interlocked.Read(ref _recordMergeBatches),
        Interlocked.Read(ref _scopeInvalidations), Interlocked.Read(ref _catalogSourcePortions),
        Interlocked.Read(ref _catalogMetadataQueries), Interlocked.Read(ref _lexicalRowsScanned),
        Interlocked.Read(ref _lexicalRecordsHydrated), Interlocked.Read(ref _catalogLeaseSlices), Interlocked.Read(ref _catalogBuildAborts));
}

/// <summary>Attempted query/batch counts and scanned/hydrated row totals, useful for reproducible acceptance measurements.</summary>
public sealed record StorageWorkStatistics(long SnapshotQueries, long RecordMergeBatches, long ScopeInvalidations,
    long CatalogSourcePortions, long CatalogMetadataQueries, long GenericLexicalRowsScanned, long GenericLexicalRecordsHydrated, long CatalogLeaseSlices, long CatalogBuildAborts);
