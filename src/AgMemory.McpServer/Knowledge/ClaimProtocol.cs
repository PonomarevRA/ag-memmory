using AgMemory.Contracts;

namespace AgMemory.McpServer;

/// <summary>Claimed values are unverified. A current citation establishes source revision eligibility, not truth or approval.</summary>
public sealed record KnowledgeClaimView(string ClaimRef, string Subject, string Predicate, string Value,
    string SourceKind, string? Locator, DateTimeOffset? ObservedAtUtc, DateTimeOffset? ValidFromUtc,
    DateTimeOffset? ValidToUtc, DateTimeOffset RecordedAtUtc, string? SourceCitationRef, bool SourceCurrent,
    bool ValidityUnknown, bool? HasConflict, string EvidenceTrust = "caller_reported_unverified");
public sealed record KnowledgeClaimsView(DateTimeOffset AsOfUtc, DateTimeOffset KnownAtUtc,
    IReadOnlyList<KnowledgeClaimView> Items, int OmittedClaimCount, bool HasConflict);
public sealed record KnowledgeEvidenceView(AgentMemory Source, string SourceKind, string CitationMeaning,
    IReadOnlyList<KnowledgeClaimView> Claims, int OmittedClaimCount);
