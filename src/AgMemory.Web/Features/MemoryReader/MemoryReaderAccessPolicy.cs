using System.Net;

namespace AgMemory.Web.Features.MemoryReader;

/// <summary>Keeps the local content reader from becoming a remotely reachable memory API.</summary>
public static class MemoryReaderAccessPolicy
{
    public static bool AllowsStoreAccess(bool isConfigured, bool isDevelopment, IPAddress? remoteAddress) =>
        isConfigured && isDevelopment && remoteAddress is not null && IPAddress.IsLoopback(remoteAddress);
}
