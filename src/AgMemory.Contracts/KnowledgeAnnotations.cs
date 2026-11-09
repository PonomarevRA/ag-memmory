namespace AgMemory.Contracts;

/// <summary>Caller-reported evidence kind, independent of canonical memory type, confidence, citation validity and truth.</summary>
public enum KnowledgeSourceKind { LegacyUnknown, UserStatement, ToolObservation, AgentInference }
public sealed record KnowledgeClaim(string Key, string RequestFingerprint, string SourceId, long SourceRevision,
    string Subject, string Predicate, string Value, KnowledgeSourceKind SourceKind, string? Locator,
    DateTimeOffset? ObservedAtUtc, DateTimeOffset? ValidFromUtc, DateTimeOffset? ValidToUtc,
    DateTimeOffset RecordedAtUtc, string? SupersedesKey);

/// <summary>Facets are advice labels for one exact procedure revision; changing the procedure never inherits old qualification.</summary>
public sealed record KnowledgeProcedureFacets(string ProcedureKey, long ProcedureRevision, string EnvironmentFamily,
    string[] RequiredCapabilities, string[] Preconditions, string[] TransferWarnings, string Fingerprint);

/// <summary>Separate namespace prevents older knowledge-v1 writers from dropping fields they cannot understand.</summary>
public sealed record KnowledgeAnnotationState
{
    public int SchemaVersion { get; init; } = 1;
    public long Version { get; init; }
    public Dictionary<string, KnowledgeClaim> Claims { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, KnowledgeProcedureFacets> ProcedureFacets { get; init; } = new(StringComparer.Ordinal);
}

public interface IKnowledgeAnnotationStore
{
    Task<KnowledgeAnnotationState> ReadAsync(string profile, CancellationToken token);
    Task<bool> CompareExchangeAsync(string profile, long expectedVersion, KnowledgeAnnotationState next, CancellationToken token);
}
