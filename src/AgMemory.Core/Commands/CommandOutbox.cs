using AgMemory.Contracts;

namespace AgMemory.Core;

internal static class CommandOutbox
{
    public static OutboxMessage Create(
        CommandEnvelope envelope,
        ScopeSelector scope,
        string kind,
        MemoryId id,
        ContractVersion contractVersion) => new(
        scope,
        CommandValueSupport.Hash($"{envelope.CommandId.Value}|{kind}"),
        kind,
        envelope.CommandId,
        envelope.CorrelationId,
        id.Value,
        contractVersion);
}
