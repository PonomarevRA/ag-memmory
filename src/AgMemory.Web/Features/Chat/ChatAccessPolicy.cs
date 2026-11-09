using System.Net;
using AgMemory.Web.Gateway;

namespace AgMemory.Web.Features.Chat;

/// <summary>
/// Keeps a configured provider from becoming an anonymous production model proxy.
/// Production chat requires a deliberately implemented authenticated host; this starter host permits
/// real-model calls only from loopback during Development.
/// </summary>
public static class ChatAccessPolicy
{
    /// <summary>
    /// Returns whether the current host may invoke the configured gateway for this request.
    /// Disabled and deterministic demo modes never spend provider credits and can report their state normally.
    /// </summary>
    public static bool AllowsGatewayInvocation(
        ChatGatewayStatus gateway,
        bool isDevelopment,
        IPAddress? remoteAddress) =>
        !gateway.IsAvailable ||
        gateway.IsDemo ||
        (isDevelopment && IsLoopback(remoteAddress));

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address);
}
