using System.Text.Json;
using AgMemory.Web.Features.MemoryGraph;

namespace AgMemory.Web.Features.Knowledge;

/// <summary>Exposes read-only knowledge diagnostics to explicitly configured Development loopback callers.</summary>
public static class KnowledgeEndpoint
{
    public const string Route = "/api/knowledge";

    /// <summary>Checks access before any storage initialization and returns one safe unavailable response on failures.</summary>
    public static async Task<IResult> HandleAsync(HttpContext context, IHostEnvironment environment,
        LocalKnowledgeFeature feature, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryGraphAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress))
            return new KnowledgeJsonResult(KnowledgeApiResponse.Unavailable);
        try { return new KnowledgeJsonResult(await feature.ReadAsync(cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new KnowledgeJsonResult(KnowledgeApiResponse.Unavailable); }
    }
}

internal sealed class KnowledgeJsonResult(KnowledgeApiResponse response) : IResult
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes the allowlisted projection without involving executable adapter types.</summary>
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(httpContext.Response.Body, response, Json, httpContext.RequestAborted).ConfigureAwait(false);
    }
}
