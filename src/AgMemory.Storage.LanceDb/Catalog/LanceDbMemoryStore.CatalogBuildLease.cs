namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    /// <summary>Reads the global source epoch; every cooperating record mutation advances it before writing any source rows.</summary>
    private string ReadSourceEpoch()
    {
        var path = Path.Combine(_options.StoragePath, ".agmemory-source-epoch");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>Advances the source fence before native mutations so physical Offset traversal cannot mix table layouts across slices.</summary>
    private void AdvanceSourceEpoch()
    {
        var path = Path.Combine(_options.StoragePath, ".agmemory-source-epoch");
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, Guid.NewGuid().ToString("N"));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>
    /// Admits queued primary operations between bounded staging batches, restoring ownership even when cancelled.
    /// The source epoch detects any scope's record writes, since a physical table rewrite can change filtered Offset order.
    /// The pointer fence separately detects same-scope metadata edits and invalidation.
    /// </summary>
    private async Task YieldCatalogBuildLeaseAsync(PersistedReaderCatalogGenerationRow generation, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _catalogLeaseSlices);
        _gate.Release();
        await Task.Yield();
        // The public caller's finally owns Release. Restore ownership before propagating cancellation or stale-build exits.
        await _gate.WaitAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var current = await ReadCatalogGenerationByKeyAsync(generation.ScopeKey, cancellationToken).ConfigureAwait(false);
        if (current?.GenerationKey != generation.GenerationKey || ReadSourceEpoch() != generation.SourceEpoch)
        {
            Interlocked.Increment(ref _catalogBuildAborts);
            throw new StaleCatalogBuildException();
        }
    }

    /// <summary>Waits for a same-scope producer without monopolizing the primary lease; always returns with primary ownership restored.</summary>
    private async Task WaitForCatalogBuilderAsync(StorageLeaseGate builder, CancellationToken cancellationToken)
    {
        _gate.Release();
        try
        {
            await builder.WaitAsync(cancellationToken).ConfigureAwait(false);
            builder.Release();
        }
        finally { await _gate.WaitAsync().ConfigureAwait(false); }
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
    }

    private sealed class StaleCatalogBuildException : Exception { }
}
