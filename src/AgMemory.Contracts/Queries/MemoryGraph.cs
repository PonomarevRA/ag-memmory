namespace AgMemory.Contracts;

/// <summary>Hard ceilings for one query-time memory-graph snapshot.</summary>
public static class MemoryGraphLimits
{
    public const int MaximumSourceRecords = 200;
    public const int MaximumVisibleNodes = 75;
    public const int MaximumEdges = 150;
}

/// <summary>Requests a graph for one exact, authorised memory scope.</summary>
public sealed record MemoryGraphRequest(
    ActorId Actor,
    MemoryScope RequestedScope,
    int SourceLimit,
    int VisibleNodeLimit,
    int EdgeLimit,
    ContractVersion ContractVersion);

/// <summary>
/// A source record contains only the fields needed for query-time graph derivation. It is a server-side
/// contract; adapters must never use it to expand outside the supplied eligibility predicate.
/// </summary>
public sealed record MemoryGraphSourceRecord(
    MemoryId MemoryId,
    MemoryScope Scope,
    MemoryRecordType Type,
    MemoryLifecycleStatus Status,
    double Importance,
    double Confidence,
    IReadOnlyList<string> Entities,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Bounded, pre-filtered graph-source request. The source applies exact scope, active lifecycle and expiry
/// before returning records; Core repeats the predicate before deriving the snapshot.
/// </summary>
public sealed record MemoryGraphSourceRequest(
    MemorySearchEligibility Eligibility,
    int Limit)
{
    public bool IsEligible(MemoryGraphSourceRecord record) =>
        Eligibility.AuthorizedScopes.Contains(record.Scope) &&
        record.Status == Eligibility.RequiredLifecycleStatus &&
        (record.ExpiresAt is null || record.ExpiresAt > Eligibility.AsOfUtc);
}

/// <summary>One internal snapshot node. Its memory identifier must be remapped before browser delivery.</summary>
public sealed record MemoryGraphNode(
    MemoryId MemoryId,
    MemoryRecordType Type,
    int ImportanceBand,
    int ConfidenceBand,
    int Degree);

/// <summary>Query-time relationship kind. It is not a persisted <see cref="MemoryRelation"/>.</summary>
public enum MemoryGraphEdgeKind
{
    SharedEntity
}

/// <summary>One unordered, aggregated relationship between two visible memory records.</summary>
public sealed record MemoryGraphEdge(
    MemoryId FirstMemoryId,
    MemoryId SecondMemoryId,
    int Weight,
    MemoryGraphEdgeKind Kind);

/// <summary>Contains a safe graph snapshot or a privacy-safe error.</summary>
public sealed record MemoryGraphSnapshot(
    IReadOnlyList<MemoryGraphNode> Nodes,
    IReadOnlyList<MemoryGraphEdge> Edges,
    MemoryError? Error,
    ContractVersion ContractVersion);
