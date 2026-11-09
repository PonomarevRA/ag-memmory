using System.Security.Cryptography;
using System.Text;
using AgMemory.Contracts;

namespace AgMemory.Core;

internal static class CommandValueSupport
{
    public static string Canonicalize(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static IReadOnlyList<string> NormalizeEntities(IEnumerable<string> entities) => entities
        .Select(Canonicalize).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal)
        .OrderBy(item => item, StringComparer.Ordinal).ToArray();

    public static int EstimateTokenCost(string content) => Math.Max(1, (content.Length + 3) / 4);

    public static string ScopeKey(MemoryScope scope) => string.Join("|", new[]
    {
        scope.TenantId.Value, scope.ProjectId?.Value ?? "<null>", scope.WorkspaceId?.Value ?? "<null>",
        scope.ChatId?.Value ?? "<null>", scope.RunId?.Value ?? "<null>"
    });

    public static string Hash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    public static void ValidateUnitInterval(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    public static bool TryTransition(
        MemoryLifecycleStatus current,
        MemoryLifecycleAction action,
        bool hasRelated,
        out MemoryLifecycleStatus next)
    {
        next = current;
        switch (action)
        {
            case MemoryLifecycleAction.Confirm when current == MemoryLifecycleStatus.Draft && hasRelated:
                next = MemoryLifecycleStatus.Active;
                return true;
            case MemoryLifecycleAction.Invalidate when current is MemoryLifecycleStatus.Draft or MemoryLifecycleStatus.Active:
                next = MemoryLifecycleStatus.Invalid;
                return true;
            case MemoryLifecycleAction.Supersede when current == MemoryLifecycleStatus.Active && hasRelated:
            case MemoryLifecycleAction.ResolveConflict when current == MemoryLifecycleStatus.Active && hasRelated:
                next = MemoryLifecycleStatus.Superseded;
                return true;
            case MemoryLifecycleAction.Contradict when current == MemoryLifecycleStatus.Active && hasRelated:
                return true;
            case MemoryLifecycleAction.UndoSupersede when current == MemoryLifecycleStatus.Superseded:
                next = MemoryLifecycleStatus.Active;
                return true;
            default:
                return false;
        }
    }
}
