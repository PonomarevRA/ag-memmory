using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.Core;

namespace AgMemory.Web.Features.MemoryReader;

/// <summary>Same-origin local reader API. Authority is entirely server-configured.</summary>
public static class MemoryReaderEndpoint
{
    public const string CatalogRoute = "/api/memory-reader";
    public const string HomeRoute = "/api/memory-reader/home";
    public const string TreeRoute = "/api/memory-reader/tree";
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
        string? @namespace,
        string? tag,
        IHostEnvironment environment,
        LocalMemoryReaderFeature feature,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryReaderAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress))
            return CatalogJson(MemoryReaderCatalogApiResponse.Unavailable);
        MemoryReaderCatalogCursor? cursor = null;
        MemoryReaderCatalogFilter filter;
        if (!string.IsNullOrWhiteSpace(continuation))
        {
            if (!feature.TryUnprotectCatalogContinuation(continuation, @namespace, tag, out cursor, out filter))
                return CatalogJson(MemoryReaderCatalogApiResponse.Stale);
        }
        else if (!MemoryReaderCatalogQueryService.TryNormalizeFilter(@namespace, tag, out filter))
        {
            return CatalogJson(MemoryReaderCatalogApiResponse.Stale);
        }
        try
        {
            var page = await feature.BrowseAsync(cursor, filter, cancellationToken).ConfigureAwait(false);
            return CatalogJson(ToCatalogApiResponse(page, feature, filter));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return CatalogJson(MemoryReaderCatalogApiResponse.Unavailable); }
    }

    public static async Task<IResult> HandleTreeAsync(
        HttpContext context,
        IHostEnvironment environment,
        LocalMemoryReaderFeature feature,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryReaderAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress))
            return TreeJson(MemoryReaderTreeApiResponse.Unavailable);
        try
        {
            var page = await feature.ReadTreeAsync(cancellationToken).ConfigureAwait(false);
            return TreeJson(ToTreeApiResponse(page));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return TreeJson(MemoryReaderTreeApiResponse.Unavailable); }
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
            if (page.State != MemoryReaderDocumentState.Available)
                return Json(ToApiResponse(page, feature));
            var snapshot = await feature.ReadDocumentWikiSnapshotAsync(routeKey, page.RecordVersion ?? 0, cancellationToken).ConfigureAwait(false);
            return snapshot is null
                ? Json(MemoryReaderApiResponse.Changed)
                : Json(ToApiResponse(page, feature, snapshot.Metadata, snapshot.Children, snapshot.Related, snapshot.Backlinks));
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
    private static IResult TreeJson(MemoryReaderTreeApiResponse response) => new MemoryReaderTreeJsonResult(response);

    private static MemoryReaderTreeApiResponse ToTreeApiResponse(MemoryReaderTreePage page) => page.Status switch
    {
        "available" => new("available", page.Roots.Select(ToTreeNodeDto).ToArray()),
        "catalog-not-ready" => MemoryReaderTreeApiResponse.NotReady,
        _ => MemoryReaderTreeApiResponse.Unavailable
    };

    private static MemoryReaderTreeNodeDto ToTreeNodeDto(MemoryReaderTreeNode node) =>
        new(node.Kind, node.Label, node.Locator, node.Href, node.LinkWeight, node.ItemCount, node.Children.Select(ToTreeNodeDto).ToArray());

    private static MemoryReaderApiResponse ToApiResponse(
        MemoryReaderDocumentPage page,
        LocalMemoryReaderFeature feature,
        MemoryWikiMetadata? metadata = null,
        IReadOnlyList<LocalMemoryReaderFeature.MemoryReaderWikiRelation>? children = null,
        IReadOnlyList<LocalMemoryReaderFeature.MemoryReaderWikiRelation>? related = null,
        IReadOnlyList<LocalMemoryReaderFeature.MemoryReaderWikiRelation>? backlinks = null) => page.State switch
    {
        MemoryReaderDocumentState.Available => new(
            "available",
            page.RouteKey,
            metadata?.Title ?? page.Blocks.FirstOrDefault(block => !string.IsNullOrWhiteSpace(block.Heading))?.Heading ?? "Запись памяти",
            metadata?.Namespace ?? "inbox",
            metadata?.Tags ?? [],
            ToRelationDtos(children), ToRelationDtos(related), ToRelationDtos(backlinks),
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

    private static IReadOnlyList<MemoryReaderRelationDto> ToRelationDtos(IReadOnlyList<LocalMemoryReaderFeature.MemoryReaderWikiRelation>? relations) =>
        (relations ?? []).Select(relation => new MemoryReaderRelationDto(relation.Href, relation.Kind, relation.Label, relation.Title, relation.Namespace, relation.SharedEntityCount)).ToArray();

    private static MemoryReaderCatalogApiResponse ToCatalogApiResponse(
        MemoryReaderCatalogPage page,
        LocalMemoryReaderFeature feature,
        MemoryReaderCatalogFilter filter) => page.State switch
    {
        MemoryReaderCatalogState.Available => new("available", page.Documents.Select(document => new MemoryReaderCatalogDocumentDto(
            document.RecordHref, document.Type.ToString(), document.Title, document.Namespace, document.Tags,
            document.Preview, document.UpdatedAt)).ToArray(),
            page.Namespaces.Select(facet => new MemoryReaderCatalogFacetDto(facet.Locator, facet.Label, facet.Count)).ToArray(),
            page.Tags.Select(facet => new MemoryReaderCatalogFacetDto(facet.Locator, facet.Label, facet.Count)).ToArray(),
            feature.ProtectCatalogContinuation(page.NextCursor, filter)),
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
    string? Title,
    string? Namespace,
    IReadOnlyList<string> Tags,
    IReadOnlyList<MemoryReaderRelationDto> Children,
    IReadOnlyList<MemoryReaderRelationDto> Related,
    IReadOnlyList<MemoryReaderRelationDto> Backlinks,
    IReadOnlyList<MemoryReaderBlockDto> Blocks,
    string? NextBlockToken)
{
    public static MemoryReaderApiResponse Unavailable { get; } = new("unavailable", null, null, null, [], [], [], [], [], null);
    public static MemoryReaderApiResponse NotFound { get; } = new("not-found", null, null, null, [], [], [], [], [], null);
    public static MemoryReaderApiResponse Changed { get; } = new("changed", null, null, null, [], [], [], [], [], null);
    public static MemoryReaderApiResponse Stale { get; } = new("stale", null, null, null, [], [], [], [], [], null);
}

public sealed record MemoryReaderBlockDto(string Id, string? Heading, IReadOnlyList<MemoryReaderInlineDto> Content);
public sealed record MemoryReaderInlineDto(string Kind, string Text, string? RouteKey, string? BlockToken);
public sealed record MemoryReaderRelationDto(string Href, string Kind, string Label, string Title, string Namespace, int SharedEntityCount);

public sealed record MemoryReaderTreeNodeDto(
    string Kind,
    string Label,
    string? Locator,
    string? Href,
    int? LinkWeight,
    int? ItemCount,
    IReadOnlyList<MemoryReaderTreeNodeDto> Children);

public sealed record MemoryReaderTreeApiResponse(string Status, IReadOnlyList<MemoryReaderTreeNodeDto> Roots)
{
    public static MemoryReaderTreeApiResponse Unavailable { get; } = new("unavailable", []);
    public static MemoryReaderTreeApiResponse NotReady { get; } = new("catalog-not-ready", []);
}

public sealed record MemoryReaderCatalogApiResponse(
    string Status,
    IReadOnlyList<MemoryReaderCatalogDocumentDto> Documents,
    IReadOnlyList<MemoryReaderCatalogFacetDto> Namespaces,
    IReadOnlyList<MemoryReaderCatalogFacetDto> Tags,
    string? NextToken)
{
    public static MemoryReaderCatalogApiResponse Unavailable { get; } = new("unavailable", [], [], [], null);
    public static MemoryReaderCatalogApiResponse NotFound { get; } = new("not-found", [], [], [], null);
    public static MemoryReaderCatalogApiResponse Changed { get; } = new("changed", [], [], [], null);
    public static MemoryReaderCatalogApiResponse Stale { get; } = new("stale", [], [], [], null);
    public static MemoryReaderCatalogApiResponse NotReady { get; } = new("catalog-not-ready", [], [], [], null);
}

public sealed record MemoryReaderCatalogDocumentDto(
    string Href,
    string Type,
    string Title,
    string Namespace,
    IReadOnlyList<string> Tags,
    string Preview,
    DateTimeOffset UpdatedAt);
public sealed record MemoryReaderCatalogFacetDto(string Locator, string Label, int Count);

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

internal sealed class MemoryReaderTreeJsonResult(MemoryReaderTreeApiResponse response) : IResult
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, response, response.GetType(), JsonOptions,
            context.RequestAborted).ConfigureAwait(false);
    }
}
