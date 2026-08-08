namespace AgMemory.Contracts;

/// <summary>
/// Carries the identity, authorisation scope and contract version shared by every write command.
/// </summary>
public sealed record CommandEnvelope(
    CommandId CommandId,
    string IdempotencyKey,
    ActorId Actor,
    CorrelationId CorrelationId,
    MemoryScope RequestedScope,
    ContractVersion ContractVersion)
{
    /// <summary>
    /// Verifies that the envelope can be safely evaluated before a command starts a transaction.
    /// </summary>
    public void Validate()
    {
        CommandId.Validate(nameof(CommandId));
        Actor.Validate(nameof(Actor));
        CorrelationId.Validate(nameof(CorrelationId));
        ContractVersion.Validate(nameof(ContractVersion));
        ArgumentException.ThrowIfNullOrWhiteSpace(IdempotencyKey, nameof(IdempotencyKey));
        if (IdempotencyKey.Length > 256) throw new ArgumentOutOfRangeException(nameof(IdempotencyKey));
        RequestedScope.Validate();
    }
}

/// <summary>
/// States whether a memory write requires an embedding to be produced before it is committed.
/// </summary>
public enum EmbeddingMode
{
    None,
    Required
}
