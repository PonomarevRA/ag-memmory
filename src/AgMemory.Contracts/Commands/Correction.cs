namespace AgMemory.Contracts;

/// <summary>Requests a revision-checked supersession or invalidation; null replacement retracts without deleting.</summary>
public sealed record CorrectMemoryCommand(CommandEnvelope Envelope, MemoryId MemoryId, long ExpectedVersion,
    MemoryRecordInput? Replacement);

/// <summary>Reports correction without promising native multi-table crash atomicity.</summary>
public sealed record CorrectMemoryResult(string Outcome, MemoryRecord? Replacement, MemoryError? Error);
