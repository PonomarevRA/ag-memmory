using AgMemory.ActivityJournal;
using System.Text.Json;

namespace AgMemory.Web.Features.AgentActivity;

/// <summary>Portable, read-only activity HTTP v1 adapter. Existing mini-chat, WebSocket and MCP contracts are untouched.</summary>
public static class AgentActivityHttpEndpoint
{
    public const string Prefix = "/api/agent-activity/v1";
    public const string Schema = "agent-activity-http/v1";
    private const int MaxPageBytes = 262_144;

    /// <summary>Registers only the two versioned read routes and their response metadata.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet(Prefix + "/status", Status)
            .WithName("GetAgentActivityHttpStatusV1").WithSummary("Read durable activity journal status")
            .Produces<ActivityHttpStatus>().Produces<ActivityHttpError>(400).Produces<ActivityHttpError>(403).Produces<ActivityHttpError>(503);
        app.MapGet(Prefix + "/events", EventsAsync)
            .WithName("GetAgentActivityHttpEventsV1").WithSummary("Read a bounded page of permitted activity")
            .Produces<ActivityHttpPage>().Produces<ActivityHttpError>(400).Produces<ActivityHttpError>(403)
            .Produces<ActivityHttpError>(409).Produces<ActivityHttpError>(503);
    }

    /// <summary>Returns a durable head only when the configured journal is readable; queue gauges are intentionally unknown.</summary>
    private static IResult Status(HttpContext context, IHostEnvironment environment, LocalAgentActivityFeature feature)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!AgentActivityEndpoint.Allowed(context, environment, feature)) return Error(403);
        if (context.Request.Query.Count != 0) return Error(400);
        var status = feature.Status();
        if (string.IsNullOrEmpty(status.JournalEpoch)) return Error(503);
        return Results.Json(new ActivityHttpStatus(Schema, "agmemory", "agent-activity/v1", status.JournalEpoch,
            status.OldestSequence, status.LatestSequence, status.MaxSizeBytes, status.UsedBytes, status.Cursor, new(), Health(status.Health)), ActivityJson.Options);
    }

    /// <summary>Validates the cursor against the exact read window and bounds the complete serialized page without skipping matching events.</summary>
    private static async Task<IResult> EventsAsync(HttpContext context, IHostEnvironment environment,
        LocalAgentActivityFeature feature, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!AgentActivityEndpoint.Allowed(context, environment, feature)) return Error(403);
        if (!ActivityHttpQueryParser.TryParse(context.Request, out var query)) return Error(400);
        try
        {
            var (read, projected) = await feature.ReadHttpAsync(query!, cancellationToken).ConfigureAwait(false);
            if (!query!.IsCurrent(read.Status)) return Error(409, read.Status.Cursor);
            var events = projected.ToList();
            var lengths = events.Select(value => JsonSerializer.SerializeToUtf8Bytes(value, ActivityJson.Options).Length).ToList();
            var eventBytes = lengths.Sum();
            var more = read.HasMore;
            var truncated = false;
            ActivityHttpPage page;
            while (true)
            {
                page = Page(query!, read.Status, events, more, truncated);
                var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(page with { Events = [] }, ActivityJson.Options).Length;
                if (envelopeBytes + eventBytes + Math.Max(0, events.Count - 1) <= MaxPageBytes) break;
                if (events.Count <= 1) return Error(503);
                // Trim the traversal tail only: forward keeps its oldest prefix; backward keeps its newest suffix.
                var index = query!.Forward ? events.Count - 1 : 0;
                eventBytes -= lengths[index]; lengths.RemoveAt(index); events.RemoveAt(index);
                more = true; truncated = true;
            }
            return Results.Json(page, ActivityJson.Options);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Error(503); }
    }

    /// <summary>Computes exclusive continuation from the retained page, never from a later status request.</summary>
    private static ActivityHttpPage Page(ActivityHttpQuery query, ActivityStatus status, List<ActivityEvent> events, bool more, bool truncated)
    {
        string next;
        if (query.Forward) next = more && events.Count > 0 ? events[^1].Sequence : status.LatestSequence;
        else next = more && events.Count > 0 ? events[0].Sequence : ActivityJson.Number(ActivityJson.Sequence(status.OldestSequence) - 1);
        return new(Schema, "agmemory", query.Forward ? "forward" : "backward", events,
            new(status.JournalEpoch, next), status.Cursor, status.OldestSequence, more, truncated, Health(status.Health));
    }

    private static ActivityHttpHealth Health(ActivityHealth value) => new(value.Status == "healthy" ? "ok" : "degraded",
        value.Dropped, null, null, value.ErrorCode);

    /// <summary>Emits only fixed portable messages; a reset head is included exclusively for HTTP 409.</summary>
    private static IResult Error(int status, ActivityCursor? reset = null)
    {
        var (code, message) = status switch
        {
            400 => ("invalid_query", "Invalid query parameters."),
            403 => ("forbidden", "Access forbidden."),
            409 => ("cursor_expired", "Cursor has expired."),
            _ => ("journal_unavailable", "Journal is unavailable.")
        };
        return Results.Json(new ActivityHttpError(Schema, "agmemory", new(code, message, status == 503), reset), ActivityJson.Options, statusCode: status);
    }
}
