using AgMemory.Contracts;

namespace AgMemory.McpServer;

/// <summary>Safe public knowledge projections contain opaque references, never store IDs or configured authority.</summary>
public sealed record KnowledgeProcedureView(string ProcedureRef, long Revision, string Goal, string[] Aliases,
    string Applicability, string Preconditions, string Steps, string Verification, string? RunbookRef, DateTimeOffset UpdatedAtUtc);
public sealed record KnowledgePresentationView(string ReceiptRef, DateTimeOffset ExpiresAtUtc);
public sealed record KnowledgeContextItem(AgentMemory Memory, string Level, KnowledgeUtility Utility, bool RequiresReview);
public sealed record KnowledgeContextView(IReadOnlyList<KnowledgeContextItem> Items, KnowledgeWorkingSet? WorkingSet,
    int EstimatedTokenCost, int OmittedCandidateCount, string Mode, string PolicyVersion)
{
    /// <summary>Current shared L2 CAS fence, even when expired content is hidden; zero means no set was ever written.</summary>
    public long WorkingSetRevision { get; init; }
    /// <summary>Present only for an explicit task request. This label is not an authorization or participant identity.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskKey { get; init; }
}
public sealed record KnowledgeReviewView(string CitationRef, long Revision, string Reason, bool WarmExcluded);
public sealed record KnowledgeCapabilitiesView(string SchemaVersion, string ProfileRef, string[] Operations,
    string WorkingAuthority, string TaskIdentity, bool HumanFeedback, bool FoundationApproval, bool VerifiedSuccess,
    int MaximumContextBudget, string CleanupMode)
{
    /// <summary>Opaque association for knowledge only; ProfileRef still identifies actor-bound citation authority.</summary>
    public string KnowledgeProfileRef { get; init; } = ProfileRef;
    public string KnowledgeSharing { get; init; } = "actor";
    /// <summary>Included only when requested; discovery without usage does not open knowledge storage.</summary>
    public KnowledgeCapacity? Capacity { get; init; }
    public string TaskWorkingStorage { get; init; } = "separate_bounded_schema_v1_namespace";
    public int MaximumTaskWorkingProfiles { get; init; } = 128;
    public string KnowledgeStorage { get; init; } = "legacy_v1";
    public int? JournalMaximumDatabaseBytes { get; init; }
}
