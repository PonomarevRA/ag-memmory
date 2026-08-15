using System.Text.Json;

namespace AgMemory.Web.Features.AppVersion;

public static class AppVersionEndpoint
{
    public const string Route = "/api/version";

    public static IResult Handle()
    {
        var info = AppVersionInfo.From(typeof(Program).Assembly);
        return new AppVersionJsonResult(new AppVersionResponse(info.Version, info.InformationalVersion));
    }

    private sealed record AppVersionResponse(string Version, string InformationalVersion);

    private sealed class AppVersionJsonResult(AppVersionResponse response) : IResult
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, response, JsonOptions,
                httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
