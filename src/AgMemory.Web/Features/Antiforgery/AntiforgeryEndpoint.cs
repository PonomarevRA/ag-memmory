using Microsoft.AspNetCore.Antiforgery;

namespace AgMemory.Web.Features.Antiforgery;

public static class AntiforgeryEndpoint
{
    public const string Route = "/api/antiforgery";

    public static IResult HandleAsync(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new AntiforgeryResponse(tokens.RequestToken));
    }

    private sealed record AntiforgeryResponse(string? RequestToken);
}
