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
    public const string TagRoute = "/api/memory-reader/tags";
    public const string AreasRoute = "/api/memory-reader/areas";
    public const string DocumentRoute = "/api/memory-reader/{routeKey}";

    public static IResult HandleAreas(HttpContext context, IHostEnvironment environment, LocalMemoryReaderFeature feature)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryReaderAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress))
            return new MemoryReaderAreasJsonResult(new MemoryReaderAreasApiResponse("unavailable", []));
        return new MemoryReaderAreasJsonResult(new MemoryReaderAreasApiResponse("available", feature.Areas));
    }


    public static async Task<IResult> HandleHomeAsync(
        HttpContext context,
        IHostEnvironment environment,
        LocalMemoryReaderFeature feature,
        CancellationToken cancellationToken,
        string? area = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryReaderAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress))
            return Json(MemoryReaderApiResponse.Unavailable);

        if (!feature.TryResolveArea(area, out var resolvedArea)) return Json(MemoryReaderApiResponse.Unavailable);
        try
        {
            return Json(ToApiResponse(await feature.ReadHomeAsync(resolvedArea, cancellationToken).ConfigureAwait(false), feature, resolvedArea));
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
        string? search,
        IHostEnvironment environment,
        LocalMemoryReaderFeature feature,
        CancellationToken cancellationToken,
        string? area = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryReaderAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress))
            return CatalogJson(MemoryReaderCatalogApiResponse.Unavailable);
        if (!feature.TryResolveArea(area, out var resolvedArea)) return CatalogJson(MemoryReaderCatalogApiResponse.Unavailable);
        MemoryReaderCatalogCursor? cursor = null;
        MemoryReaderCatalogFilter filter;
        if (!string.IsNullOrWhiteSpace(continuation))
        {
            if (!feature.TryUnprotectCatalogContinuation(continuation, @namespace, tag, search, resolvedArea, out cursor, out filter))
                return CatalogJson(MemoryReaderCatalogApiResponse.Stale);
        }
        else if (!MemoryReaderCatalogQueryService.TryNormalizeFilter(@namespace, tag, search, out filter))
        {
            return CatalogJson(MemoryReaderCatalogApiResponse.Stale);
        }
        try
        {
            var page = await feature.BrowseAsync(cursor, filter, resolvedArea, cancellationToken).ConfigureAwait(false);
            // A tag locator is meaningful only inside its selected area and immutable catalog generation.
            if (page.State == MemoryReaderCatalogState.Available && filter.Tag is not null && !page.Tags.Any(facet => string.Equals(facet.Locator, filter.Tag, StringComparison.Ordinal)))
                return CatalogJson(MemoryReaderCatalogApiResponse.Stale);
            return CatalogJson(ToCatalogApiResponse(page, feature, filter, resolvedArea));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(MemoryReaderEndpoint))
                .LogWarning(exception, "Local memory reader catalog read failed.");
            return CatalogJson(MemoryReaderCatalogApiResponse.Unavailable);
        }
    }

    public static async Task<IResult> HandleTagsAsync(HttpContext context, string? continuation, IHostEnvironment environment, LocalMemoryReaderFeature feature, CancellationToken cancellationToken, string? area = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryReaderAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress)) return TagJson(MemoryReaderTagApiResponse.Unavailable);
        if (!feature.TryResolveArea(area, out var resolvedArea)) return TagJson(MemoryReaderTagApiResponse.Unavailable);
        var start = 0;
        string? continuationGeneration = null;
        if (!string.IsNullOrWhiteSpace(continuation) && !feature.TryUnprotectTagContinuation(continuation, resolvedArea, out continuationGeneration, out start))
            return TagJson(MemoryReaderTagApiResponse.Changed);
        try
        {
            var page = await feature.BrowseAsync(null, MemoryReaderCatalogFilter.Empty, resolvedArea, cancellationToken).ConfigureAwait(false);
            if (page.State != MemoryReaderCatalogState.Available) return TagJson(MemoryReaderTagApiResponse.Unavailable);
            if (string.IsNullOrWhiteSpace(page.GenerationKey) || (continuationGeneration is not null && !string.Equals(continuationGeneration, page.GenerationKey, StringComparison.Ordinal)))
                return TagJson(MemoryReaderTagApiResponse.Changed);
            const int size = 12;
            var tags = page.Tags.Skip(start).Take(size).Select(tag => new MemoryReaderCatalogFacetDto(tag.Locator, tag.Label, tag.Count)).ToArray();
            var next = start + tags.Length < page.Tags.Count ? feature.ProtectTagContinuation(page.GenerationKey, start + tags.Length, resolvedArea) : null;
            return TagJson(new("available", tags, next));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return TagJson(MemoryReaderTagApiResponse.Unavailable); }
    }

    public static async Task<IResult> HandleTreeAsync(
        HttpContext context,
        string? continuation,
        IHostEnvironment environment,
        LocalMemoryReaderFeature feature,
        CancellationToken cancellationToken,
        string? area = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryReaderAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress))
            return TreeJson(MemoryReaderTreeApiResponse.Unavailable);
        if (!feature.TryResolveArea(area, out var resolvedArea)) return TreeJson(MemoryReaderTreeApiResponse.Unavailable);
        var start = 0;
        string? continuationGeneration = null;
        if (!string.IsNullOrWhiteSpace(continuation) && !feature.TryUnprotectTreeContinuation(continuation, resolvedArea, out continuationGeneration, out start))
            return TreeJson(MemoryReaderTreeApiResponse.Changed);
        try
        {
            var page = await feature.ReadTreeAsync(resolvedArea, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(page.GenerationKey) || (continuationGeneration is not null && !string.Equals(continuationGeneration, page.GenerationKey, StringComparison.Ordinal)))
                return TreeJson(MemoryReaderTreeApiResponse.Changed);
            return TreeJson(ToTreeApiResponse(page, feature, resolvedArea, start));
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
        CancellationToken cancellationToken,
        string? area = null)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!MemoryReaderAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress))
            return Json(MemoryReaderApiResponse.Unavailable);
        if (!feature.TryResolveArea(area, out var resolvedArea) || string.IsNullOrWhiteSpace(routeKey) || routeKey.Length > 128)
            return Json(MemoryReaderApiResponse.NotFound);

        string? blockKey = null;
        MemoryReaderBlockCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(block) && !feature.TryUnprotectNavigation(routeKey, block, resolvedArea, out blockKey, out cursor))
            return Json(MemoryReaderApiResponse.Stale);

        try
        {
            var page = await feature.ReadDocumentAsync(routeKey, blockKey, cursor, resolvedArea, cancellationToken).ConfigureAwait(false);
            if (page.State != MemoryReaderDocumentState.Available)
                return Json(ToApiResponse(page, feature, resolvedArea));
            var snapshot = await feature.ReadDocumentWikiSnapshotAsync(routeKey, page.RecordVersion ?? 0, resolvedArea, cancellationToken).ConfigureAwait(false);
            return snapshot is null
                ? Json(MemoryReaderApiResponse.Changed)
                : Json(ToApiResponse(page, feature, resolvedArea, snapshot.Metadata, snapshot.Children, snapshot.Related, snapshot.Backlinks));
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
    private static IResult TagJson(MemoryReaderTagApiResponse response) => new MemoryReaderTagJsonResult(response);

    private static MemoryReaderTreeApiResponse ToTreeApiResponse(MemoryReaderTreePage page, LocalMemoryReaderFeature feature, string area, int start = 0) => page.Status switch
    {
        "available" => TreeSlice(page, feature, area, start),
        "catalog-not-ready" => MemoryReaderTreeApiResponse.NotReady,
        _ => MemoryReaderTreeApiResponse.Unavailable
    };

    private static MemoryReaderTreeApiResponse TreeSlice(MemoryReaderTreePage page, LocalMemoryReaderFeature feature, string area, int start)
    {
        var flattened = Flatten(page.Roots).ToArray();
        const int size = 32;
        // A continuation is a flat reader list: returning descendants here would duplicate them on later portions.
        var roots = flattened.Skip(start).Take(size).Select(node => new MemoryReaderTreeNodeDto(
            node.Node.Kind, node.Node.Label, node.Node.Locator, node.Node.Href is null ? null : WithArea(node.Node.Href, area), node.Node.LinkWeight, node.Node.ItemCount, [], node.Key, node.ParentKey, node.Depth)).ToArray();
        var next = start + roots.Length < flattened.Length ? feature.ProtectTreeContinuation(page.GenerationKey!, start + roots.Length, area) : null;
        return new("available", roots, next);
    }

    private static IEnumerable<(MemoryReaderTreeNode Node, string Key, string? ParentKey, int Depth)> Flatten(IEnumerable<MemoryReaderTreeNode> nodes, string? parentKey = null, int depth = 0)
    {
        var index = 0;
        foreach (var node in nodes)
        {
            var key = parentKey is null ? $"n{index}" : $"{parentKey}.{index}";
            yield return (node, key, parentKey, depth);
            foreach (var child in Flatten(node.Children, key, depth + 1)) yield return child;
            index++;
        }
    }

    private static MemoryReaderTreeNodeDto ToTreeNodeDto(MemoryReaderTreeNode node) =>
        new(node.Kind, node.Label, node.Locator, node.Href, node.LinkWeight, node.ItemCount, node.Children.Select(ToTreeNodeDto).ToArray());

    private static MemoryReaderApiResponse ToApiResponse(
        MemoryReaderDocumentPage page,
        LocalMemoryReaderFeature feature,
        string area,
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
            ToRelationDtos(children, area), ToRelationDtos(related, area), ToRelationDtos(backlinks, area),
            page.Blocks.Select(block => new MemoryReaderBlockDto(
                feature.ProtectBlock(page.RouteKey, block.BlockKey, area),
                block.Heading,
                block.Content.Select(inline => inline.Kind == MemoryReaderInlineKind.Link &&
                                             inline.RouteKey is not null && inline.BlockKey is not null
                    ? new MemoryReaderInlineDto("link", inline.Text, inline.RouteKey, feature.ProtectBlock(inline.RouteKey, inline.BlockKey, area))
                    : new MemoryReaderInlineDto("text", inline.Text, null, null)).ToArray())).ToArray(),
                feature.ProtectContinuation(page.RouteKey, page.NextCursor, area)),
        MemoryReaderDocumentState.NotFound => MemoryReaderApiResponse.NotFound,
        MemoryReaderDocumentState.Changed => MemoryReaderApiResponse.Changed,
        _ => MemoryReaderApiResponse.Unavailable
    };

    private static IReadOnlyList<MemoryReaderRelationDto> ToRelationDtos(IReadOnlyList<LocalMemoryReaderFeature.MemoryReaderWikiRelation>? relations, string? area = null) =>
        (relations ?? []).Select(relation => new MemoryReaderRelationDto(WithArea(relation.Href, area), relation.Kind, relation.Label, relation.Title, relation.Namespace, relation.SharedEntityCount)).ToArray();

    private static MemoryReaderCatalogApiResponse ToCatalogApiResponse(
        MemoryReaderCatalogPage page,
        LocalMemoryReaderFeature feature,
        MemoryReaderCatalogFilter filter,
        string area) => page.State switch
    {
        MemoryReaderCatalogState.Available => new("available", page.Documents.Select(document => new MemoryReaderCatalogDocumentDto(
            WithArea(document.RecordHref, area), document.Type.ToString(), document.Title, document.Namespace, document.Tags,
            document.Preview, document.UpdatedAt)).ToArray(),
            page.Namespaces.Select(facet => new MemoryReaderCatalogFacetDto(facet.Locator, facet.Label, facet.Count)).ToArray(),
            page.Tags.Select(facet => new MemoryReaderCatalogFacetDto(facet.Locator, facet.Label, facet.Count)).ToArray(),
            feature.ProtectCatalogContinuation(page.NextCursor, filter, area)),
        MemoryReaderCatalogState.NotFound => MemoryReaderCatalogApiResponse.NotFound,
        MemoryReaderCatalogState.Changed => MemoryReaderCatalogApiResponse.Changed,
        MemoryReaderCatalogState.Stale => MemoryReaderCatalogApiResponse.Stale,
        MemoryReaderCatalogState.CatalogNotReady => MemoryReaderCatalogApiResponse.NotReady,
        _ => MemoryReaderCatalogApiResponse.Unavailable
    };

    private static string WithArea(string href, string? area) => string.IsNullOrWhiteSpace(area)
        ? href
        : string.Concat(href, href.Contains('?', StringComparison.Ordinal) ? "&area=" : "?area=", Uri.EscapeDataString(area));
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

public sealed record MemoryReaderAreasApiResponse(string Status, IReadOnlyList<MemoryReaderAreaDto> Areas);

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
    IReadOnlyList<MemoryReaderTreeNodeDto> Children,
    string? NodeKey = null,
    string? ParentKey = null,
    int Depth = 0);

public sealed record MemoryReaderTreeApiResponse(string Status, IReadOnlyList<MemoryReaderTreeNodeDto> Roots, string? NextToken = null)
{
    public static MemoryReaderTreeApiResponse Unavailable { get; } = new("unavailable", []);
    public static MemoryReaderTreeApiResponse NotReady { get; } = new("catalog-not-ready", []);
    public static MemoryReaderTreeApiResponse Changed { get; } = new("changed", []);
}

public sealed record MemoryReaderTagApiResponse(string Status, IReadOnlyList<MemoryReaderCatalogFacetDto> Tags, string? NextToken)
{
    public static MemoryReaderTagApiResponse Unavailable { get; } = new("unavailable", [], null);
    public static MemoryReaderTagApiResponse Changed { get; } = new("changed", [], null);
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

internal sealed class MemoryReaderAreasJsonResult(MemoryReaderAreasApiResponse response) : IResult
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, response, JsonOptions, context.RequestAborted).ConfigureAwait(false);
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

internal sealed class MemoryReaderTagJsonResult(MemoryReaderTagApiResponse response) : IResult
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, response, response.GetType(), JsonOptions, context.RequestAborted).ConfigureAwait(false);
    }
}
