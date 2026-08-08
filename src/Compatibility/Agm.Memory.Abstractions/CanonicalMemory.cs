namespace Agm.Memory.Abstractions;

public enum CanonicalMemoryType
{
    Fact,
    Decision,
    Preference,
    Task,
    Event,
    Procedure,
    Constraint,
    Incident,
    LessonLearned
}

public enum CanonicalMemoryStatus { Draft, Active, Superseded, Invalid }
public enum MemorySourceType { Conversation, Document, Task, Manual }
public enum MemoryEvidenceRole { Direct, Supporting, Contradicting }
public enum MemoryIngestionMode { Manual, Extract }
public enum MemoryIngestionAction { Created, Reinforced, Ignored }
public enum MemoryAssertionKind { Assertion, Question, Hypothesis }
public enum MemoryLifecycleAction
{
    Confirm,
    Invalidate,
    Supersede,
    Contradict,
    UndoSupersede,
    ResolveConflict
}
public enum MemoryLifecycleOutcome { Applied, NotFound, StaleVersion, InvalidTransition }
public enum MemoryRelationType { Supersedes, Contradicts, ConfirmedBy }

public sealed record MemoryScope(Guid TenantId, Guid? ProjectId = null);

public static class MemoryScopeValidation
{
    public static void Validate(this MemoryScope scope)
    {
        if (scope.TenantId == Guid.Empty)
            throw new ArgumentException("Tenant id must not be empty.", nameof(scope.TenantId));
        if (scope.ProjectId == Guid.Empty)
            throw new ArgumentException("Project id must be null or non-empty.", nameof(scope.ProjectId));
    }
}

public sealed record MemorySourceDescriptor(MemorySourceType Type, string ExternalReference);

public sealed record MemorySourceFragmentInput(
    string Content,
    string? ExternalReference = null,
    string? AuthorReference = null,
    DateTimeOffset? OccurredAt = null);

public sealed record MemoryExtractionMetadata(
    string ExtractorVersion,
    string? ModelId = null,
    string? PromptVersion = null);

public sealed record CanonicalMemoryCandidate(
    CanonicalMemoryType Type,
    string Subject,
    string Statement,
    string? Reason,
    double Confidence,
    double Importance,
    IReadOnlyList<string> Entities,
    IReadOnlyList<int> SourceFragmentIndexes,
    CanonicalMemoryStatus RequestedStatus = CanonicalMemoryStatus.Active,
    MemoryAssertionKind AssertionKind = MemoryAssertionKind.Assertion);

public sealed record MemoryIngestRequest(
    MemoryScope Scope,
    MemorySourceDescriptor Source,
    IReadOnlyList<MemorySourceFragmentInput> Fragments,
    MemoryIngestionMode Mode,
    string IdempotencyKey,
    string Actor,
    Guid CorrelationId,
    IReadOnlyList<CanonicalMemoryCandidate>? Candidates = null,
    MemoryExtractionMetadata? Extraction = null);

public sealed record CanonicalMemoryRecord(
    Guid Id,
    MemoryScope Scope,
    CanonicalMemoryType Type,
    CanonicalMemoryStatus Status,
    string Subject,
    string CanonicalText,
    string? Reason,
    double Importance,
    double Confidence,
    IReadOnlyList<string> Entities,
    long Version,
    Guid? SupersededById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MemorySourceRecord(
    Guid Id,
    MemoryScope Scope,
    MemorySourceType Type,
    string ExternalReference,
    DateTimeOffset CreatedAt);

public sealed record MemorySourceFragmentRecord(
    Guid Id,
    Guid SourceId,
    string? ExternalReference,
    string Content,
    string ContentHash,
    string? AuthorReference,
    DateTimeOffset? OccurredAt,
    DateTimeOffset CreatedAt);

public sealed record MemoryEvidenceRecord(
    Guid MemoryId,
    Guid SourceFragmentId,
    MemoryEvidenceRole Role,
    double ExtractionConfidence);

public sealed record CanonicalMemoryVersion(
    Guid MemoryId,
    long Version,
    CanonicalMemoryStatus Status,
    string CanonicalText,
    string? Reason,
    double Confidence,
    DateTimeOffset CreatedAt,
    string Actor,
    Guid CorrelationId);

public sealed record MemoryAuditEntry(
    Guid Id,
    MemoryScope Scope,
    Guid? MemoryId,
    string Action,
    string Actor,
    string Reason,
    Guid CorrelationId,
    DateTimeOffset CreatedAt,
    MemoryExtractionMetadata? Extraction = null);

public sealed record MemoryRelation(
    Guid Id,
    MemoryScope Scope,
    Guid FromMemoryId,
    Guid ToMemoryId,
    MemoryRelationType Type,
    bool IsActive,
    string Actor,
    string Reason,
    Guid CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReversedAt = null,
    Guid? ReversedByCorrelationId = null);

public sealed record MemoryLifecycleRequest(
    MemoryScope Scope,
    Guid MemoryId,
    MemoryLifecycleAction Action,
    long ExpectedVersion,
    string Actor,
    string Reason,
    Guid CorrelationId,
    Guid? RelatedMemoryId = null);

public sealed record MemoryLifecycleResult(
    MemoryLifecycleOutcome Outcome,
    CanonicalMemoryRecord? Memory,
    CanonicalMemoryRecord? RelatedMemory = null,
    MemoryRelation? Relation = null);

public sealed record MemoryIngestionItemResult(
    MemoryIngestionAction Action,
    CanonicalMemoryRecord? Memory,
    bool RequiresReview);

public sealed record MemoryIngestResult(
    Guid SourceId,
    IReadOnlyList<Guid> SourceFragmentIds,
    IReadOnlyList<MemoryIngestionItemResult> Items,
    bool IdempotencyHit,
    Guid CorrelationId);

public interface IMemoryCandidateExtractor
{
    Task<IReadOnlyList<CanonicalMemoryCandidate>> ExtractAsync(
        MemoryScope scope,
        IReadOnlyList<MemorySourceFragmentInput> fragments,
        MemoryExtractionMetadata metadata,
        CancellationToken cancellationToken);
}

public interface IMemoryIngestionService
{
    Task<MemoryIngestResult> IngestAsync(
        MemoryIngestRequest request, CancellationToken cancellationToken);
}

public interface ICanonicalMemoryLifecycle
{
    Task<MemoryLifecycleResult> ApplyAsync(
        MemoryLifecycleRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<CanonicalMemoryRecord>> ListCurrentAsync(
        MemoryScope scope, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRelation>> GetRelationsAsync(
        MemoryScope scope, Guid memoryId, CancellationToken cancellationToken);
}

public interface ICanonicalMemoryCatalog
{
    Task<CanonicalMemoryRecord?> GetAsync(
        MemoryScope scope, Guid memoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryEvidenceRecord>> GetProvenanceAsync(
        MemoryScope scope, Guid memoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemorySourceFragmentRecord>> GetSourceFragmentsAsync(
        MemoryScope scope, Guid memoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CanonicalMemoryVersion>> GetHistoryAsync(
        MemoryScope scope, Guid memoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryAuditEntry>> GetAuditAsync(
        MemoryScope scope, CancellationToken cancellationToken);
    Task<IReadOnlyList<CanonicalMemoryRecord>> ListAsync(
        MemoryScope scope, CancellationToken cancellationToken);
}
