using System.Text.Json;

namespace AgMemory.Web.Features.TechnicalLogs;

/// <summary>Independent read-only native technical diagnostics. No consumer or observer dependency is registered.</summary>
public static class TechnicalLogEndpoint
{
    public const string Prefix = "/api/technical-logs/v1";
    public const string Schema = "technical-logs-http/v1";
    private const int MaxPageBytes = 262_144;

    /// <summary>Registers only the two versioned read routes and their response metadata.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet(Prefix + "/status", Status)
            .WithName("GetTechnicalLogHttpStatusV1").WithSummary("Read durable technical log status")
            .Produces<TechnicalLogStatus>().Produces<TechnicalLogError>(400).Produces<TechnicalLogError>(403).Produces<TechnicalLogError>(503);
        app.MapGet(Prefix + "/events", EventsAsync)
            .WithName("GetTechnicalLogHttpEventsV1").WithSummary("Read a bounded page of technical diagnostics")
            .Produces<TechnicalLogPage>().Produces<TechnicalLogError>(400).Produces<TechnicalLogError>(403)
            .Produces<TechnicalLogError>(409).Produces<TechnicalLogError>(503);
    }

    /// <summary>Returns a durable head only when the configured journal is readable; queue gauges are intentionally unknown.</summary>
    private static IResult Status(HttpContext context, IHostEnvironment environment, TechnicalLogProvider feature)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Allowed(context, environment)) return Error(403);
        if (context.Request.Query.Count != 0) return Error(400);
        try { return Results.Json(feature.Status(), TechnicalLogJson.Options); }
        catch { return Error(503); }
    }

    /// <summary>Validates the cursor against the exact read window and bounds the complete serialized page without skipping matching events.</summary>
    private static async Task<IResult> EventsAsync(HttpContext context, IHostEnvironment environment,
        TechnicalLogProvider feature, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Allowed(context, environment)) return Error(403);
        if (!TechnicalLogQueryParser.TryParse(context.Request, out var query)) return Error(400);
        try
        {
            var read = await Task.Run(() => feature.Read(query!), cancellationToken).ConfigureAwait(false);
            if (!query!.IsCurrent(read.Status)) return Error(409, read.Status.Cursor);
            var events = read.Events.ToList();
            var lengths = events.Select(value => JsonSerializer.SerializeToUtf8Bytes(value, TechnicalLogJson.Options).Length).ToList();
            var eventBytes = lengths.Sum();
            var more = read.HasMore;
            var truncated = false;
            TechnicalLogPage page;
            while (true)
            {
                page = Page(query!, read.Status, events, more, truncated);
                var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(page with { Events = [] }, TechnicalLogJson.Options).Length;
                if (envelopeBytes + eventBytes + Math.Max(0, events.Count - 1) <= MaxPageBytes) break;
                if (events.Count <= 1) return Error(503);
                // Trim the traversal tail only: forward keeps its oldest prefix; backward keeps its newest suffix.
                var index = query!.Forward ? events.Count - 1 : 0;
                eventBytes -= lengths[index]; lengths.RemoveAt(index); events.RemoveAt(index);
                more = true; truncated = true;
            }
            return Results.Json(page, TechnicalLogJson.Options);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Error(503); }
    }

    /// <summary>Computes exclusive continuation from the retained page, never from a later status request.</summary>
    private static TechnicalLogPage Page(TechnicalLogQuery query, TechnicalLogStatus status, List<TechnicalLogEvent> events, bool more, bool truncated)
    {
        string next;
        if (query.Forward) next = more && events.Count > 0 ? events[^1].Sequence : status.LatestSequence;
        else next = more && events.Count > 0 ? events[0].Sequence : TechnicalLogJson.Number(TechnicalLogJson.Sequence(status.OldestSequence) - 1);
        return new(Schema, "agmemory", query.Forward ? "forward" : "backward", events,
            new(status.JournalEpoch, next), status.Cursor, status.OldestSequence, more, truncated, status.Health);
    }

    /// <summary>Retains Development, loopback peer/Host and optional exact Origin without requiring a memory graph.</summary>
    internal static bool Allowed(HttpContext context, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment() || context.Connection.RemoteIpAddress is null || !System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress)) return false;
        var host = context.Request.Host.Host;
        if (host != "localhost" && (!System.Net.IPAddress.TryParse(host, out var address) || !System.Net.IPAddress.IsLoopback(address))) return false;
        if (context.Request.Headers["Sec-Fetch-Site"].ToString() == "cross-site") return false;
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length == 0) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == context.Request.Scheme &&
            string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query);
    }

    /// <summary>Emits only fixed portable messages; a reset head is included exclusively for HTTP 409.</summary>
    private static IResult Error(int status, TechnicalLogCursor? reset = null)
    {
        var (code, message) = status switch
        {
            400 => ("invalid_query", "Invalid query parameters."),
            403 => ("forbidden", "Access forbidden."),
            409 => ("cursor_expired", "Cursor has expired."),
            _ => ("journal_unavailable", "Journal is unavailable.")
        };
        return Results.Json(new TechnicalLogError(Schema, "agmemory", new(code, message, status == 503), reset), TechnicalLogJson.Options, statusCode: status);
    }
}
