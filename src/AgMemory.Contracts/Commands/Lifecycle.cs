namespace AgMemory.Contracts;

/// <summary>
/// Requests a version-checked lifecycle transition for an existing memory record.
/// </summary>
public sealed record LifecycleCommand(
    CommandEnvelope Envelope,
    MemoryId MemoryId,
    MemoryLifecycleAction Action,
    long ExpectedVersion,
    MemoryId? RelatedMemoryId = null,
    string? Reason = null);

/// <summary>
/// Reports the result of a memory lifecycle transition.
/// </summary>
public enum LifecycleOutcome
{
    Applied,
    NotFound,
    StaleVersion,
    InvalidTransition,
    Failed
}

/// <summary>
/// Returns the memory state and safe error details after a lifecycle command.
/// </summary>
public sealed record LifecycleResult(
    LifecycleOutcome Outcome,
    MemoryRecord? Memory,
    long? CurrentVersion,
    MemoryError? Error,
    ContractVersion ContractVersion);
