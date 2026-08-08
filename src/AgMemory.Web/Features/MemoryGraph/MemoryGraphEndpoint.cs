using System.Net;
using System.Security.Cryptography;
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
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryGraphAccessPolicy.AllowsStoreAccess(
                feature.IsConfigured,
                environment.IsDevelopment(),
                context.Connection.RemoteIpAddress))
            return Json(MemoryGraphApiResponse.Unavailable);

        try
        {
            var snapshot = await feature.ReadAsync(cancellationToken).ConfigureAwait(false);
            return snapshot.Error is null ? Json(ToApiResponse(snapshot)) : Json(MemoryGraphApiResponse.Unavailable);
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

    private static MemoryGraphApiResponse ToApiResponse(MemoryGraphSnapshot snapshot)
    {
        var opaqueIds = snapshot.Nodes.ToDictionary(node => node.MemoryId, _ => OpaqueId());
        var ordinalByType = new Dictionary<MemoryRecordType, int>();
        var nodes = snapshot.Nodes.Select(node =>
        {
            var ordinal = ordinalByType.GetValueOrDefault(node.Type) + 1;
            ordinalByType[node.Type] = ordinal;
            return new MemoryGraphNodeDto(
                opaqueIds[node.MemoryId],
                $"{node.Type} {ordinal}",
                node.Type.ToString(),
                node.ImportanceBand,
                node.ConfidenceBand,
                node.Degree);
        }).ToArray();
        var edges = snapshot.Edges
            .Where(edge => opaqueIds.ContainsKey(edge.FirstMemoryId) && opaqueIds.ContainsKey(edge.SecondMemoryId))
            .Select(edge => new MemoryGraphEdgeDto(
                opaqueIds[edge.FirstMemoryId],
                opaqueIds[edge.SecondMemoryId],
                edge.Weight,
                edge.Kind.ToString()))
            .ToArray();
        return new("available", nodes, edges);
    }

    private static string OpaqueId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

/// <summary>Browser-safe graph response. It deliberately has no scope, actor, durable ID, text or error field.</summary>
public sealed record MemoryGraphApiResponse(
    string Status,
    IReadOnlyList<MemoryGraphNodeDto> Nodes,
    IReadOnlyList<MemoryGraphEdgeDto> Edges)
{
    public static MemoryGraphApiResponse Unavailable { get; } = new("unavailable", [], []);
}

public sealed record MemoryGraphNodeDto(
    string Id,
    string Label,
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
