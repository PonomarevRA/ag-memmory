namespace AgMemory.Contracts;

/// <summary>Independent classification of canonical content; residency and trust remain separate.</summary>
public enum KnowledgeClass { Unclassified, Knowledge, Procedure, Experience }
public enum FoundationStatus { None, PendingReview, Approved, Revoked }
public enum KnowledgeFeedbackKind { Helpful, Harmful, Irrelevant, Outdated, Incorrect, Applied }
public enum ProcedureApplicationOutcome { ReportedSuccess, VerifiedSuccess, Partial, Failed, NotApplicable, Unknown }
public enum ProblemOccurrenceStatus { Open, Diagnosing, Mitigated, Resolved, Closed }

public sealed record KnowledgeMetadata(string SourceId, long SourceRevision, KnowledgeClass Class,
    FoundationStatus Foundation, string Applicability, DateTimeOffset? ReviewAtUtc, bool WarmExcluded = false, long MetadataRevision = 1);
public sealed record KnowledgePresentation(string Receipt, string SourceId, long SourceRevision, string TaskKey,
    DateTimeOffset PresentedAtUtc, DateTimeOffset ExpiresAtUtc);
public sealed record KnowledgeFeedback(string Receipt, string TaskKey, KnowledgeFeedbackKind Kind, long Revision,
    DateTimeOffset UpdatedAtUtc);
public sealed record KnowledgeProcedure(string Key, long Revision, string Goal, string[] Aliases, string Applicability,
    string Preconditions, string Steps, string Verification, string? RunbookRef, DateTimeOffset UpdatedAtUtc);
public sealed record KnowledgeOccurrence(string Key, string FamilyKey, string Symptom, string Environment,
    ProblemOccurrenceStatus Status, DateTimeOffset OccurredAtUtc, long Revision = 1);
public sealed record KnowledgeApplication(string Key, string ProcedureKey, long ProcedureRevision, string Environment,
    ProcedureApplicationOutcome Outcome, string Evidence, DateTimeOffset AppliedAtUtc, string? OccurrenceKey = null);
public sealed record KnowledgeWorkingSet(long Revision, string Goal, string[] Facts, string[] Assumptions,
    string[] Questions, string NextAction, DateTimeOffset ExpiresAtUtc);
public sealed record KnowledgeResidency(string SourceId, long SourceRevision, DateTimeOffset ExpiresAtUtc);
public sealed record KnowledgeRequestReceipt(string Key, string Fingerprint, string Operation, string ResultKey,
    long ResultRevision, DateTimeOffset CreatedAtUtc);

/// <summary>One bounded authority-local document. A store commits state and idempotency receipts together.</summary>
public sealed record KnowledgeState
{
    public int SchemaVersion { get; init; } = 1;
    public long Version { get; init; }
    public Dictionary<string, KnowledgeMetadata> Metadata { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, KnowledgePresentation> Presentations { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, KnowledgeFeedback> Feedback { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, KnowledgeProcedure> Procedures { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, KnowledgeProcedure> ProcedureHistory { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, KnowledgeOccurrence> Occurrences { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, KnowledgeApplication> Applications { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, KnowledgeResidency> Warm { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, KnowledgeRequestReceipt> Requests { get; init; } = new(StringComparer.Ordinal);
    public KnowledgeWorkingSet? WorkingSet { get; set; }
}

/// <summary>Exact server-generated profile partition. Implementations must atomically publish one complete document.</summary>
public interface IKnowledgeStore
{
    Task<KnowledgeState> ReadAsync(string profileKey, CancellationToken cancellationToken);
    Task<bool> CompareExchangeAsync(string profileKey, long expectedVersion, KnowledgeState replacement, CancellationToken cancellationToken);
}

/// <summary>Optional durable replay ledger. State CAS and a single receipt must commit atomically; reads never truncate history.</summary>
public interface IKnowledgeReplayStore : IKnowledgeStore
{
    Task<KnowledgeRequestReceipt?> ReadRequestAsync(string profileKey, string requestKey, CancellationToken cancellationToken);
    Task<int> CountRequestsAsync(string profileKey, CancellationToken cancellationToken);
    Task<int> CountNamespaceRequestsAsync(string profileKey, CancellationToken cancellationToken);
    int MaximumRequests { get; }
}

public sealed record KnowledgeMutationResult(string Outcome, string Key, long Revision);
public sealed record KnowledgeUtility(int Presented, int Applied, int Helpful, int Harmful, int Irrelevant,
    int Outdated, int Incorrect, int DistinctTasks, double? Utility, DateTimeOffset? LastPresentedAtUtc,
    DateTimeOffset? LastAppliedAtUtc = null, int Presented1Day = 0, int Presented7Days = 0, int Presented30Days = 0,
    int Applied1Day = 0, int Applied7Days = 0, int Applied30Days = 0);
public sealed record KnowledgeReviewItem(string SourceId, long SourceRevision, string Reason, bool WarmExcluded);

/// <summary>Counts domain limits only; filesystem byte/profile limits and factual trust are separate.</summary>
public sealed record KnowledgeCollectionCapacity(string Collection, int Used, int Limit, int Remaining, bool NearLimit);
public sealed record KnowledgeCapacity(IReadOnlyList<KnowledgeCollectionCapacity> Collections,
    string ReplayRetention, bool DomainLimitsOnly = true)
{
    public string RequestCountScope { get; init; } = "profile";
    public int? ProfileRequestCount { get; init; }
}
