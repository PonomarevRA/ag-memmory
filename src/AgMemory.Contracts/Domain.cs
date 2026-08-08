namespace AgMemory.Contracts;

public sealed record MemoryScope(
    ScopeId TenantId,
    ScopeId? ProjectId = null,
    ScopeId? WorkspaceId = null,
    ScopeId? ChatId = null,
    ScopeId? RunId = null)
{
    public void Validate()
    {
        TenantId.Validate(nameof(TenantId));
        ValidateOptional(ProjectId, nameof(ProjectId));
        ValidateOptional(WorkspaceId, nameof(WorkspaceId));
        ValidateOptional(ChatId, nameof(ChatId));
        ValidateOptional(RunId, nameof(RunId));
    }

    private static void ValidateOptional(ScopeId? value, string name)
    {
        if (value is { } supplied) supplied.Validate(name);
    }
}

/// <summary>Exact five-dimensional scope comparison; no hierarchy or prefix semantics exist here.</summary>
public sealed record ScopeSelector(MemoryScope Scope)
{
    public bool Matches(MemoryScope candidate) =>
        Scope.TenantId == candidate.TenantId &&
        Scope.ProjectId == candidate.ProjectId &&
        Scope.WorkspaceId == candidate.WorkspaceId &&
        Scope.ChatId == candidate.ChatId &&
        Scope.RunId == candidate.RunId;
}

public sealed record AuthorizedScopeSet
{
    public AuthorizedScopeSet(IEnumerable<ScopeSelector> selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);
        var supplied = selectors.ToArray();
        foreach (var selector in supplied) selector.Scope.Validate();
        Selectors = supplied
            .Distinct()
            .OrderBy(selector => selector.Scope.TenantId.Value, StringComparer.Ordinal)
            .ThenBy(selector => selector.Scope.ProjectId?.Value, StringComparer.Ordinal)
            .ThenBy(selector => selector.Scope.WorkspaceId?.Value, StringComparer.Ordinal)
            .ThenBy(selector => selector.Scope.ChatId?.Value, StringComparer.Ordinal)
            .ThenBy(selector => selector.Scope.RunId?.Value, StringComparer.Ordinal)
            .ToArray();
        if (Selectors.Count == 0)
            throw new ArgumentException("At least one exact scope selector is required.", nameof(selectors));
    }

    public IReadOnlyList<ScopeSelector> Selectors { get; }
    public bool Contains(MemoryScope scope) => Selectors.Any(selector => selector.Matches(scope));
}

public enum MemoryRecordType
{
    Fact,
    Decision,
    Preference,
    Task,
    Event,
    Procedure,
    Constraint,
    Incident,
    LessonLearned,
    Observation,
    Outcome,
    Summary
}

public enum MemoryLifecycleStatus { Draft, Active, Superseded, Invalid }

public enum MemoryLifecycleAction
{
    Confirm,
    Invalidate,
    Supersede,
    Contradict,
    UndoSupersede,
    ResolveConflict
}

public enum MemoryRelationType { Supersedes, Contradicts, Supports, Related }

/// <summary>Portable entity identity used by graph-capable adapters; it carries no source content.</summary>
public sealed record MemoryEntity(string Identity, string Name, string? Kind = null)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Identity, nameof(Identity));
        ArgumentException.ThrowIfNullOrWhiteSpace(Name, nameof(Name));
    }
}

/// <summary>Scope-bound relation. Adapters may persist it only after both endpoints pass exact authorization.</summary>
public sealed record MemoryRelation(
    MemoryId Id,
    MemoryScope Scope,
    MemoryId FromMemoryId,
    MemoryId ToMemoryId,
    MemoryRelationType Type,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public void Validate()
    {
        Id.Validate(nameof(Id));
        FromMemoryId.Validate(nameof(FromMemoryId));
        ToMemoryId.Validate(nameof(ToMemoryId));
        Scope.Validate();
        if (FromMemoryId == ToMemoryId) throw new ArgumentException("A memory relation cannot self-reference.");
        if (Version <= 0) throw new ArgumentOutOfRangeException(nameof(Version));
        if (CreatedAt.Offset != TimeSpan.Zero || UpdatedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Relation timestamps must be UTC.");
    }
}

public sealed record SourceEvidenceRef(
    string Identity,
    string? SourceSystem = null,
    string? FragmentIdentity = null,
    string? ApprovedMetadata = null)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Identity, nameof(Identity));
        if (Identity != Identity.Trim())
            throw new ArgumentException("Evidence identity must be normalized.", nameof(Identity));
    }
}

public sealed record MemoryProvenance(
    string SourceSystem,
    string? LegacyRecordId,
    ScopeId? WorkspaceId,
    ScopeId? ChatId,
    ScopeId? RunId,
    string? MessageId,
    string? ExecutionId,
    IReadOnlyList<SourceEvidenceRef> Evidence)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceSystem, nameof(SourceSystem));
        if (WorkspaceId is { } workspaceId) workspaceId.Validate(nameof(WorkspaceId));
        if (ChatId is { } chatId) chatId.Validate(nameof(ChatId));
        if (RunId is { } runId) runId.Validate(nameof(RunId));
        ArgumentNullException.ThrowIfNull(Evidence);
        foreach (var evidence in Evidence) evidence.Validate();
    }
}

public sealed record EmbeddingReference(
    string Provider,
    string Model,
    string ModelVersion,
    int Dimension,
    string Normalization,
    string ContentHash)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Provider, nameof(Provider));
        ArgumentException.ThrowIfNullOrWhiteSpace(Model, nameof(Model));
        ArgumentException.ThrowIfNullOrWhiteSpace(ModelVersion, nameof(ModelVersion));
        ArgumentException.ThrowIfNullOrWhiteSpace(Normalization, nameof(Normalization));
        ArgumentException.ThrowIfNullOrWhiteSpace(ContentHash, nameof(ContentHash));
        if (Dimension <= 0) throw new ArgumentOutOfRangeException(nameof(Dimension));
    }
}

public sealed record EmbeddingContract(
    string Provider,
    string Model,
    string ModelVersion,
    int Dimension,
    string Normalization,
    ContractVersion Version)
{
    public void Validate()
    {
        Version.Validate(nameof(Version));
        new EmbeddingReference(Provider, Model, ModelVersion, Dimension, Normalization, "configured").Validate();
    }

    public bool Matches(EmbeddingReference reference) =>
        string.Equals(Provider, reference.Provider, StringComparison.Ordinal) &&
        string.Equals(Model, reference.Model, StringComparison.Ordinal) &&
        string.Equals(ModelVersion, reference.ModelVersion, StringComparison.Ordinal) &&
        Dimension == reference.Dimension &&
        string.Equals(Normalization, reference.Normalization, StringComparison.Ordinal);
}

public sealed record DecisionDetails(
    string Problem,
    string Context,
    IReadOnlyList<string> Options,
    string Decision,
    string Reason,
    IReadOnlyList<string> Consequences,
    string? Outcome)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Problem, nameof(Problem));
        ArgumentException.ThrowIfNullOrWhiteSpace(Context, nameof(Context));
        ArgumentNullException.ThrowIfNull(Options);
        ArgumentException.ThrowIfNullOrWhiteSpace(Decision, nameof(Decision));
        ArgumentException.ThrowIfNullOrWhiteSpace(Reason, nameof(Reason));
        ArgumentNullException.ThrowIfNull(Consequences);
    }
}

/// <summary>Canonical durable memory. Reason is a nullable, ingress-redacted TD-P2-01 field.</summary>
public sealed record MemoryRecord(
    MemoryId Id,
    MemoryScope Scope,
    MemoryRecordType Type,
    MemoryLifecycleStatus Status,
    string CanonicalText,
    string? Reason,
    double Importance,
    double Confidence,
    int EstimatedTokenCost,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    IReadOnlyList<string> Entities,
    MemoryProvenance Provenance,
    EmbeddingReference? Embedding,
    DateTimeOffset? ExpiresAt,
    string DeduplicationKey,
    DecisionDetails? DecisionDetails = null)
{
    public void Validate()
    {
        Id.Validate(nameof(Id));
        Scope.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(CanonicalText, nameof(CanonicalText));
        if (Reason is not null) ArgumentException.ThrowIfNullOrWhiteSpace(Reason, nameof(Reason));
        ValidateUnitInterval(Importance, nameof(Importance));
        ValidateUnitInterval(Confidence, nameof(Confidence));
        if (EstimatedTokenCost <= 0) throw new ArgumentOutOfRangeException(nameof(EstimatedTokenCost));
        if (Version <= 0) throw new ArgumentOutOfRangeException(nameof(Version));
        if (CreatedAt.Offset != TimeSpan.Zero || UpdatedAt.Offset != TimeSpan.Zero || ExpiresAt?.Offset != TimeSpan.Zero)
            throw new ArgumentException("Record timestamps must be UTC.");
        ArgumentNullException.ThrowIfNull(Entities);
        if (Entities.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Entities cannot contain blank values.", nameof(Entities));
        if (Entities.Distinct(StringComparer.Ordinal).Count() != Entities.Count)
            throw new ArgumentException("Entities must be deduplicated.", nameof(Entities));
        Provenance.Validate();
        Embedding?.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(DeduplicationKey, nameof(DeduplicationKey));
        if (Type == MemoryRecordType.Decision)
            (DecisionDetails ?? throw new ArgumentException("Decision details are required.", nameof(DecisionDetails))).Validate();
    }

    internal static void ValidateUnitInterval(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed record SessionHotMemory(
    MemoryId Id,
    MemoryScope Scope,
    string Content,
    MemoryProvenance Provenance,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt)
{
    public void Validate()
    {
        Id.Validate(nameof(Id));
        Scope.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(Content, nameof(Content));
        Provenance.Validate();
        if (Version <= 0) throw new ArgumentOutOfRangeException(nameof(Version));
        if (CreatedAt.Offset != TimeSpan.Zero || UpdatedAt.Offset != TimeSpan.Zero || ExpiresAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Hot-memory timestamps must be UTC.");
    }
}
