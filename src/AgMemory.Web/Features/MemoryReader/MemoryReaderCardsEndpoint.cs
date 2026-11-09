using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.Core;

namespace AgMemory.Web.Features.MemoryReader;

/// <summary>Content-bearing card delivery owned by the configured reader, separate from topology-only graph delivery.</summary>
public static class MemoryReaderCardsEndpoint
{
    public const string Route = "/api/memory-reader/cards";

    /// <summary>Requires an explicit configured area and bounded opaque routes; unavailable responses expose no lookup diagnostics.</summary>
    public static async Task<IResult> HandleAsync(HttpContext context, string? routes, string? area,
        IHostEnvironment environment, LocalMemoryReaderFeature feature, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var empty = new MemoryReaderCardsResponse("unavailable", null, []);
        if (!MemoryReaderAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress) ||
            string.IsNullOrEmpty(area) || !feature.TryResolveArea(area, out var resolvedArea) ||
            routes is not { Length: > 0 and <= 3224 }) return Json(empty);
        var keys = routes.Split(',');
        if (keys.Length > MemoryReaderCardLimits.MaximumCards || keys.Any(key => !MemoryReaderCardLimits.IsRouteKey(key))) return Json(empty);
        try
        {
            var sources = await feature.ReadCardsAsync(keys, resolvedArea, cancellationToken).ConfigureAwait(false);
            var cards = new List<MemoryReaderCard>(sources.Count);
            foreach (var source in sources)
            {
                var text = MemoryReaderCardText.Format(source.CanonicalText, MemoryReaderCardLimits.MaximumTitleCharacters, MemoryReaderCardLimits.MaximumPreviewCharacters);
                cards.Add(new(source.RouteKey, text.Title, text.Preview, source.Version));
            }
            return Json(new MemoryReaderCardsResponse("available", resolvedArea, cards));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Json(empty); }
    }

    private static IResult Json(MemoryReaderCardsResponse response) => Results.Json(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

/// <summary>Current reader text, not a topology-generation promise or authorization token.</summary>
public sealed record MemoryReaderCardsResponse(string Status, string? AreaId, IReadOnlyList<MemoryReaderCard> Cards);
