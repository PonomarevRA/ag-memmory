using System.Text.Json;
using AgMemory.Web.Features.MemoryGraph;

namespace AgMemory.Web.Features.MemoryStatus;

/// <summary>Loopback-only aggregate memory diagnostics. Browser callers never receive memory content or scope data.</summary>
public static class MemoryStatusEndpoint
{
    public const string Route = "/api/memory-status";

    public static async Task<IResult> HandleAsync(
        HttpContext context,
        IHostEnvironment environment,
        LocalMemoryGraphFeature feature,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryGraphAccessPolicy.AllowsStoreAccess(
                feature.IsConfigured,
                environment.IsDevelopment(),
                context.Connection.RemoteIpAddress))
            return Json(MemoryStatusApiResponse.Unavailable);

        try
        {
            var snapshot = await feature.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            return Json(new MemoryStatusApiResponse(
                "available",
                snapshot.TotalMemoryCount,
                snapshot.ActiveMemoryCount,
                snapshot.ExpiredMemoryCount,
                snapshot.InactiveMemoryCount,
                snapshot.LatestUpdateAt,
                snapshot.ActiveByType.Select(item => new MemoryStatusTypeCountDto(item.Type.ToString(), item.Count)).ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(MemoryStatusEndpoint))
                .LogWarning(exception, "Local memory status read failed.");
            return Json(MemoryStatusApiResponse.Unavailable);
        }
    }

    private static IResult Json(MemoryStatusApiResponse response) => new MemoryStatusJsonResult(response);
}

/// <summary>Browser-safe status response: values are aggregate counts and timestamps only.</summary>
public sealed record MemoryStatusApiResponse(
    string Status,
    int TotalMemoryCount,
    int ActiveMemoryCount,
    int ExpiredMemoryCount,
    int InactiveMemoryCount,
    DateTimeOffset? LatestUpdateAt,
    IReadOnlyList<MemoryStatusTypeCountDto> ActiveByType)
{
    public static MemoryStatusApiResponse Unavailable { get; } = new("unavailable", 0, 0, 0, 0, null, []);
}

public sealed record MemoryStatusTypeCountDto(string Type, int Count);

internal sealed class MemoryStatusJsonResult(MemoryStatusApiResponse response) : IResult
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(httpContext.Response.Body, response, response.GetType(), JsonOptions,
            httpContext.RequestAborted).ConfigureAwait(false);
    }
}
