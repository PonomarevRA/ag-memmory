using AgMemory.Contracts;

namespace AgMemory.Web.Features.MemoryReader;

/// <summary>Explicit local composition for the content-bearing reader. No value falls back to a browser input.</summary>
public sealed class MemoryReaderHostOptions
{
    public const string SectionName = "MemoryReader";

    public bool Enabled { get; init; }
    public string? StoragePath { get; init; }
    public string? ActorId { get; init; }
    public string? HomeMemoryId { get; init; }
    public MemoryReaderScopeOptions? Scope { get; init; }

    internal MemoryReaderHostConfiguration? TryCreate(string dataDirectory)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(dataDirectory) || string.IsNullOrWhiteSpace(ActorId) ||
            string.IsNullOrWhiteSpace(HomeMemoryId) || Scope is null || string.IsNullOrWhiteSpace(Scope.TenantId))
            return null;

        try
        {
            var scope = new MemoryScope(new(Scope.TenantId), Optional(Scope.ProjectId), Optional(Scope.WorkspaceId),
                Optional(Scope.ChatId), Optional(Scope.RunId));
            scope.Validate();
            var actor = new ActorId(ActorId);
            actor.Validate(nameof(ActorId));
            var home = new MemoryId(HomeMemoryId);
            home.Validate(nameof(HomeMemoryId));
            var configuredPath = string.IsNullOrWhiteSpace(StoragePath) ? "memory-graph.lancedb" : StoragePath;
            var storagePath = Path.IsPathRooted(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : Path.GetFullPath(Path.Combine(dataDirectory, configuredPath));
            return new(actor, scope, home, storagePath);
        }
        catch
        {
            return null;
        }
    }

    private static ScopeId? Optional(string? value) => value is null ? null : new ScopeId(value);
}

public sealed class MemoryReaderScopeOptions
{
    public string? TenantId { get; init; }
    public string? ProjectId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? ChatId { get; init; }
    public string? RunId { get; init; }
}

internal sealed record MemoryReaderHostConfiguration(ActorId Actor, MemoryScope Scope, MemoryId HomeMemoryId, string StoragePath);
