namespace Agm.Memory.Abstractions;

public enum MemoryGraphEdgeKind
{
    SameEntity = 0,
    SameTopic = 1,
    SameSession = 2,
    References = 3,
    DerivedFrom = 4,
    SameProject = 5
}

public sealed record MemoryGraphScope(Guid TenantId);

public sealed record MemoryGraphCandidate(
    Guid MemoryId,
    Guid TenantId,
    double Score,
    Guid? ProjectId = null,
    IReadOnlyList<string>? EntityKeys = null,
    IReadOnlyList<string>? TopicKeys = null);

public sealed record MemoryGraphEdge(
    Guid TenantId,
    Guid FromMemoryId,
    Guid ToMemoryId,
    MemoryGraphEdgeKind Kind,
    double Strength = 1,
    DateTimeOffset? ObservedAt = null,
    bool Bidirectional = true);

public sealed record MemoryGraphEdgeWeights(
    double SameEntity = 1,
    double SameTopic = 0.65,
    double SameSession = 0.8,
    double References = 0.9,
    double DerivedFrom = 1,
    double SameProject = 0.15)
{
    public double For(MemoryGraphEdgeKind kind) => kind switch
    {
        MemoryGraphEdgeKind.SameEntity => SameEntity,
        MemoryGraphEdgeKind.SameTopic => SameTopic,
        MemoryGraphEdgeKind.SameSession => SameSession,
        MemoryGraphEdgeKind.References => References,
        MemoryGraphEdgeKind.DerivedFrom => DerivedFrom,
        MemoryGraphEdgeKind.SameProject => SameProject,
        _ => 0
    };
}

public sealed record MemoryGraphRerankOptions(
    bool Enabled = false,
    int WalkSteps = 2,
    int MaximumNeighbors = 10,
    double RestartProbability = 0.35,
    double Lambda = 0.2,
    TimeSpan? EdgeDecayHalfLife = null,
    MemoryGraphEdgeWeights? EdgeWeights = null,
    bool SessionBoostEnabled = false,
    double SessionEntityBoost = 0.05,
    double SessionTopicBoost = 0.025,
    double MaximumSessionBoost = 0.1,
    int MaximumSessionSignals = 16)
{
    public TimeSpan EffectiveEdgeDecayHalfLife => EdgeDecayHalfLife ?? TimeSpan.FromDays(30);
    public MemoryGraphEdgeWeights EffectiveEdgeWeights => EdgeWeights ?? new();
}

public sealed record MemoryGraphSessionContext(
    IReadOnlyList<string>? EntityKeys = null,
    IReadOnlyList<string>? TopicKeys = null);

public sealed record MemoryGraphRerankRequest(
    MemoryGraphScope Scope,
    IReadOnlyList<MemoryGraphCandidate> Candidates,
    IReadOnlyList<MemoryGraphEdge> Edges,
    IReadOnlySet<Guid> ExactSeedIds,
    DateTimeOffset Now,
    MemoryGraphSessionContext? Session = null);

public interface IMemoryGraphReranker
{
    IReadOnlyList<MemoryGraphCandidate> Rerank(
        MemoryGraphRerankRequest request,
        MemoryGraphRerankOptions options);
}
