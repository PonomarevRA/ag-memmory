namespace Agm.Memory.Abstractions;

public static class FanOutMemoryLimits
{
    public const int MaximumAppendCharacters = 4_000;
}

public sealed record FanOutMemoryScope(Guid WorkspaceId, Guid ParentChatId, Guid RunId);

public sealed record FanOutMemoryEntry(
    FanOutMemoryScope Scope,
    long Version,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt);

public enum FanOutMemoryWriteOutcome { Updated, Unchanged, Conflict }
public enum FanOutMemoryClearOutcome { Cleared, NotFound, Conflict }

public sealed record FanOutMemoryWriteResult(FanOutMemoryWriteOutcome Outcome, FanOutMemoryEntry? Entry);

public sealed record FanOutMemoryStatus(
    Guid RunId, Guid ParentChatId, long Version, int CharacterCount, int EstimatedTokens,
    DateTimeOffset UpdatedAt, DateTimeOffset ExpiresAt, bool IsExpired);

public interface IFanOutMemoryStore
{
    Task<FanOutMemoryWriteResult> AppendAsync(
        FanOutMemoryScope scope, string content, string? captureKey, long expectedVersion, TimeSpan ttl,
        CancellationToken cancellationToken);
    Task<FanOutMemoryEntry?> ReadAsync(FanOutMemoryScope scope, CancellationToken cancellationToken);
    Task<FanOutMemoryStatus?> GetStatusAsync(FanOutMemoryScope scope, CancellationToken cancellationToken);
    Task<FanOutMemoryClearOutcome> ClearAsync(
        FanOutMemoryScope scope, long? expectedVersion, CancellationToken cancellationToken);
}
