using Agm.Memory.Abstractions;

namespace Agm.Memory;

public sealed class StrictMemorySourceAccessPolicy(
    IEnumerable<MemorySourceType>? enabledSourceTypes = null) : IMemorySourceAccessPolicy
{
    private readonly HashSet<MemorySourceType> enabledTypes =
        enabledSourceTypes?.ToHashSet() ?? Enum.GetValues<MemorySourceType>().ToHashSet();

    public MemorySourceAccessOutcome Evaluate(MemorySourceAccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentNullException.ThrowIfNull(request.RequestScope);
        ArgumentNullException.ThrowIfNull(request.SourceScope);
        request.RequestScope.Validate();
        request.SourceScope.Validate();

        if (!enabledTypes.Contains(request.Source.Type)) return MemorySourceAccessOutcome.SourceKindDisabled;
        if (request.RequestScope.TenantId != request.SourceScope.TenantId)
            return MemorySourceAccessOutcome.TenantMismatch;
        if (request.RequestScope.ProjectId != request.SourceScope.ProjectId)
            return MemorySourceAccessOutcome.ProjectMismatch;
        return MemorySourceAccessOutcome.Allowed;
    }
}
