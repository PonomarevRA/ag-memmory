using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Agm.Memory.Abstractions;

namespace Agm.Memory;

/// <summary>
/// Transactional in-process reference implementation of canonical ingestion.
/// Durable providers can implement the same contracts without changing retrieval consumers.
/// </summary>
public sealed partial class MemoryIngestionService(
    TimeProvider timeProvider,
    IMemoryCandidateExtractor? extractor = null) : IMemoryIngestionService, ICanonicalMemoryCatalog, ICanonicalMemoryLifecycle
{
    private const double ActiveConfidenceThreshold = 0.70;
    private const double ReviewImportanceThreshold = 0.85;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, IngestionReceipt> receipts = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, MemorySourceRecord> sources = [];
    private readonly Dictionary<Guid, MemorySourceFragmentRecord> fragments = [];
    private readonly Dictionary<Guid, CanonicalMemoryRecord> memories = [];
    private readonly List<MemoryEvidenceRecord> evidence = [];
    private readonly List<CanonicalMemoryVersion> versions = [];
    private readonly List<MemoryAuditEntry> audit = [];
    private readonly List<MemoryRelation> relations = [];

    public async Task<MemoryIngestResult> IngestAsync(
        MemoryIngestRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var candidates = request.Mode switch
        {
            MemoryIngestionMode.Manual => request.Candidates!,
            MemoryIngestionMode.Extract when extractor is not null =>
                await extractor.ExtractAsync(
                    request.Scope, request.Fragments, request.Extraction!, cancellationToken),
            _ => throw new InvalidOperationException("MemoryExtractorNotConfigured")
        };
        ValidateCandidates(candidates, request.Fragments.Count);
        var receiptKey = ReceiptKey(request.Scope, request.IdempotencyKey);
        var requestShape = RequestShape(request, candidates);

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (receipts.TryGetValue(receiptKey, out var duplicate))
            {
                if (!string.Equals(duplicate.RequestShape, requestShape, StringComparison.Ordinal))
                    throw new InvalidOperationException("MemoryIngestionIdempotencyConflict");
                return duplicate.Result with { IdempotencyHit = true };
            }

            var now = timeProvider.GetUtcNow();
            var sourceReference = request.Source.ExternalReference.Trim();
            var source = sources.Values.SingleOrDefault(item =>
                item.Scope == request.Scope && item.Type == request.Source.Type &&
                string.Equals(item.ExternalReference, sourceReference, StringComparison.Ordinal)) ??
                new MemorySourceRecord(
                    Guid.NewGuid(), request.Scope, request.Source.Type, sourceReference, now);
            var normalizedFragments = request.Fragments
                .Select(input =>
                {
                    var created = CreateFragment(source.Id, input, now);
                    return fragments.Values.FirstOrDefault(item =>
                               item.SourceId == source.Id &&
                               string.Equals(item.ContentHash, created.ContentHash, StringComparison.Ordinal) &&
                               string.Equals(item.ExternalReference, created.ExternalReference, StringComparison.Ordinal)) ??
                           created;
                })
                .ToArray();
            var itemResults = new List<MemoryIngestionItemResult>(candidates.Count);

            // All validation happens before this point. Mutations below are committed together
            // under one gate, giving the reference provider transaction-like visibility.
            sources.TryAdd(source.Id, source);
            foreach (var fragment in normalizedFragments) fragments.TryAdd(fragment.Id, fragment);

            foreach (var candidate in candidates)
            {
                var canonicalText = Normalize(candidate.Statement);
                if (canonicalText.Length == 0)
                {
                    itemResults.Add(new(MemoryIngestionAction.Ignored, null, false));
                    continue;
                }
                var existing = memories.Values.SingleOrDefault(memory =>
                    memory.Scope == request.Scope && memory.Type == candidate.Type &&
                    string.Equals(memory.CanonicalText, canonicalText, StringComparison.OrdinalIgnoreCase) &&
                    memory.Status != CanonicalMemoryStatus.Invalid);
                if (existing is not null)
                {
                    var reinforced = existing with
                    {
                        Confidence = Math.Max(existing.Confidence, candidate.Confidence),
                        Importance = Math.Max(existing.Importance, candidate.Importance),
                        UpdatedAt = now
                    };
                    memories[existing.Id] = reinforced;
                    AddEvidence(reinforced.Id, candidate, normalizedFragments);
                    audit.Add(Audit(request, reinforced.Id, "reinforce", "duplicate canonical memory", now));
                    itemResults.Add(new(MemoryIngestionAction.Reinforced, reinforced, false));
                    continue;
                }

                var status = candidate.RequestedStatus == CanonicalMemoryStatus.Active &&
                             candidate.AssertionKind == MemoryAssertionKind.Assertion &&
                             candidate.Confidence >= ActiveConfidenceThreshold
                    ? CanonicalMemoryStatus.Active
                    : CanonicalMemoryStatus.Draft;
                var requiresReview = status == CanonicalMemoryStatus.Draft ||
                                     candidate.Importance >= ReviewImportanceThreshold;
                var memory = new CanonicalMemoryRecord(
                    Guid.NewGuid(), request.Scope, candidate.Type, status,
                    Normalize(candidate.Subject), canonicalText,
                    string.IsNullOrWhiteSpace(candidate.Reason) ? null : Normalize(candidate.Reason),
                    candidate.Importance, candidate.Confidence,
                    candidate.Entities.Select(Normalize).Where(value => value.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    1, null, now, now);
                memories.Add(memory.Id, memory);
                versions.Add(new(
                    memory.Id, 1, memory.Status, memory.CanonicalText, memory.Reason,
                    memory.Confidence, now, request.Actor.Trim(), request.CorrelationId));
                AddEvidence(memory.Id, candidate, normalizedFragments);
                audit.Add(Audit(request, memory.Id, "create", "ingestion", now));
                itemResults.Add(new(MemoryIngestionAction.Created, memory, requiresReview));
            }

            var result = new MemoryIngestResult(
                source.Id, normalizedFragments.Select(item => item.Id).ToArray(),
                itemResults, false, request.CorrelationId);
            receipts.Add(receiptKey, new(requestShape, result));
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CanonicalMemoryRecord?> GetAsync(
        MemoryScope scope, Guid memoryId, CancellationToken cancellationToken)
    {
        scope.Validate();
        await gate.WaitAsync(cancellationToken);
        try
        {
            return memories.TryGetValue(memoryId, out var memory) && memory.Scope == scope
                ? memory
                : null;
        }
        finally { gate.Release(); }
    }

    public async Task<MemoryLifecycleResult> ApplyAsync(
        MemoryLifecycleRequest request, CancellationToken cancellationToken)
    {
        ValidateLifecycleRequest(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!memories.TryGetValue(request.MemoryId, out var memory) || memory.Scope != request.Scope)
                return new(MemoryLifecycleOutcome.NotFound, null);
            CanonicalMemoryRecord? related = null;
            if (request.RelatedMemoryId is { } relatedId)
            {
                if (!memories.TryGetValue(relatedId, out related) || related.Scope != request.Scope)
                    return new(MemoryLifecycleOutcome.NotFound, null);
            }
            if (memory.Version != request.ExpectedVersion)
                return new(MemoryLifecycleOutcome.StaleVersion, memory, related);

            var now = timeProvider.GetUtcNow();
            return request.Action switch
            {
                MemoryLifecycleAction.Confirm => Confirm(request, memory, related, now),
                MemoryLifecycleAction.Invalidate => Invalidate(request, memory, now),
                MemoryLifecycleAction.Supersede => Supersede(request, memory, related!, now, "supersede"),
                MemoryLifecycleAction.Contradict => Contradict(request, memory, related!, now),
                MemoryLifecycleAction.UndoSupersede => UndoSupersede(request, memory, now),
                MemoryLifecycleAction.ResolveConflict => ResolveConflict(request, memory, related!, now),
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<CanonicalMemoryRecord>> ListCurrentAsync(
        MemoryScope scope, CancellationToken cancellationToken)
    {
        scope.Validate();
        await gate.WaitAsync(cancellationToken);
        try
        {
            return memories.Values
                .Where(item => item.Scope == scope && item.Status == CanonicalMemoryStatus.Active)
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<MemoryRelation>> GetRelationsAsync(
        MemoryScope scope, Guid memoryId, CancellationToken cancellationToken)
    {
        scope.Validate();
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!memories.TryGetValue(memoryId, out var memory) || memory.Scope != scope) return [];
            return relations.Where(item => item.Scope == scope &&
                    (item.FromMemoryId == memoryId || item.ToMemoryId == memoryId))
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .ToArray();
        }
        finally { gate.Release(); }
    }

    public Task<IReadOnlyList<MemoryEvidenceRecord>> GetProvenanceAsync(
        MemoryScope scope, Guid memoryId, CancellationToken cancellationToken) =>
        ReadMemoryItemsAsync(scope, memoryId, evidence, item => item.MemoryId, cancellationToken);

    public Task<IReadOnlyList<CanonicalMemoryVersion>> GetHistoryAsync(
        MemoryScope scope, Guid memoryId, CancellationToken cancellationToken) =>
        ReadMemoryItemsAsync(scope, memoryId, versions, item => item.MemoryId, cancellationToken);

    public async Task<IReadOnlyList<MemorySourceFragmentRecord>> GetSourceFragmentsAsync(
        MemoryScope scope, Guid memoryId, CancellationToken cancellationToken)
    {
        var provenance = await GetProvenanceAsync(scope, memoryId, cancellationToken);
        var fragmentIds = provenance.Select(item => item.SourceFragmentId).ToHashSet();
        await gate.WaitAsync(cancellationToken);
        try { return fragments.Values.Where(item => fragmentIds.Contains(item.Id)).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<MemoryAuditEntry>> GetAuditAsync(
        MemoryScope scope, CancellationToken cancellationToken)
    {
        scope.Validate();
        await gate.WaitAsync(cancellationToken);
        try { return audit.Where(item => item.Scope == scope).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<CanonicalMemoryRecord>> ListAsync(
        MemoryScope scope, CancellationToken cancellationToken)
    {
        scope.Validate();
        await gate.WaitAsync(cancellationToken);
        try { return memories.Values.Where(item => item.Scope == scope).OrderBy(item => item.CreatedAt).ToArray(); }
        finally { gate.Release(); }
    }

    private MemoryLifecycleResult Confirm(
        MemoryLifecycleRequest request, CanonicalMemoryRecord memory,
        CanonicalMemoryRecord? confirmer, DateTimeOffset now)
    {
        if (memory.Status != CanonicalMemoryStatus.Draft)
            return Invalid(memory, confirmer);
        var updated = UpdateMemory(memory, CanonicalMemoryStatus.Active, null, request, now);
        var relation = confirmer is null ? null : AddRelation(
            request, updated.Id, confirmer.Id, MemoryRelationType.ConfirmedBy, now);
        AddLifecycleAudit(request, updated.Id, "confirm", now);
        return new(MemoryLifecycleOutcome.Applied, updated, confirmer, relation);
    }

    private MemoryLifecycleResult Invalidate(
        MemoryLifecycleRequest request, CanonicalMemoryRecord memory, DateTimeOffset now)
    {
        if (memory.Status == CanonicalMemoryStatus.Invalid) return Invalid(memory, null);
        var updated = UpdateMemory(memory, CanonicalMemoryStatus.Invalid, null, request, now);
        AddLifecycleAudit(request, updated.Id, "invalidate", now);
        return new(MemoryLifecycleOutcome.Applied, updated);
    }

    private MemoryLifecycleResult Supersede(
        MemoryLifecycleRequest request, CanonicalMemoryRecord memory,
        CanonicalMemoryRecord replacement, DateTimeOffset now, string action)
    {
        if (memory.Id == replacement.Id || memory.Status != CanonicalMemoryStatus.Active ||
            replacement.Status != CanonicalMemoryStatus.Active)
            return Invalid(memory, replacement);
        var updated = UpdateMemory(
            memory, CanonicalMemoryStatus.Superseded, replacement.Id, request, now);
        var relation = AddRelation(
            request, memory.Id, replacement.Id, MemoryRelationType.Supersedes, now);
        AddLifecycleAudit(request, memory.Id, action, now);
        return new(MemoryLifecycleOutcome.Applied, updated, replacement, relation);
    }

    private MemoryLifecycleResult Contradict(
        MemoryLifecycleRequest request, CanonicalMemoryRecord memory,
        CanonicalMemoryRecord contradiction, DateTimeOffset now)
    {
        if (memory.Id == contradiction.Id || memory.Status != CanonicalMemoryStatus.Active ||
            contradiction.Status != CanonicalMemoryStatus.Active ||
            relations.Any(item => item.IsActive && item.Type == MemoryRelationType.Contradicts &&
                ((item.FromMemoryId == memory.Id && item.ToMemoryId == contradiction.Id) ||
                 (item.FromMemoryId == contradiction.Id && item.ToMemoryId == memory.Id))))
            return Invalid(memory, contradiction);
        var updated = UpdateMemory(memory, memory.Status, memory.SupersededById, request, now);
        var relation = AddRelation(
            request, memory.Id, contradiction.Id, MemoryRelationType.Contradicts, now);
        AddLifecycleAudit(request, memory.Id, "contradict", now);
        return new(MemoryLifecycleOutcome.Applied, updated, contradiction, relation);
    }

    private MemoryLifecycleResult UndoSupersede(
        MemoryLifecycleRequest request, CanonicalMemoryRecord memory, DateTimeOffset now)
    {
        if (memory.Status != CanonicalMemoryStatus.Superseded || memory.SupersededById is not { } replacementId)
            return Invalid(memory, null);
        var relation = relations.LastOrDefault(item => item.IsActive &&
            item.Type == MemoryRelationType.Supersedes && item.FromMemoryId == memory.Id &&
            item.ToMemoryId == replacementId);
        if (relation is null) return Invalid(memory, null);
        ReverseRelation(relation, request.CorrelationId, now);
        var resolvedContradiction = relations.LastOrDefault(item => !item.IsActive &&
            item.Type == MemoryRelationType.Contradicts &&
            item.ReversedByCorrelationId == relation.CorrelationId &&
            ((item.FromMemoryId == memory.Id && item.ToMemoryId == replacementId) ||
             (item.FromMemoryId == replacementId && item.ToMemoryId == memory.Id)));
        if (resolvedContradiction is not null) RestoreRelation(resolvedContradiction);
        var updated = UpdateMemory(memory, CanonicalMemoryStatus.Active, null, request, now);
        AddLifecycleAudit(request, memory.Id, "undo_supersede", now);
        return new(MemoryLifecycleOutcome.Applied, updated,
            memories.GetValueOrDefault(replacementId), relations.Single(item => item.Id == relation.Id));
    }

    private MemoryLifecycleResult ResolveConflict(
        MemoryLifecycleRequest request, CanonicalMemoryRecord loser,
        CanonicalMemoryRecord winner, DateTimeOffset now)
    {
        if (loser.Id == winner.Id || loser.Status != CanonicalMemoryStatus.Active ||
            winner.Status != CanonicalMemoryStatus.Active)
            return Invalid(loser, winner);
        var contradiction = relations.LastOrDefault(item => item.IsActive &&
            item.Type == MemoryRelationType.Contradicts &&
            ((item.FromMemoryId == loser.Id && item.ToMemoryId == winner.Id) ||
             (item.FromMemoryId == winner.Id && item.ToMemoryId == loser.Id)));
        if (contradiction is null) return Invalid(loser, winner);
        ReverseRelation(contradiction, request.CorrelationId, now);
        return Supersede(request, loser, winner, now, "resolve_conflict");
    }

    private CanonicalMemoryRecord UpdateMemory(
        CanonicalMemoryRecord memory, CanonicalMemoryStatus status, Guid? supersededById,
        MemoryLifecycleRequest request, DateTimeOffset now)
    {
        var updated = memory with
        {
            Status = status,
            Version = memory.Version + 1,
            SupersededById = supersededById,
            UpdatedAt = now
        };
        memories[memory.Id] = updated;
        versions.Add(new(updated.Id, updated.Version, updated.Status, updated.CanonicalText,
            updated.Reason, updated.Confidence, now, request.Actor.Trim(), request.CorrelationId));
        return updated;
    }

    private MemoryRelation AddRelation(
        MemoryLifecycleRequest request, Guid from, Guid to,
        MemoryRelationType type, DateTimeOffset now)
    {
        var relation = new MemoryRelation(
            Guid.NewGuid(), request.Scope, from, to, type, true,
            request.Actor.Trim(), request.Reason.Trim(), request.CorrelationId, now);
        relations.Add(relation);
        return relation;
    }

    private void ReverseRelation(MemoryRelation relation, Guid correlationId, DateTimeOffset now)
    {
        var reversed = relation with
        {
            IsActive = false,
            ReversedAt = now,
            ReversedByCorrelationId = correlationId
        };
        relations[relations.FindIndex(item => item.Id == relation.Id)] = reversed;
    }

    private void RestoreRelation(MemoryRelation relation)
    {
        var restored = relation with
        {
            IsActive = true,
            ReversedAt = null,
            ReversedByCorrelationId = null
        };
        relations[relations.FindIndex(item => item.Id == relation.Id)] = restored;
    }

    private void AddLifecycleAudit(
        MemoryLifecycleRequest request, Guid memoryId, string action, DateTimeOffset now) =>
        audit.Add(new(Guid.NewGuid(), request.Scope, memoryId, action,
            request.Actor.Trim(), request.Reason.Trim(), request.CorrelationId, now));

    private static MemoryLifecycleResult Invalid(
        CanonicalMemoryRecord memory, CanonicalMemoryRecord? related) =>
        new(MemoryLifecycleOutcome.InvalidTransition, memory, related);

    private async Task<IReadOnlyList<T>> ReadMemoryItemsAsync<T>(
        MemoryScope scope, Guid memoryId, IEnumerable<T> items, Func<T, Guid> getMemoryId,
        CancellationToken cancellationToken)
    {
        if (await GetAsync(scope, memoryId, cancellationToken) is null) return [];
        await gate.WaitAsync(cancellationToken);
        try { return items.Where(item => getMemoryId(item) == memoryId).ToArray(); }
        finally { gate.Release(); }
    }

    private void AddEvidence(
        Guid memoryId, CanonicalMemoryCandidate candidate,
        IReadOnlyList<MemorySourceFragmentRecord> sourceFragments)
    {
        foreach (var index in candidate.SourceFragmentIndexes)
        {
            var fragmentId = sourceFragments[index].Id;
            if (evidence.Any(item => item.MemoryId == memoryId && item.SourceFragmentId == fragmentId)) continue;
            evidence.Add(new(memoryId, fragmentId, MemoryEvidenceRole.Direct, candidate.Confidence));
        }
    }

    private static MemorySourceFragmentRecord CreateFragment(
        Guid sourceId, MemorySourceFragmentInput input, DateTimeOffset now)
    {
        var normalized = Normalize(input.Content);
        return new(
            Guid.NewGuid(), sourceId, input.ExternalReference?.Trim(), normalized,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))),
            input.AuthorReference?.Trim(), input.OccurredAt, now);
    }

    private static MemoryAuditEntry Audit(
        MemoryIngestRequest request, Guid memoryId, string action, string reason, DateTimeOffset now) =>
        new(Guid.NewGuid(), request.Scope, memoryId, action, request.Actor.Trim(), reason,
            request.CorrelationId, now, request.Extraction);

    private static void ValidateRequest(MemoryIngestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Scope.Validate();
        if (string.IsNullOrWhiteSpace(request.Source.ExternalReference) ||
            request.Source.ExternalReference.Length > 500)
            throw new ArgumentException("Source external reference is required.", nameof(request));
        if (request.Fragments.Count == 0 || request.Fragments.Any(item => string.IsNullOrWhiteSpace(item.Content)))
            throw new ArgumentException("At least one non-empty source fragment is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("Idempotency key is invalid.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Length > 200 || request.CorrelationId == Guid.Empty)
            throw new ArgumentException("Actor and correlation id are required.", nameof(request));
        if (request.Mode == MemoryIngestionMode.Manual && request.Candidates is null)
            throw new ArgumentException("Manual ingestion requires candidates.", nameof(request));
        if (request.Mode == MemoryIngestionMode.Extract && request.Extraction is null)
            throw new ArgumentException("Extraction metadata is required.", nameof(request));
    }

    private static void ValidateLifecycleRequest(MemoryLifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Scope.Validate();
        if (request.MemoryId == Guid.Empty || request.ExpectedVersion <= 0)
            throw new ArgumentException("Memory id and a positive expected version are required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Length > 200 ||
            string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500 ||
            request.CorrelationId == Guid.Empty)
            throw new ArgumentException("Actor, reason, and correlation id are required.", nameof(request));
        var needsRelated = request.Action is MemoryLifecycleAction.Supersede or
            MemoryLifecycleAction.Contradict or MemoryLifecycleAction.ResolveConflict;
        if (needsRelated && (request.RelatedMemoryId is null || request.RelatedMemoryId == Guid.Empty))
            throw new ArgumentException("This lifecycle action requires a related memory id.", nameof(request));
        if (!needsRelated && request.Action != MemoryLifecycleAction.Confirm &&
            request.RelatedMemoryId is not null)
            throw new ArgumentException("This lifecycle action does not accept a related memory id.", nameof(request));
    }

    private static void ValidateCandidates(
        IReadOnlyList<CanonicalMemoryCandidate> candidates, int fragmentCount)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Confidence is < 0 or > 1 || candidate.Importance is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(candidates), "Scores must be between zero and one.");
            if (string.IsNullOrWhiteSpace(candidate.Subject) || candidate.SourceFragmentIndexes.Count == 0 ||
                candidate.SourceFragmentIndexes.Any(index => index < 0 || index >= fragmentCount))
                throw new ArgumentException("Candidate scope or provenance is invalid.", nameof(candidates));
            if (candidate.RequestedStatus is CanonicalMemoryStatus.Superseded or CanonicalMemoryStatus.Invalid)
                throw new ArgumentException("Ingestion cannot publish terminal statuses directly.", nameof(candidates));
        }
    }

    private static string ReceiptKey(MemoryScope scope, string idempotencyKey) =>
        $"{scope.TenantId:N}:{scope.ProjectId?.ToString("N") ?? "_"}:{idempotencyKey.Trim()}";

    private static string RequestShape(
        MemoryIngestRequest request, IReadOnlyList<CanonicalMemoryCandidate> candidates)
    {
        var value = string.Join('|',
            request.Source.Type,
            request.Source.ExternalReference.Trim(),
            request.Mode,
            string.Join(';', request.Fragments.Select(item => Normalize(item.Content))),
            string.Join(';', candidates.Select(item =>
                $"{item.Type}:{Normalize(item.Subject)}:{Normalize(item.Statement)}:{item.Confidence:R}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string Normalize(string? value) =>
        Whitespace().Replace(value?.Trim() ?? string.Empty, " ");

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private sealed record IngestionReceipt(string RequestShape, MemoryIngestResult Result);
}
