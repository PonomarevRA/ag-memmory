using System.Security.Cryptography;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    /// <summary>
    /// Reads one content-bearing document through an exact-scope, active and non-expired selected projection.
    /// The projection deliberately excludes embeddings, provenance, entities and every general materializer field.
    /// </summary>
    public async Task<MemoryReaderSourceRecord?> ReadByIdAsync(
        MemorySearchEligibility eligibility,
        MemoryId memoryId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        memoryId.Validate(nameof(memoryId));
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var predicate = $"({BuildEligibilityPredicate(eligibility)}) AND id = {Literal(memoryId.Value)}";
            var records = await ReadMemoryReaderSourceRecordsAsync(predicate, cancellationToken).ConfigureAwait(false);
            return records.Count == 1 && records[0].MemoryId == memoryId && IsReaderEligible(records[0], eligibility)
                ? records[0]
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Creates an opaque route only after the Core reader has passed exact-scope eligibility.</summary>
    public async Task<MemoryReaderRoute> GetOrCreateRouteAsync(MemoryId memoryId, CancellationToken cancellationToken)
    {
        memoryId.Validate(nameof(memoryId));
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadReaderRoutesAsync($"memory_id = {Literal(memoryId.Value)}", cancellationToken).ConfigureAwait(false);
            if (existing.Count == 1) return new(existing[0].RouteKey, memoryId);
            if (existing.Count > 1) throw new InvalidDataException("The reader route map has duplicate memory identifiers.");

            var created = new PersistedReaderRouteRow(NewRouteKey(), memoryId.Value);
            using (var table = await OpenTableAsync(ReaderRoutesTable, cancellationToken).ConfigureAwait(false))
            {
                await table.MergeInsert("memory_id")
                    .WhenMatchedUpdateAll()
                    .WhenNotMatchedInsertAll()
                    .Execute(BuildReaderRouteBatch([created]))
                    .ConfigureAwait(false);
            }

            var resolved = await ReadReaderRoutesAsync($"memory_id = {Literal(memoryId.Value)}", cancellationToken).ConfigureAwait(false);
            if (resolved.Count != 1) throw new InvalidDataException("The reader route map did not produce one route.");
            return new(resolved[0].RouteKey, memoryId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Resolves at most one local route. A route map entry is never treated as authorization.</summary>
    public async Task<MemoryId?> ResolveRouteAsync(string routeKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(routeKey) || routeKey.Length > 128) return null;
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var matches = await ReadReaderRoutesAsync($"route_key = {Literal(routeKey)}", cancellationToken).ConfigureAwait(false);
            return matches.Count == 1 ? new MemoryId(matches[0].MemoryId) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<MemoryReaderSourceRecord>> ReadMemoryReaderSourceRecordsAsync(
        string predicate,
        CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query()
            .Select(ReaderSourceColumnNames)
            .Where(predicate)
            .Limit(2)
            .ToArrow()
            .ConfigureAwait(false);
        return ReadMemoryReaderSourceRecords(batches).ToArray();
    }

    private async Task<IReadOnlyList<PersistedReaderRouteRow>> ReadReaderRoutesAsync(
        string predicate,
        CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(ReaderRoutesTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query().Where(predicate).Limit(2).ToArrow().ConfigureAwait(false);
        return ReadReaderRouteRows(batches).ToArray();
    }

    private static bool IsReaderEligible(MemoryReaderSourceRecord record, MemorySearchEligibility eligibility) =>
        eligibility.AuthorizedScopes.Contains(record.Scope) &&
        record.Status == MemoryLifecycleStatus.Active &&
        (record.ExpiresAt is null || record.ExpiresAt > eligibility.AsOfUtc);

    private static string NewRouteKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
