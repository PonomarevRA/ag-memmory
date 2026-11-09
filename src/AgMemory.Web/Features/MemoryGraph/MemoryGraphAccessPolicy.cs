using System.Net;

namespace AgMemory.Web.Features.MemoryGraph;

/// <summary>Keeps the local diagnostics graph from becoming a remotely reachable data-store reader.</summary>
public static class MemoryGraphAccessPolicy
{
    public static bool AllowsStoreAccess(bool isConfigured, bool isDevelopment, IPAddress? remoteAddress) =>
        isConfigured && isDevelopment && remoteAddress is not null && IPAddress.IsLoopback(remoteAddress);
}
