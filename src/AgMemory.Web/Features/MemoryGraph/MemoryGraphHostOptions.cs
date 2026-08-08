using AgMemory.Contracts;

namespace AgMemory.Web.Features.MemoryGraph;

/// <summary>
/// Deliberate local-only composition input for the graph visualiser. A disabled, missing or malformed value
/// never selects a fallback actor or scope.
/// </summary>
public sealed class MemoryGraphHostOptions
{
    public const string SectionName = "MemoryGraph";

    public bool Enabled { get; init; }
    public string? StoragePath { get; init; }
    public string? ActorId { get; init; }
    public MemoryGraphScopeOptions? Scope { get; init; }

    internal MemoryGraphHostConfiguration? TryCreate(string contentRootPath)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(contentRootPath) || string.IsNullOrWhiteSpace(StoragePath) ||
            string.IsNullOrWhiteSpace(ActorId) || Scope is null || string.IsNullOrWhiteSpace(Scope.TenantId))
            return null;

        try
        {
            var scope = new MemoryScope(
                new ScopeId(Scope.TenantId),
                Optional(Scope.ProjectId),
                Optional(Scope.WorkspaceId),
                Optional(Scope.ChatId),
                Optional(Scope.RunId));
            scope.Validate();
            var actor = new ActorId(ActorId);
            actor.Validate(nameof(ActorId));
            var storagePath = Path.GetFullPath(StoragePath, contentRootPath);
            return new(actor, scope, storagePath);
        }
        catch
        {
            return null;
        }
    }

    private static ScopeId? Optional(string? value) => value is null ? null : new ScopeId(value);
}

public sealed class MemoryGraphScopeOptions
{
    public string? TenantId { get; init; }
    public string? ProjectId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? ChatId { get; init; }
    public string? RunId { get; init; }
}

internal sealed record MemoryGraphHostConfiguration(
    ActorId Actor,
    MemoryScope Scope,
    string StoragePath);
