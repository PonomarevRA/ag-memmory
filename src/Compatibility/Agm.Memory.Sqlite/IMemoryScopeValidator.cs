using Agm.Memory.Abstractions;

namespace Agm.Memory.Sqlite;

/// <summary>
/// Lets a host enforce ownership of identifiers without coupling the module to host tables.
/// Standalone consumers may use the default validator, which accepts non-empty scopes.
/// </summary>
public interface IMemoryScopeValidator
{
    ValueTask<bool> WorkspaceExistsAsync(Guid workspaceId, CancellationToken cancellationToken);

    ValueTask<bool> FanOutScopeExistsAsync(
        FanOutMemoryScope scope, CancellationToken cancellationToken);
}

internal sealed class AllowAllMemoryScopeValidator : IMemoryScopeValidator
{
    public static readonly AllowAllMemoryScopeValidator Instance = new();

    public ValueTask<bool> WorkspaceExistsAsync(
        Guid workspaceId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(workspaceId != Guid.Empty);

    public ValueTask<bool> FanOutScopeExistsAsync(
        FanOutMemoryScope scope, CancellationToken cancellationToken) =>
        ValueTask.FromResult(
            scope.WorkspaceId != Guid.Empty &&
            scope.ParentChatId != Guid.Empty &&
            scope.RunId != Guid.Empty);
}
