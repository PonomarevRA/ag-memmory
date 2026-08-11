using System.Text.Json;
using AgMemory.Contracts;

namespace AgMemory.Web.Features.MemoryReader;

/// <summary>Same-origin local reader API. Authority is entirely server-configured.</summary>
public static class MemoryReaderEndpoint
{
    public const string CatalogRoute = "/api/memory-reader";
    public const string HomeRoute = "/api/memory-reader/home";
    public const string DocumentRoute = "/api/memory-reader/{routeKey}";

    public static async Task<IResult> HandleHomeAsync(
        HttpContext context,
        IHostEnvironment environment,
        LocalMemoryReaderFeature feature,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryReaderAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress))
            return Json(MemoryReaderApiResponse.Unavailable);

        try
        {
            return Json(ToApiResponse(await feature.ReadHomeAsync(cancellationToken).ConfigureAwait(false), feature));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Json(MemoryReaderApiResponse.Unavailable);
        }
    }

    public static async Task<IResult> HandleCatalogAsync(
        HttpContext context,
        string? continuation,
        IHostEnvironment environment,
        LocalMemoryReaderFeature feature,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryReaderAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress))
            return CatalogJson(MemoryReaderCatalogApiResponse.Unavailable);
        MemoryReaderCatalogCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(continuation) && !feature.TryUnprotectCatalogContinuation(continuation, out cursor))
            return CatalogJson(MemoryReaderCatalogApiResponse.Stale);
        try
        {
            var page = await feature.BrowseAsync(cursor, cancellationToken).ConfigureAwait(false);
            return CatalogJson(ToCatalogApiResponse(page, feature));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return CatalogJson(MemoryReaderCatalogApiResponse.Unavailable); }
    }

    public static async Task<IResult> HandleDocumentAsync(
        HttpContext context,
        string routeKey,
        string? block,
        IHostEnvironment environment,
        LocalMemoryReaderFeature feature,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryReaderAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress))
            return Json(MemoryReaderApiResponse.Unavailable);
        if (string.IsNullOrWhiteSpace(routeKey) || routeKey.Length > 128)
            return Json(MemoryReaderApiResponse.NotFound);

        string? blockKey = null;
        MemoryReaderBlockCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(block) && !feature.TryUnprotectNavigation(routeKey, block, out blockKey, out cursor))
            return Json(MemoryReaderApiResponse.Stale);

        try
        {
            var page = await feature.ReadDocumentAsync(routeKey, blockKey, cursor, cancellationToken).ConfigureAwait(false);
            return Json(ToApiResponse(page, feature));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Json(MemoryReaderApiResponse.Unavailable);
        }
    }

    private static IResult Json(MemoryReaderApiResponse response) => new MemoryReaderJsonResult(response);
    private static IResult CatalogJson(MemoryReaderCatalogApiResponse response) => new MemoryReaderCatalogJsonResult(response);

    private static MemoryReaderApiResponse ToApiResponse(MemoryReaderDocumentPage page, LocalMemoryReaderFeature feature) => page.State switch
    {
        MemoryReaderDocumentState.Available => new(
            "available",
            page.RouteKey,
            page.Blocks.Select(block => new MemoryReaderBlockDto(
                feature.ProtectBlock(page.RouteKey, block.BlockKey),
                block.Heading,
                block.Content.Select(inline => inline.Kind == MemoryReaderInlineKind.Link &&
                                             inline.RouteKey is not null && inline.BlockKey is not null
                    ? new MemoryReaderInlineDto("link", inline.Text, inline.RouteKey, feature.ProtectBlock(inline.RouteKey, inline.BlockKey))
                    : new MemoryReaderInlineDto("text", inline.Text, null, null)).ToArray())).ToArray(),
            feature.ProtectContinuation(page.RouteKey, page.NextCursor)),
        MemoryReaderDocumentState.NotFound => MemoryReaderApiResponse.NotFound,
        MemoryReaderDocumentState.Changed => MemoryReaderApiResponse.Changed,
        _ => MemoryReaderApiResponse.Unavailable
    };

    private static MemoryReaderCatalogApiResponse ToCatalogApiResponse(MemoryReaderCatalogPage page, LocalMemoryReaderFeature feature) => page.State switch
    {
        MemoryReaderCatalogState.Available => new("available", page.Documents.Select(document => new MemoryReaderCatalogDocumentDto(
            document.RecordHref, document.Type.ToString(), document.Preview, document.UpdatedAt)).ToArray(), feature.ProtectCatalogContinuation(page.NextCursor)),
        MemoryReaderCatalogState.NotFound => MemoryReaderCatalogApiResponse.NotFound,
        MemoryReaderCatalogState.Changed => MemoryReaderCatalogApiResponse.Changed,
        MemoryReaderCatalogState.Stale => MemoryReaderCatalogApiResponse.Stale,
        MemoryReaderCatalogState.CatalogNotReady => MemoryReaderCatalogApiResponse.NotReady,
        _ => MemoryReaderCatalogApiResponse.Unavailable
    };
}

/// <summary>Content delivery is intentional; this DTO still omits scope, actor, provenance and durable IDs.</summary>
public sealed record MemoryReaderApiResponse(
    string Status,
    string? RouteKey,
    IReadOnlyList<MemoryReaderBlockDto> Blocks,
    string? NextBlockToken)
{
    public static MemoryReaderApiResponse Unavailable { get; } = new("unavailable", null, [], null);
    public static MemoryReaderApiResponse NotFound { get; } = new("not-found", null, [], null);
    public static MemoryReaderApiResponse Changed { get; } = new("changed", null, [], null);
    public static MemoryReaderApiResponse Stale { get; } = new("stale", null, [], null);
}

public sealed record MemoryReaderBlockDto(string Id, string? Heading, IReadOnlyList<MemoryReaderInlineDto> Content);
public sealed record MemoryReaderInlineDto(string Kind, string Text, string? RouteKey, string? BlockToken);

public sealed record MemoryReaderCatalogApiResponse(
    string Status,
    IReadOnlyList<MemoryReaderCatalogDocumentDto> Documents,
    string? NextToken)
{
    public static MemoryReaderCatalogApiResponse Unavailable { get; } = new("unavailable", [], null);
    public static MemoryReaderCatalogApiResponse NotFound { get; } = new("not-found", [], null);
    public static MemoryReaderCatalogApiResponse Changed { get; } = new("changed", [], null);
    public static MemoryReaderCatalogApiResponse Stale { get; } = new("stale", [], null);
    public static MemoryReaderCatalogApiResponse NotReady { get; } = new("catalog-not-ready", [], null);
}

public sealed record MemoryReaderCatalogDocumentDto(string Href, string Type, string Preview, DateTimeOffset UpdatedAt);

internal sealed class MemoryReaderJsonResult(MemoryReaderApiResponse response) : IResult
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, response, response.GetType(), JsonOptions,
            context.RequestAborted).ConfigureAwait(false);
    }
}

internal sealed class MemoryReaderCatalogJsonResult(MemoryReaderCatalogApiResponse response) : IResult
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, response, response.GetType(), JsonOptions,
            context.RequestAborted).ConfigureAwait(false);
    }
}
