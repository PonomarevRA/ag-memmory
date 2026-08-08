namespace Agm.Memory.Abstractions;

public sealed record WorkspaceMemoryProvenance(
    Guid WorkspaceId, Guid ChatId, Guid? RunId, Guid MessageId, Guid ExecutionId);

public sealed record WorkspaceMemorySnapshot(
    Guid WorkspaceId, bool Enabled, long SettingsVersion, long? Version,
    string? Reason, int CharacterCount, int EstimatedTokens,
    IReadOnlyList<WorkspaceMemoryProvenance> Provenance, DateTimeOffset? UpdatedAt,
    string? Content = null);

public sealed record WorkspaceMemoryInjection(
    string Content, string Reason, long Version,
    IReadOnlyList<WorkspaceMemoryProvenance> Provenance);

public enum WorkspaceMemoryMutationOutcome { Updated, Conflict, NotFound }

public sealed record WorkspaceMemoryMutationResult(
    WorkspaceMemoryMutationOutcome Outcome, WorkspaceMemorySnapshot? Snapshot);

public enum WorkspaceMemoryWriteOutcome { Updated, Conflict, NotFound, Disabled }

public sealed record WorkspaceMemoryWriteResult(
    WorkspaceMemoryWriteOutcome Outcome, WorkspaceMemorySnapshot? Snapshot);

public interface IWorkspaceMemoryService
{
    Task<WorkspaceMemorySnapshot?> GetAsync(
        Guid workspaceId, CancellationToken cancellationToken, bool includeContent = false);
    Task<WorkspaceMemoryMutationResult> SetEnabledAsync(
        Guid workspaceId, bool enabled, long expectedVersion, CancellationToken cancellationToken);
    Task<WorkspaceMemoryMutationResult> ForgetAsync(
        Guid workspaceId, long expectedVersion, CancellationToken cancellationToken);
    Task<WorkspaceMemoryInjection?> GetInjectionAsync(
        Guid workspaceId, CancellationToken cancellationToken);
}

/// <summary>
/// Optional write-side contract for hosts that persist consolidated memory directly.
/// It is separate from <see cref="IWorkspaceMemoryService"/> so read-only hosts do not
/// have to expose mutation capabilities.
/// </summary>
public interface IWorkspaceMemoryWriter
{
    Task<WorkspaceMemoryWriteResult> WriteAsync(
        Guid workspaceId,
        string content,
        string reason,
        IReadOnlyList<WorkspaceMemoryProvenance> provenance,
        long expectedVersion,
        CancellationToken cancellationToken);
}

public interface IWorkspaceMemoryExtractor
{
    string? Extract(string output);
}

public interface IWorkspaceMemoryConsolidator
{
    string Consolidate(string? existing, string candidate, int maximumCharacters);
}
