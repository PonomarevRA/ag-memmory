using AgMemory.Contracts;

namespace AgMemory.Core;

internal sealed class CommandPreflight(
    IAuthorizationScopeValidator authorization,
    MemoryCoreOptions options)
{
    public Task<ScopeAuthorizationResult> AuthorizeAsync(
        ActorId actor,
        MemoryOperation operation,
        MemoryScope scope,
        CancellationToken cancellationToken) =>
        authorization.AuthorizeAsync(actor, operation, scope, cancellationToken);

    public bool TryValidateEnvelope(CommandEnvelope envelope, out MemoryError? error)
    {
        error = null;
        try
        {
            envelope.Validate();
            if (envelope.ContractVersion != options.SupportedContractVersion)
                error = new(MemoryErrorCode.UnsupportedContractVersion, nameof(envelope.ContractVersion));
        }
        catch (ArgumentException)
        {
            error = new(MemoryErrorCode.InvalidArgument, nameof(envelope));
        }
        return error is null;
    }

    public static ScopeSelector? ExactRequestedSelector(AuthorizedScopeSet set, MemoryScope requested) =>
        set.Selectors.SingleOrDefault(selector => selector.Matches(requested));
}
