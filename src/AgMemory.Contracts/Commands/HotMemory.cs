namespace AgMemory.Contracts;

/// <summary>
/// Appends or updates short-lived session memory with an optimistic version check.
/// </summary>
public sealed record AppendHotMemoryCommand(
    CommandEnvelope Envelope,
    string Content,
    MemoryProvenance Provenance,
    DateTimeOffset ExpiresAt,
    string CaptureKey,
    long ExpectedVersion);

/// <summary>
/// Reports the effect of an append to session-scoped hot memory.
/// </summary>
public enum HotMemoryOutcome { Created, Updated, IdempotencyReplay, Conflict, StaleVersion, Failed }

/// <summary>
/// Returns the safe outcome of an append-hot-memory request.
/// </summary>
public sealed record HotMemoryResult(
    HotMemoryOutcome Outcome,
    SessionHotMemory? Memory,
    long? CurrentVersion,
    MemoryError? Error,
    ContractVersion ContractVersion);
