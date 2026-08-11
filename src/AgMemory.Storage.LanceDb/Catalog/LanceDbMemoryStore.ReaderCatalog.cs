using System.Globalization;
using Apache.Arrow;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    public async Task<MemoryReaderCatalogBuildPortion> ReadBuildPortionAsync(
        MemoryReaderCatalogBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Eligibility);
        if (request.Cursor is { NextSourceOffset: < 0 }) throw new ArgumentOutOfRangeException(nameof(request));

        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            return await ReadCatalogBuildPortionCoreAsync(request.Eligibility, request.Cursor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MemoryReaderCatalogLeafPage?> ReadReadyLeafPageAsync(
        MemorySearchEligibility eligibility,
        MemoryReaderCatalogCursor? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        if (cursor is { NextLeafPosition: < 0 } || cursor is { GenerationKey.Length: > 128 }) return null;
        if (eligibility.AuthorizedScopes.Selectors.Count != 1) return null;

        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var scope = eligibility.AuthorizedScopes.Selectors[0].Scope;
            var ready = await ReadCatalogGenerationAsync(scope, cancellationToken).ConfigureAwait(false);
            if (ready is null || !string.Equals(ready.State, "Ready", StringComparison.Ordinal))
                ready = await BuildReadyCatalogGenerationAsync(scope, eligibility, cancellationToken).ConfigureAwait(false);
            if (ready is null || !string.Equals(ready.State, "Ready", StringComparison.Ordinal)) return null;
            if (cursor is not null && !string.Equals(cursor.GenerationKey, ready.GenerationKey, StringComparison.Ordinal)) return null;

            var start = cursor?.NextLeafPosition ?? 0;
            var leaves = await ReadCatalogLeavesAsync(ready.GenerationKey, start, cancellationToken).ConfigureAwait(false);
            var hasMore = leaves.Count > MemoryReaderLimits.DocumentsPerPage;
            var entries = leaves.Take(MemoryReaderLimits.DocumentsPerPage).Select(leaf => new MemoryReaderCatalogLeafEntry(
                    int.Parse(leaf.LeafPosition, CultureInfo.InvariantCulture),
                    new MemoryId(leaf.MemoryId),
                    ParseEnum<MemoryRecordType>(leaf.RecordType),
                    string.Empty,
                    ParseUtc(leaf.UpdatedAtUtc),
                    long.Parse(leaf.Version, CultureInfo.InvariantCulture)))
                .ToArray();
            var next = hasMore
                ? new MemoryReaderCatalogCursor(ready.GenerationKey, entries[^1].Position + 1)
                : null;
            return new(ready.GenerationKey, entries, next);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MemoryReaderCatalogBuildPortion> ReadCatalogBuildPortionCoreAsync(
        MemorySearchEligibility eligibility,
        MemoryReaderCatalogBuildCursor? cursor,
        CancellationToken cancellationToken)
    {
        var offset = cursor?.NextSourceOffset ?? 0;
        if (offset > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(cursor));
        using var table = await OpenTableAsync(RecordsTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query()
            .Select(ReaderSourceColumnNames)
            .Where(BuildEligibilityPredicate(eligibility))
            .Limit(MemoryReaderLimits.DocumentsPerPage)
            .Offset((int)offset)
            .WithRowId()
            .ToArrow()
            .ConfigureAwait(false);
        var records = ReadMemoryReaderSourceRecords(batches)
            .Where(record => IsCatalogEligible(record, eligibility))
            .Select(record => new MemoryReaderCatalogSourceRecord(
                record.MemoryId,
                record.Scope,
                record.Type,
                record.Status,
                record.CanonicalText,
                record.CreatedAt,
                record.UpdatedAt,
                record.Version,
                record.ExpiresAt))
            .GroupBy(record => record.MemoryId)
            .Select(group => group.First())
            .OrderBy(record => record.CreatedAt)
            .ThenBy(record => record.MemoryId.Value, StringComparer.Ordinal)
            .ToArray();
        return new(records, records.Length == MemoryReaderLimits.DocumentsPerPage
            ? new(offset + records.Length)
            : null);
    }

    private async Task<PersistedReaderCatalogGenerationRow?> BuildReadyCatalogGenerationAsync(
        MemoryScope scope,
        MemorySearchEligibility eligibility,
        CancellationToken cancellationToken)
    {
        var previous = await ReadCatalogGenerationAsync(scope, cancellationToken).ConfigureAwait(false);
        if (previous is not null && !string.Equals(previous.State, "Ready", StringComparison.Ordinal))
            await DeleteCatalogBuildRunRowsAsync(previous.GenerationKey, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var generation = new PersistedReaderCatalogGenerationRow(
            ScopeKey(scope), NewRouteKey(), "Building", Utc(now), null);
        await PersistCatalogGenerationAsync(generation, cancellationToken).ConfigureAwait(false);

        var runs = new List<CatalogBuildRun?>();
        MemoryReaderCatalogBuildCursor? cursor = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var portion = await ReadCatalogBuildPortionCoreAsync(eligibility, cursor, cancellationToken).ConfigureAwait(false);
            if (portion.Records.Count > 0)
                await AddCatalogBuildRunAsync(generation, portion.Records, runs, cancellationToken).ConfigureAwait(false);

            if (portion.NextCursor is null) break;
            cursor = portion.NextCursor;
        }

        CatalogBuildRun? merged = null;
        foreach (var run in runs)
        {
            if (run is null) continue;
            merged = merged is null
                ? run
                : await MergeCatalogBuildRunsAsync(generation, merged, run, cancellationToken).ConfigureAwait(false);
        }
        if (merged is not null)
            await PersistCatalogLeavesFromRunAsync(generation, merged, cancellationToken).ConfigureAwait(false);

        var ready = generation with { State = "Ready", ReadyAtUtc = Utc(DateTimeOffset.UtcNow) };
        await PersistCatalogGenerationAsync(ready, cancellationToken).ConfigureAwait(false);
        // Ready leaves are self-contained; cleanup only after their generation pointer is public.
        await DeleteCatalogBuildRunRowsAsync(generation.GenerationKey, CancellationToken.None).ConfigureAwait(false);
        return ready;
    }

    private async Task AddCatalogBuildRunAsync(
        PersistedReaderCatalogGenerationRow generation,
        IReadOnlyList<MemoryReaderCatalogSourceRecord> records,
        List<CatalogBuildRun?> runs,
        CancellationToken cancellationToken)
    {
        var run = await PersistCatalogSourceRunAsync(generation, records, cancellationToken).ConfigureAwait(false);
        for (var level = 0; ; level++)
        {
            if (level == runs.Count)
            {
                runs.Add(run);
                return;
            }

            if (runs[level] is null)
            {
                runs[level] = run;
                return;
            }

            run = await MergeCatalogBuildRunsAsync(generation, runs[level]!, run, cancellationToken).ConfigureAwait(false);
            runs[level] = null;
        }
    }

    private async Task<CatalogBuildRun> PersistCatalogSourceRunAsync(
        PersistedReaderCatalogGenerationRow generation,
        IReadOnlyList<MemoryReaderCatalogSourceRecord> records,
        CancellationToken cancellationToken)
    {
        var runKey = NewRouteKey();
        var rows = records.Select((record, index) => new PersistedReaderCatalogBuildRunRow(
            $"{generation.GenerationKey}:{runKey}:{index:D12}",
            generation.GenerationKey,
            runKey,
            CatalogPosition(index),
            CatalogSortKey(record.CreatedAt, record.MemoryId),
            record.MemoryId.Value,
            record.Type.ToString(),
            Utc(record.UpdatedAt),
            record.Version.ToString(CultureInfo.InvariantCulture))).ToArray();
        await PersistCatalogBuildRunRowsAsync(rows, cancellationToken).ConfigureAwait(false);
        return new(generation.GenerationKey, runKey, rows.Length);
    }

    private async Task<CatalogBuildRun> MergeCatalogBuildRunsAsync(
        PersistedReaderCatalogGenerationRow generation,
        CatalogBuildRun left,
        CatalogBuildRun right,
        CancellationToken cancellationToken)
    {
        var runKey = NewRouteKey();
        var leftReader = new CatalogBuildRunReader(this, left);
        var rightReader = new CatalogBuildRunReader(this, right);
        var leftRow = await leftReader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        var rightRow = await rightReader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        var output = new List<PersistedReaderCatalogBuildRunRow>(MemoryReaderLimits.DocumentsPerPage);
        var position = 0;

        while (leftRow is not null || rightRow is not null)
        {
            var takeLeft = rightRow is null || (leftRow is not null && CompareCatalogBuildRows(leftRow, rightRow) <= 0);
            var source = takeLeft ? leftRow! : rightRow!;
            output.Add(source with
            {
                RowKey = $"{generation.GenerationKey}:{runKey}:{position:D12}",
                GenerationKey = generation.GenerationKey,
                RunKey = runKey,
                RowPosition = CatalogPosition(position)
            });
            position++;

            if (output.Count == MemoryReaderLimits.DocumentsPerPage)
            {
                await PersistCatalogBuildRunRowsAsync(output, cancellationToken).ConfigureAwait(false);
                output.Clear();
            }

            if (takeLeft)
                leftRow = await leftReader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            else
                rightRow = await rightReader.ReadNextAsync(cancellationToken).ConfigureAwait(false);
        }

        await PersistCatalogBuildRunRowsAsync(output, cancellationToken).ConfigureAwait(false);
        if (position != left.Count + right.Count)
            throw new InvalidDataException("The reader catalog merge did not preserve every staged row.");
        return new(generation.GenerationKey, runKey, position);
    }

    private async Task PersistCatalogLeavesFromRunAsync(
        PersistedReaderCatalogGenerationRow generation,
        CatalogBuildRun run,
        CancellationToken cancellationToken)
    {
        var reader = new CatalogBuildRunReader(this, run);
        var leaves = new List<PersistedReaderCatalogLeafRow>(MemoryReaderLimits.DocumentsPerPage);
        var position = 0;
        while (await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false) is { } row)
        {
            leaves.Add(new(
                $"{generation.GenerationKey}:{position:D12}",
                generation.ScopeKey,
                generation.GenerationKey,
                CatalogPosition(position),
                row.MemoryId,
                row.RecordType,
                row.UpdatedAtUtc,
                row.Version));
            position++;
            if (leaves.Count == MemoryReaderLimits.DocumentsPerPage)
            {
                await PersistCatalogLeavesAsync(leaves, cancellationToken).ConfigureAwait(false);
                leaves.Clear();
            }
        }

        await PersistCatalogLeavesAsync(leaves, cancellationToken).ConfigureAwait(false);
        if (position != run.Count)
            throw new InvalidDataException("The reader catalog leaves did not preserve every merged row.");
    }

    private async Task<PersistedReaderCatalogGenerationRow?> ReadCatalogGenerationAsync(
        MemoryScope scope,
        CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(ReaderCatalogGenerationsTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query()
            .Select(ReaderCatalogGenerationColumnNames)
            .Where($"scope_key = {Literal(ScopeKey(scope))}")
            .Limit(2)
            .ToArrow()
            .ConfigureAwait(false);
        var matches = ReadCatalogGenerationRows(batches).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("The reader catalog has duplicate scope generations.");
        return matches.SingleOrDefault();
    }

    private async Task<IReadOnlyList<PersistedReaderCatalogLeafRow>> ReadCatalogLeavesAsync(
        string generationKey,
        int start,
        CancellationToken cancellationToken)
    {
        if (start < 0 || start > int.MaxValue - MemoryReaderLimits.DocumentsPerPage)
            throw new ArgumentOutOfRangeException(nameof(start));

        // Fixed positions keep physical LanceDB arrival order from becoming public catalog order.
        var positions = Enumerable.Range(start, MemoryReaderLimits.DocumentsPerPage + 1).Select(CatalogPosition).ToArray();
        using var table = await OpenTableAsync(ReaderCatalogLeavesTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query()
            .Select(ReaderCatalogLeafColumnNames)
            .Where($"generation_key = {Literal(generationKey)} AND leaf_position IN ({string.Join(", ", positions.Select(Literal))})")
            .Limit(MemoryReaderLimits.DocumentsPerPage + 1)
            .WithRowId()
            .ToArrow()
            .ConfigureAwait(false);
        var leaves = ReadCatalogLeafRows(batches)
            .OrderBy(leaf => leaf.LeafPosition, StringComparer.Ordinal)
            .ToArray();
        if (!positions.Take(leaves.Length).SequenceEqual(leaves.Select(leaf => leaf.LeafPosition), StringComparer.Ordinal))
            throw new InvalidDataException("The reader catalog leaf sequence is incomplete or inconsistent.");
        return leaves;
    }

    private async Task PersistCatalogGenerationAsync(PersistedReaderCatalogGenerationRow generation, CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(ReaderCatalogGenerationsTable, cancellationToken).ConfigureAwait(false);
        await table.MergeInsert("scope_key")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(BuildReaderCatalogGenerationBatch([generation]))
            .ConfigureAwait(false);
    }

    private async Task PersistCatalogLeavesAsync(IReadOnlyCollection<PersistedReaderCatalogLeafRow> leaves, CancellationToken cancellationToken)
    {
        if (leaves.Count == 0) return;
        using var table = await OpenTableAsync(ReaderCatalogLeavesTable, cancellationToken).ConfigureAwait(false);
        await table.MergeInsert("leaf_key")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(BuildReaderCatalogLeafBatch(leaves))
            .ConfigureAwait(false);
    }

    private async Task PersistCatalogBuildRunRowsAsync(
        IReadOnlyCollection<PersistedReaderCatalogBuildRunRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        using var table = await OpenTableAsync(ReaderCatalogBuildRunsTable, cancellationToken).ConfigureAwait(false);
        await table.MergeInsert("row_key")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(BuildReaderCatalogBuildRunBatch(rows))
            .ConfigureAwait(false);
    }

    private async Task DeleteCatalogBuildRunRowsAsync(string generationKey, CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(ReaderCatalogBuildRunsTable, cancellationToken).ConfigureAwait(false);
        await table.Delete($"generation_key = {Literal(generationKey)}").ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PersistedReaderCatalogBuildRunRow>> ReadCatalogBuildRunRowsAsync(
        CatalogBuildRun run,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        if (start < 0 || count is < 1 or > MemoryReaderLimits.DocumentsPerPage || start > run.Count - count)
            throw new ArgumentOutOfRangeException(nameof(start));

        // A run is read by its persisted positions, never by an implicit table scan order.
        var positions = Enumerable.Range(start, count).Select(CatalogPosition).ToArray();
        var predicate = $"generation_key = {Literal(run.GenerationKey)} AND run_key = {Literal(run.RunKey)} AND row_position IN ({string.Join(", ", positions.Select(Literal))})";
        using var table = await OpenTableAsync(ReaderCatalogBuildRunsTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query()
            .Select(ReaderCatalogBuildRunColumnNames)
            .Where(predicate)
            .Limit(count)
            .WithRowId()
            .ToArrow()
            .ConfigureAwait(false);
        var rows = ReadCatalogBuildRunRows(batches).OrderBy(row => row.RowPosition, StringComparer.Ordinal).ToArray();
        if (rows.Length != count || !positions.SequenceEqual(rows.Select(row => row.RowPosition), StringComparer.Ordinal))
            throw new InvalidDataException("The reader catalog staged run is incomplete or inconsistent.");
        return rows;
    }

    private async Task InvalidateReaderCatalogAsync(MemoryScope scope, CancellationToken cancellationToken)
    {
        var previous = await ReadCatalogGenerationAsync(scope, cancellationToken).ConfigureAwait(false);
        var invalid = new PersistedReaderCatalogGenerationRow(
            ScopeKey(scope), NewRouteKey(), "Invalid", Utc(DateTimeOffset.UtcNow), null);
        await PersistCatalogGenerationAsync(invalid, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
            await DeleteCatalogBuildRunRowsAsync(previous.GenerationKey, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<PersistedReaderCatalogGenerationRow> ReadCatalogGenerationRows(RecordBatch batch)
    {
        for (var index = 0; index < batch.Length; index++) yield return PersistedReaderCatalogGenerationRow.Read(batch, index);
    }

    private static IEnumerable<PersistedReaderCatalogLeafRow> ReadCatalogLeafRows(RecordBatch batch)
    {
        for (var index = 0; index < batch.Length; index++) yield return PersistedReaderCatalogLeafRow.Read(batch, index);
    }

    private static IEnumerable<PersistedReaderCatalogBuildRunRow> ReadCatalogBuildRunRows(RecordBatch batch)
    {
        for (var index = 0; index < batch.Length; index++) yield return PersistedReaderCatalogBuildRunRow.Read(batch, index);
    }

    private static string CatalogPosition(int position) => position.ToString("D12", CultureInfo.InvariantCulture);

    private static string CatalogSortKey(DateTimeOffset createdAt, MemoryId memoryId) =>
        string.Concat(Utc(createdAt), "\u001f", memoryId.Value);

    private static int CompareCatalogBuildRows(PersistedReaderCatalogBuildRunRow left, PersistedReaderCatalogBuildRunRow right)
    {
        var sort = string.Compare(left.SortKey, right.SortKey, StringComparison.Ordinal);
        return sort != 0 ? sort : string.Compare(left.MemoryId, right.MemoryId, StringComparison.Ordinal);
    }

    private sealed record CatalogBuildRun(string GenerationKey, string RunKey, int Count);

    private sealed class CatalogBuildRunReader
    {
        private readonly LanceDbMemoryStore _store;
        private readonly CatalogBuildRun _run;
        private IReadOnlyList<PersistedReaderCatalogBuildRunRow> _rows = [];
        private int _nextPosition;
        private int _index;

        public CatalogBuildRunReader(LanceDbMemoryStore store, CatalogBuildRun run)
        {
            _store = store;
            _run = run;
        }

        public async Task<PersistedReaderCatalogBuildRunRow?> ReadNextAsync(CancellationToken cancellationToken)
        {
            if (_index == _rows.Count)
            {
                if (_nextPosition == _run.Count) return null;
                var count = Math.Min(MemoryReaderLimits.DocumentsPerPage, _run.Count - _nextPosition);
                _rows = await _store.ReadCatalogBuildRunRowsAsync(_run, _nextPosition, count, cancellationToken).ConfigureAwait(false);
                _nextPosition += count;
                _index = 0;
            }

            return _rows[_index++];
        }
    }

    private static bool IsCatalogEligible(MemoryReaderSourceRecord record, MemorySearchEligibility eligibility) =>
        eligibility.AuthorizedScopes.Contains(record.Scope) &&
        record.Status == MemoryLifecycleStatus.Active &&
        (record.ExpiresAt is null || record.ExpiresAt > eligibility.AsOfUtc);
}
