using AgMemory.Web.Features.MemoryReader;
using System.Net;
using System.Text.Json;
using AgMemory.Contracts;

namespace AgMemory.Web.Features.MemoryGraph;

/// <summary>Same-origin fixed-scope graph endpoint; an optional area input only confirms reader navigation continuity.</summary>
public static class MemoryGraphEndpoint
{
    public const string Route = "/api/memory-graph";

    /// <summary>Validates loopback access, the fixed reader-area assertion and its protected continuation before reading topology.</summary>
    public static async Task<IResult> HandleAsync(
        HttpContext context,
        IHostEnvironment environment,
        LocalMemoryGraphFeature feature,
        CancellationToken cancellationToken,
        string? continuation = null,
        LocalMemoryReaderFeature? reader = null,
        string? area = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryGraphAccessPolicy.AllowsStoreAccess(
                feature.IsConfigured,
                environment.IsDevelopment(),
                context.Connection.RemoteIpAddress))
            return Json(MemoryGraphApiResponse.Unavailable);

        MemoryReaderAreaDto? graphArea = null;
        reader?.TryMatchGraphArea(feature, out graphArea);
        // Browser area is only a continuity assertion; it never changes the fixed graph selector.
        if (area is not null && !string.Equals(area, graphArea?.Id, StringComparison.Ordinal))
            return Json(MemoryGraphApiResponse.Unavailable);

        MemoryGraphPortionCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(continuation) && !feature.TryUnprotectContinuation(continuation, out cursor, graphArea?.Id))
            return Json(MemoryGraphApiResponse.Stale);
        try
        {
            var snapshot = await feature.ReadPortionAsync(cursor, cancellationToken).ConfigureAwait(false);
            return Json(ToApiResponse(snapshot, feature, graphArea));
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

    /// <summary>Remaps safe topology to browser DTOs; only an exact server reader-area match enables document navigation.</summary>
    private static MemoryGraphApiResponse ToApiResponse(MemoryGraphPortion snapshot, LocalMemoryGraphFeature feature, MemoryReaderAreaDto? graphArea)
    {
        if (snapshot.State != MemoryGraphPortionState.Available) return snapshot.State == MemoryGraphPortionState.Changed
            ? MemoryGraphApiResponse.Changed : MemoryGraphApiResponse.Unavailable;
        var nodes = new List<MemoryGraphNodeDto>(snapshot.Nodes.Count);
        foreach (var node in snapshot.Nodes)
        {
            var href = graphArea is null ? null : $"{node.RecordHref}?area={Uri.EscapeDataString(graphArea.Id)}";
            nodes.Add(new(node.NodeKey.Value, href, node.Type.ToString(), node.ImportanceBand, node.ConfidenceBand, node.Degree));
        }
        var edges = snapshot.Edges.Select(edge => new MemoryGraphEdgeDto(edge.FirstNodeKey.Value,
            edge.SecondNodeKey.Value, edge.Weight, edge.Kind.ToString())).ToArray();
        return new("available", nodes, edges, feature.ProtectContinuation(snapshot.NextCursor, graphArea?.Id),
            graphArea?.Id, graphArea?.Label);
    }
}

/// <summary>Browser-safe topology and an optional configured reader area label; no source text or authority is exposed.</summary>
public sealed record MemoryGraphApiResponse(
    string Status,
    IReadOnlyList<MemoryGraphNodeDto> Nodes,
    IReadOnlyList<MemoryGraphEdgeDto> Edges,
    string? NextToken,
    string? AreaId = null,
    string? AreaLabel = null)
{
    public static MemoryGraphApiResponse Unavailable { get; } = new("unavailable", [], [], null);
    public static MemoryGraphApiResponse Changed { get; } = new("changed", [], [], null);
    public static MemoryGraphApiResponse Stale { get; } = new("stale", [], [], null);
}

/// <summary>Opaque topology node; Href is absent when no configured reader area matches the fixed graph.</summary>
public sealed record MemoryGraphNodeDto(
    string Id,
    string? Href,
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
