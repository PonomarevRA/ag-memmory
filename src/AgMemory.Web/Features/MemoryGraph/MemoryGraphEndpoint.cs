using System.Net;
using System.Text.Json;
using AgMemory.Contracts;

namespace AgMemory.Web.Features.MemoryGraph;

/// <summary>Same-origin, input-free graph endpoint. It maps all durable IDs to response-local opaque values.</summary>
public static class MemoryGraphEndpoint
{
    public const string Route = "/api/memory-graph";

    public static async Task<IResult> HandleAsync(
        HttpContext context,
        IHostEnvironment environment,
        LocalMemoryGraphFeature feature,
        CancellationToken cancellationToken,
        string? continuation = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryGraphAccessPolicy.AllowsStoreAccess(
                feature.IsConfigured,
                environment.IsDevelopment(),
                context.Connection.RemoteIpAddress))
            return Json(MemoryGraphApiResponse.Unavailable);

        MemoryGraphPortionCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(continuation) && !feature.TryUnprotectContinuation(continuation, out cursor))
            return Json(MemoryGraphApiResponse.Stale);
        try
        {
            var snapshot = await feature.ReadPortionAsync(cursor, cancellationToken).ConfigureAwait(false);
            return Json(ToApiResponse(snapshot, feature));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Json(MemoryGraphApiResponse.Unavailable);
        }
    }

    private static IResult Json(MemoryGraphApiResponse response) => new MemoryGraphJsonResult(response);

    private static MemoryGraphApiResponse ToApiResponse(MemoryGraphPortion snapshot, LocalMemoryGraphFeature feature)
    {
        if (snapshot.State != MemoryGraphPortionState.Available) return snapshot.State == MemoryGraphPortionState.Changed
            ? MemoryGraphApiResponse.Changed : MemoryGraphApiResponse.Unavailable;
        return new("available", snapshot.Nodes.Select(node => new MemoryGraphNodeDto(node.NodeKey.Value, node.RecordHref,
            node.Type.ToString(), node.ImportanceBand, node.ConfidenceBand, node.Degree)).ToArray(),
            snapshot.Edges.Select(edge => new MemoryGraphEdgeDto(edge.FirstNodeKey.Value, edge.SecondNodeKey.Value, edge.Weight, edge.Kind.ToString())).ToArray(),
            feature.ProtectContinuation(snapshot.NextCursor));
    }
}

/// <summary>Browser-safe graph response. It deliberately has no scope, actor, durable ID, text or error field.</summary>
public sealed record MemoryGraphApiResponse(
    string Status,
    IReadOnlyList<MemoryGraphNodeDto> Nodes,
    IReadOnlyList<MemoryGraphEdgeDto> Edges,
    string? NextToken)
{
    public static MemoryGraphApiResponse Unavailable { get; } = new("unavailable", [], [], null);
    public static MemoryGraphApiResponse Changed { get; } = new("changed", [], [], null);
    public static MemoryGraphApiResponse Stale { get; } = new("stale", [], [], null);
}

public sealed record MemoryGraphNodeDto(
    string Id,
    string Href,
    string Type,
    int ImportanceBand,
    int ConfidenceBand,
    int Degree);

public sealed record MemoryGraphEdgeDto(
    string SourceId,
    string TargetId,
    int Weight,
    string Kind);

/// <summary>Writes a deterministic local JSON payload without requiring endpoint-specific request services.</summary>
internal sealed class MemoryGraphJsonResult(MemoryGraphApiResponse response) : IResult
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(httpContext.Response.Body, response, response.GetType(), JsonOptions,
            httpContext.RequestAborted).ConfigureAwait(false);
    }
}
