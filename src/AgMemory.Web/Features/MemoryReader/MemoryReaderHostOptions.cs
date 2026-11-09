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
    /// <summary>Server-owned reader areas. Browser clients can only select the safe id.</summary>
    public IReadOnlyList<MemoryReaderAreaOptions>? Areas { get; init; }

    internal MemoryReaderHostConfiguration? TryCreate(string dataDirectory)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(dataDirectory) || string.IsNullOrWhiteSpace(ActorId) ||
            Scope is null || string.IsNullOrWhiteSpace(Scope.TenantId))
            return null;

        try
        {
            var scope = new MemoryScope(new(Scope.TenantId), Optional(Scope.ProjectId), Optional(Scope.WorkspaceId),
                Optional(Scope.ChatId), Optional(Scope.RunId));
            scope.Validate();
            var actor = new ActorId(ActorId);
            actor.Validate(nameof(ActorId));
            MemoryId? home = null;
            if (!string.IsNullOrWhiteSpace(HomeMemoryId))
            {
                home = new MemoryId(HomeMemoryId);
                home.Value.Validate(nameof(HomeMemoryId));
            }
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

    internal IReadOnlyList<MemoryReaderAreaConfiguration> TryCreateAreas(string dataDirectory)
    {
        if (Areas is not { Count: > 0 })
        {
            var legacy = TryCreate(dataDirectory);
            return legacy is null ? [] : [new("default", "Память", legacy)];
        }

        var result = new List<MemoryReaderAreaConfiguration>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var area in Areas)
        {
            if (area is null || !area.Enabled || !IsSafeAreaId(area.Id) || string.IsNullOrWhiteSpace(area.Label) || area.Label.Length > 120)
                continue;
            var id = area.Id!;
            if (!ids.Add(id)) continue;
            var configuration = new MemoryReaderHostOptions
            {
                Enabled = true, StoragePath = area.StoragePath, ActorId = area.ActorId,
                HomeMemoryId = area.HomeMemoryId, Scope = area.Scope
            }.TryCreate(dataDirectory);
            if (configuration is not null) result.Add(new(id, area.Label, configuration));
        }
        return result;
    }

    private static ScopeId? Optional(string? value) => value is null ? null : new ScopeId(value);
    private static bool IsSafeAreaId(string? value) => value is { Length: > 0 and <= 64 } &&
        value[0] is >= 'a' and <= 'z' && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}

public sealed class MemoryReaderAreaOptions
{
    public string? Id { get; init; }
    public string? Label { get; init; }
    public bool Enabled { get; init; } = true;
    public string? StoragePath { get; init; }
    public string? ActorId { get; init; }
    public string? HomeMemoryId { get; init; }
    public MemoryReaderScopeOptions? Scope { get; init; }
}

public sealed class MemoryReaderScopeOptions
{
    public string? TenantId { get; init; }
    public string? ProjectId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? ChatId { get; init; }
    public string? RunId { get; init; }
}

internal sealed record MemoryReaderHostConfiguration(ActorId Actor, MemoryScope Scope, MemoryId? HomeMemoryId, string StoragePath);
internal sealed record MemoryReaderAreaConfiguration(string Id, string Label, MemoryReaderHostConfiguration Configuration);
