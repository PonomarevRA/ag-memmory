using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Apache.Arrow;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    // Changing this state invalidates only derived reader projections, never durable memory records.
    private const string ReadyCatalogGenerationState = "Ready:shared-entity-related-v1";

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
            if (ready is null || !IsReadyCatalogGeneration(ready))
                ready = await BuildReadyCatalogGenerationAsync(scope, eligibility, cancellationToken).ConfigureAwait(false);
            if (ready is null || !IsReadyCatalogGeneration(ready)) return null;
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
                record.ExpiresAt,
                record.Entities))
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
        if (previous is not null && !IsReadyCatalogGeneration(previous))
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
        await BuildWikiLeavesAsync(scope, generation, eligibility, cancellationToken).ConfigureAwait(false);

        var ready = generation with { State = ReadyCatalogGenerationState, ReadyAtUtc = Utc(DateTimeOffset.UtcNow) };
        await PersistCatalogGenerationAsync(ready, cancellationToken).ConfigureAwait(false);
        // Ready leaves are self-contained; cleanup only after their generation pointer is public.
        await DeleteCatalogBuildRunRowsAsync(generation.GenerationKey, CancellationToken.None).ConfigureAwait(false);
        return ready;
    }

    private static bool IsReadyCatalogGeneration(PersistedReaderCatalogGenerationRow generation) =>
        string.Equals(generation.State, ReadyCatalogGenerationState, StringComparison.Ordinal);

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

    private static readonly Regex WikiDocumentLink = new(@"\[\[(?<kind>doc|related):(?<path>[a-z0-9-]+(?:/[a-z0-9-]+)*)\|(?<label>[^\]\r\n]{1,160})\]\]", RegexOptions.CultureInvariant);

    private async Task BuildWikiLeavesAsync(MemoryScope scope, PersistedReaderCatalogGenerationRow generation,
        MemorySearchEligibility eligibility, CancellationToken cancellationToken)
    {
        MemoryReaderCatalogBuildCursor? cursor = null;
        var entitiesByMemoryId = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var documentsByMemoryId = new Dictionary<string, PersistedReaderWikiDocumentRow>(StringComparer.Ordinal);
        while (true)
        {
            var portion = await ReadCatalogBuildPortionCoreAsync(eligibility, cursor, cancellationToken).ConfigureAwait(false);
            var documents = new List<PersistedReaderWikiDocumentRow>(portion.Records.Count);
            foreach (var record in portion.Records)
            {
                entitiesByMemoryId[record.MemoryId.Value] = NormalizeEntities(record.Entities);
                var metadata = await ReadWikiMetadataCoreAsync(scope, record.MemoryId, record.Version, cancellationToken).ConfigureAwait(false);
                var document = new PersistedReaderWikiDocumentRow($"{generation.GenerationKey}:{record.MemoryId.Value}", generation.GenerationKey, record.MemoryId.Value,
                    record.Version.ToString(CultureInfo.InvariantCulture), metadata?.Title ?? WikiFallbackTitle(record.CanonicalText),
                    metadata?.Namespace ?? "inbox", metadata?.Slug);
                documents.Add(document);
                documentsByMemoryId.Add(record.MemoryId.Value, document);
            }
            await PersistWikiDocumentsAsync(documents, cancellationToken).ConfigureAwait(false);
            if (portion.NextCursor is null) break;
            cursor = portion.NextCursor;
        }

        var relations = new WikiRelationAccumulator(generation.GenerationKey);
        // Populate the bounded automatic candidates first. Curated markup is then able to replace an automatic
        // candidate when necessary, while never displacing another curated relation.
        AddSharedEntityRelations(relations, entitiesByMemoryId, documentsByMemoryId);
        cursor = null;
        while (true)
        {
            var portion = await ReadCatalogBuildPortionCoreAsync(eligibility, cursor, cancellationToken).ConfigureAwait(false);
            foreach (var record in portion.Records)
            {
                foreach (Match link in WikiDocumentLink.Matches(record.CanonicalText))
                {
                    var path = link.Groups["path"].Value;
                    var separator = path.LastIndexOf('/');
                    if (separator <= 0 || separator == path.Length - 1) continue;
                    var target = await ReadWikiDocumentBySlugCoreAsync(generation.GenerationKey, path[..separator], path[(separator + 1)..], cancellationToken).ConfigureAwait(false);
                    if (target is null || target.MemoryId == record.MemoryId.Value) continue;
                    var label = link.Groups["label"].Value;
                    var sharedEntityCount = SharedEntityCount(record.Entities, entitiesByMemoryId.GetValueOrDefault(target.MemoryId));
                    if (link.Groups["kind"].Value == "doc")
                    {
                        relations.Add(record.MemoryId.Value, target.MemoryId, MemoryWikiRelationKind.Child, label, sharedEntityCount, explicitRelation: true);
                        relations.Add(target.MemoryId, record.MemoryId.Value, MemoryWikiRelationKind.Backlink, label, sharedEntityCount, explicitRelation: true);
                    }
                    else
                    {
                        relations.Add(record.MemoryId.Value, target.MemoryId, MemoryWikiRelationKind.Related, label, sharedEntityCount, explicitRelation: true);
                        relations.Add(target.MemoryId, record.MemoryId.Value, MemoryWikiRelationKind.Related, label, sharedEntityCount, explicitRelation: true);
                    }
                }
            }
            if (portion.NextCursor is null) break;
            cursor = portion.NextCursor;
        }

        foreach (var batch in relations.Rows().Chunk(MemoryReaderLimits.DocumentsPerPage))
            await PersistWikiRelationsAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects the existing graph's shared normalized entities into reader-safe related links. Only a fixed number
    /// of sorted candidates is inspected per entity, so one common entity cannot turn catalog compilation quadratic.
    /// </summary>
    private static void AddSharedEntityRelations(
        WikiRelationAccumulator relations,
        IReadOnlyDictionary<string, IReadOnlySet<string>> entitiesByMemoryId,
        IReadOnlyDictionary<string, PersistedReaderWikiDocumentRow> documentsByMemoryId)
    {
        var sourcesByEntity = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (memoryId, entities) in entitiesByMemoryId)
        {
            foreach (var entity in entities)
            {
                if (!sourcesByEntity.TryGetValue(entity, out var memoryIds))
                {
                    memoryIds = new(StringComparer.Ordinal);
                    sourcesByEntity.Add(entity, memoryIds);
                }
                memoryIds.Add(memoryId);
            }
        }

        foreach (var (source, sourceEntities) in entitiesByMemoryId.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var candidates = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var entity in sourceEntities.OrderBy(value => value, StringComparer.Ordinal))
            {
                if (!sourcesByEntity.TryGetValue(entity, out var targets)) continue;
                foreach (var target in targets.Where(target => !string.Equals(target, source, StringComparison.Ordinal))
                             .Take(MemoryWikiLimits.MaximumRelatedPerDocument))
                    candidates.Add(target);
            }

            foreach (var target in candidates.Take(MemoryWikiLimits.MaximumRelatedPerDocument))
            {
                var sharedEntityCount = SharedEntityCount(sourceEntities, entitiesByMemoryId[target]);
                if (sharedEntityCount <= 0 || !documentsByMemoryId.TryGetValue(target, out var document)) continue;
                relations.Add(source, target, MemoryWikiRelationKind.Related, document.Title, sharedEntityCount);
            }
        }
    }

    private static string WikiFallbackTitle(string text)
    {
        var title = text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim();
        title ??= text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (title.Length == 0) title = "Запись без текста";
        return title.Length <= MemoryWikiLimits.MaximumTitleCharacters ? title : string.Concat(title.AsSpan(0, MemoryWikiLimits.MaximumTitleCharacters - 1), "…");
    }

    private static IReadOnlySet<string> NormalizeEntities(IReadOnlyList<string> entities) => entities
        .Select(entity => entity.Trim().ToLowerInvariant())
        .Where(entity => entity.Length is > 0 and <= MemoryReaderLimits.MaximumCatalogTagEntityCharacters)
        .ToHashSet(StringComparer.Ordinal);

    private static int SharedEntityCount(IReadOnlyList<string> source, IReadOnlySet<string>? target)
    {
        if (target is null || target.Count == 0) return 0;
        return NormalizeEntities(source).Count(target.Contains);
    }

    private static int SharedEntityCount(IReadOnlySet<string> source, IReadOnlySet<string>? target) =>
        target is null || target.Count == 0 ? 0 : source.Count(target.Contains);

    private static PersistedReaderWikiRelationRow WikiRelation(string generation, string source, string target, MemoryWikiRelationKind kind, string label, int sharedEntityCount) =>
        new(Hash($"{generation}\u001f{source}\u001f{target}\u001f{kind}"), generation, source, target, kind.ToString(), label,
            sharedEntityCount.ToString(CultureInfo.InvariantCulture));

    /// <summary>Keeps relation generation bounded and stable even when a target is encountered in later source portions.</summary>
    private sealed class WikiRelationAccumulator(string generation)
    {
        private readonly Dictionary<string, RelationBucket> _children = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RelationBucket> _related = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RelationBucket> _backlinks = new(StringComparer.Ordinal);

        public void Add(string source, string target, MemoryWikiRelationKind kind, string label, int sharedEntityCount = 0,
            bool explicitRelation = false)
        {
            var buckets = kind switch
            {
                MemoryWikiRelationKind.Child => _children,
                MemoryWikiRelationKind.Related => _related,
                _ => _backlinks
            };
            var cap = kind == MemoryWikiRelationKind.Backlink
                ? MemoryWikiLimits.MaximumBacklinksPerPage
                : kind == MemoryWikiRelationKind.Child
                    ? MemoryWikiLimits.MaximumChildrenPerDocument
                    : MemoryWikiLimits.MaximumRelatedPerDocument;
            if (!buckets.TryGetValue(source, out var bucket))
            {
                bucket = new(cap);
                buckets.Add(source, bucket);
            }
            bucket.Add(target, kind, label, sharedEntityCount, explicitRelation);
        }

        public IEnumerable<PersistedReaderWikiRelationRow> Rows()
        {
            foreach (var source in _children.Keys.Concat(_related.Keys).Concat(_backlinks.Keys).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
            {
                if (_children.TryGetValue(source, out var children))
                    foreach (var relation in children.Values)
                        yield return WikiRelation(generation, source, relation.Target, relation.Kind, relation.Label, relation.SharedEntityCount);
                if (_related.TryGetValue(source, out var related))
                    foreach (var relation in related.Values)
                        yield return WikiRelation(generation, source, relation.Target, relation.Kind, relation.Label, relation.SharedEntityCount);
                if (_backlinks.TryGetValue(source, out var backlinks))
                    foreach (var relation in backlinks.Values)
                        yield return WikiRelation(generation, source, relation.Target, relation.Kind, relation.Label, relation.SharedEntityCount);
            }
        }

        private sealed class RelationBucket(int cap)
        {
            private readonly SortedDictionary<string, Candidate> _relations = new(StringComparer.Ordinal);

            public IEnumerable<Candidate> Values => _relations.Values;

            public void Add(string target, MemoryWikiRelationKind kind, string label, int sharedEntityCount, bool explicitRelation)
            {
                // Each relation kind has its own target budget; labels arbitrate repeated explicit links.
                var key = target;
                if (_relations.TryGetValue(key, out var existing))
                {
                    if (explicitRelation && !existing.Explicit ||
                        explicitRelation == existing.Explicit && string.CompareOrdinal(label, existing.Label) < 0)
                        _relations[key] = existing with { Label = label, SharedEntityCount = sharedEntityCount, Explicit = explicitRelation };
                    return;
                }

                if (_relations.Count == cap)
                {
                    // Curated wiki grammar has priority over the automatic graph projection when a page is full.
                    var largestAutomatic = _relations.Values
                        .Where(candidate => !candidate.Explicit)
                        .OrderBy(candidate => candidate.Target, StringComparer.Ordinal)
                        .LastOrDefault();
                    if (largestAutomatic is not null)
                    {
                        // A curated link always outranks an automatic candidate, regardless of its lexical key.
                        if (!explicitRelation && string.CompareOrdinal(key, largestAutomatic.Target) >= 0) return;
                        _relations.Remove(largestAutomatic.Target);
                    }
                    else if (explicitRelation)
                    {
                        // Between equally curated candidates retain the deterministic lexical bounded set.
                        var largestCurated = _relations.Values.OrderBy(candidate => candidate.Target, StringComparer.Ordinal).Last();
                        if (string.CompareOrdinal(key, largestCurated.Target) >= 0) return;
                        _relations.Remove(largestCurated.Target);
                    }
                    else return;
                }
                _relations.Add(key, new(target, kind, label, sharedEntityCount, explicitRelation));
            }
        }

        private sealed record Candidate(string Target, MemoryWikiRelationKind Kind, string Label, int SharedEntityCount, bool Explicit);
    }

    private async Task<PersistedReaderWikiDocumentRow?> ReadWikiDocumentBySlugCoreAsync(string generation, string @namespace, string slug, CancellationToken cancellationToken)
    {
        using var table = await OpenTableAsync(ReaderWikiDocumentsTable, cancellationToken).ConfigureAwait(false);
        var batches = await table.Query().Where($"generation_key = {Literal(generation)} AND namespace = {Literal(@namespace)} AND slug = {Literal(slug)}").Limit(2).ToArrow().ConfigureAwait(false);
        var rows = ReadReaderWikiDocumentRows(batches).ToArray();
        return rows.Length == 1 ? rows[0] : null;
    }

    private async Task PersistWikiDocumentsAsync(IReadOnlyCollection<PersistedReaderWikiDocumentRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        using var table = await OpenTableAsync(ReaderWikiDocumentsTable, cancellationToken).ConfigureAwait(false);
        await table.MergeInsert("document_key").WhenMatchedUpdateAll().WhenNotMatchedInsertAll().Execute(BuildReaderWikiDocumentBatch(rows)).ConfigureAwait(false);
    }

    private async Task PersistWikiRelationsAsync(IReadOnlyCollection<PersistedReaderWikiRelationRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        using var table = await OpenTableAsync(ReaderWikiRelationsTable, cancellationToken).ConfigureAwait(false);
        await table.MergeInsert("relation_key").WhenMatchedUpdateAll().WhenNotMatchedInsertAll().Execute(BuildReaderWikiRelationBatch(rows)).ConfigureAwait(false);
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
