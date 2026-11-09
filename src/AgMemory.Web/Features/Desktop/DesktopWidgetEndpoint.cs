using System.Net;
using AgMemory.Web.Features.AgentActivity;

namespace AgMemory.Web.Features.Desktop;

/// <summary>Serves only local widget snapshots; callers cannot supply origins, paths, scopes, queries or remote targets.</summary>
public static class DesktopWidgetEndpoint
{
    /// <summary>Registers only the bounded read-only widget route, independently of graph API enablement.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/desktop/v1/widget", ReadAsync).WithName("GetDesktopWidgetV1")
            .WithSummary("Read a bounded local desktop widget snapshot")
            .Produces<DesktopWidgetResponse>().ProducesProblem(400).ProducesProblem(403);
    }

    /// <summary>Rejects authority/path selectors and foreign browser origins before reading authorized metadata.</summary>
    internal static async Task<IResult> ReadAsync(HttpContext context, IDesktopWidget widget, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Allowed(context)) return TypedResults.Problem(statusCode: 403, title: "Access forbidden.");
        if (context.Request.Query.Count != 0) return TypedResults.Problem(statusCode: 400, title: "Invalid query parameters.");
        var origin = context.Request.Scheme + "://" + context.Request.Host.Value;
        return TypedResults.Ok(await widget.ReadAsync(origin, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Requires a local peer, local Host and matching browser Origin when present; programmatic local HTTP remains supported.</summary>
    internal static bool Allowed(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)) return false;
        var localHost = context.Request.Host.Host == "localhost" || IPAddress.TryParse(context.Request.Host.Host, out var host) && IPAddress.IsLoopback(host);
        if (!localHost || context.Request.Headers["Sec-Fetch-Site"].ToString() == "cross-site") return false;
        return AgentActivityEndpoint.SameOrigin(context, false);
    }
}
