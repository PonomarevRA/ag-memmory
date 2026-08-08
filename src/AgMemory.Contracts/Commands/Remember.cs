namespace AgMemory.Contracts;

/// <summary>
/// Describes the candidate record that a caller wants to retain as durable memory.
/// </summary>
public sealed record MemoryRecordInput(
    MemoryRecordType Type,
    string CanonicalText,
    string? Reason,
    double Importance,
    double Confidence,
    IReadOnlyList<string> Entities,
    MemoryProvenance Provenance,
    DateTimeOffset? ExpiresAt = null,
    DecisionDetails? DecisionDetails = null,
    EmbeddingMode EmbeddingMode = EmbeddingMode.None);

/// <summary>
/// Requests creation or reinforcement of one durable memory record.
/// </summary>
public sealed record RememberCommand(CommandEnvelope Envelope, MemoryRecordInput Record);

/// <summary>
/// Reports the durable-memory effect of a <see cref="RememberCommand"/>.
/// </summary>
public enum RememberOutcome
{
    Created,
    Reinforced,
    IdempotencyReplay,
    DuplicateConflict,
    Failed
}

/// <summary>
/// Returns the safe result of remembering a record without exposing provider diagnostics.
/// </summary>
public sealed record RememberResult(
    RememberOutcome Outcome,
    MemoryRecord? Memory,
    MemoryError? Error,
    ContractVersion ContractVersion);
