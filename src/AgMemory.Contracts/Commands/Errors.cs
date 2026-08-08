namespace AgMemory.Contracts;

/// <summary>
/// Categorises failures that can be safely returned across the memory contract boundary.
/// </summary>
public enum MemoryErrorCode
{
    InvalidArgument,
    UnsupportedContractVersion,
    Unauthorized,
    RedactionRejected,
    IdempotencyKeyConflict,
    Conflict,
    StaleVersion,
    NotFound,
    InvalidTransition,
    PolicyNotConfigured,
    EmbeddingContractMismatch,
    DependencyFailure
}

/// <summary>
/// Privacy-safe failure shape; it deliberately has no free-form message field.
/// </summary>
public sealed record MemoryError(MemoryErrorCode Code, string? Field, string? PolicyOrRuleVersion = null);
