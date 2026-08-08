namespace AgMemory.Contracts;

/// <summary>
/// Requests removal of a memory record using its expected version.
/// </summary>
public sealed record ForgetMemoryCommand(CommandEnvelope Envelope, MemoryId MemoryId, long ExpectedVersion);

/// <summary>
/// Reports the result of a version-checked forget request.
/// </summary>
public enum ForgetOutcome { Forgotten, NotFound, StaleVersion, Failed }

/// <summary>
/// Returns the safe result of forgetting a memory record.
/// </summary>
public sealed record ForgetResult(
    ForgetOutcome Outcome,
    long? CurrentVersion,
    MemoryError? Error,
    ContractVersion ContractVersion);
