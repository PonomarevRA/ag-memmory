using AgMemory.Contracts;
using AgMemory.Web.Features.MemoryReader;

namespace AgMemory.Web.Features.MemoryExperience;

/// <summary>Gated local operational list. It deliberately has no raw-text or record-by-id route.</summary>
public static class MemoryRecordBrowserEndpoint
{
    public const string Route = "/api/memory-records";
    public static async Task<IResult> HandleAsync(HttpContext context, string? continuation, string? type, string? @namespace, string? tag, string? search, string? sort,
        string? area, IHostEnvironment environment, Microsoft.Extensions.Options.IOptions<MemoryExperienceOptions> options, LocalMemoryReaderFeature feature, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        // Authorisation gate precedes resolver and store composition by design.
        if (!MemoryExperienceAccessPolicy.Allows(options.Value.RecordBrowserEnabled, environment.IsDevelopment(), context.Connection.RemoteIpAddress)) return Results.NotFound();
        if (!feature.TryResolveArea(area, out var resolvedArea) || !TryFilter(type, @namespace, tag, search, sort, out var filter)) return Results.NotFound();
        MemoryRecordBrowserCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(continuation) && !feature.TryUnprotectRecordContinuation(continuation, filter, resolvedArea, out cursor)) return Results.NotFound();
        var page = await feature.BrowseRecordsAsync(cursor, filter, resolvedArea, cancellationToken).ConfigureAwait(false);
        return page.State == MemoryRecordBrowserState.Available
            ? Results.Json(new Response("available", page.Records.Select(item => new Item(item.Type.ToString(), item.Title, item.Namespace, item.Tags, item.Preview, item.UpdatedAt)).ToArray(), feature.ProtectRecordContinuation(page.NextCursor, filter, resolvedArea)))
            : Results.NotFound();
    }
    private static bool TryFilter(string? type, string? ns, string? tag, string? search, string? sort, out MemoryRecordBrowserFilter filter)
    {
        filter = default!;
        MemoryRecordType? parsedType = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<MemoryRecordType>(type, true, out var parsed)) return false;
            parsedType = parsed;
        }
        var parsedSort = string.Equals(sort, "updated-asc", StringComparison.OrdinalIgnoreCase) ? MemoryRecordBrowserSort.UpdatedAscending : MemoryRecordBrowserSort.UpdatedDescending;
        if (sort is not null && !string.Equals(sort, "updated-asc", StringComparison.OrdinalIgnoreCase) && !string.Equals(sort, "updated-desc", StringComparison.OrdinalIgnoreCase)) return false;
        if (search?.Length > MemoryRecordBrowserLimits.MaximumSearchCharacters || !Valid(ns) || !Valid(tag)) return false;
        filter = new(parsedType, Empty(ns), Empty(tag), Empty(search), parsedSort); return true;
    }
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool Valid(string? value) => value is null || (value.Length is > 0 and <= 120 && value == value.Trim());
    private sealed record Response(string Status, IReadOnlyList<Item> Records, string? Continuation);
    private sealed record Item(string Type, string Title, string Namespace, IReadOnlyList<string> Tags, string Preview, DateTimeOffset UpdatedAt);
}
