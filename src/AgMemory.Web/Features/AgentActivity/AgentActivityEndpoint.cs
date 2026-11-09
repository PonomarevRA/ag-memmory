using AgMemory.ActivityJournal;
using AgMemory.Web.Features.MemoryGraph;
using Microsoft.AspNetCore.Antiforgery;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace AgMemory.Web.Features.AgentActivity;

/// <summary>Local same-origin history and stop-and-wait stream. Every packet is projected before it reaches a browser.</summary>
public static class AgentActivityEndpoint
{
    public const string Prefix = "/api/agent-activity";
    private const int PacketLimit = 65_536;
    private static readonly SemaphoreSlim StreamSlots = new(16, 16);

    /// <summary>Registers the same fixed browser contract for history, status, clear and the single shared stream.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet(Prefix + "/status", (HttpContext context, IHostEnvironment environment, LocalAgentActivityFeature feature) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Allowed(context, environment, feature) ? Results.Json(feature.Status(), ActivityJson.Options) : Results.StatusCode(403);
        });
        AgentActivityHttpEndpoint.Map(app);
        app.MapGet(Prefix + "/history", HistoryAsync);
        app.MapPost(Prefix + "/clear", ClearAsync);
        app.Map(Prefix + "/stream", StreamAsync);
    }

    /// <summary>Admits only configured Development loopback requests and validates bounded metadata filters.</summary>
    private static async Task<IResult> HistoryAsync(HttpContext context, IHostEnvironment environment, LocalAgentActivityFeature feature,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Allowed(context, environment, feature)) return Results.StatusCode(403);
        var query = context.Request.Query;
        if (!int.TryParse(query["limit"].FirstOrDefault() ?? "100", out var limit) || limit is < 1 or > 100) return Results.BadRequest();
        long? before = null;
        if (query.ContainsKey("beforeSequence"))
        {
            if (!TrySequence(query["beforeSequence"].ToString(), out var number)) return Results.BadRequest();
            before = number;
        }
        var session = query["sessionId"].FirstOrDefault();
        var operation = query["operation"].FirstOrDefault();
        var text = query["q"].FirstOrDefault();
        if ((session?.Length ?? 0) > 128 || (operation?.Length ?? 0) > 128 || (text?.Length ?? 0) > 512) return Results.BadRequest();
        try
        {
            var result = await feature.ReadAsync(limit, null, before, session, operation, text, cancellationToken).ConfigureAwait(false);
            var events = result.Events.ToList();
            var more = result.Read.HasMore;
            ActivityHistory page;
            do
            {
                page = new(events, result.Read.Status.Cursor, result.Read.Status.OldestSequence, more);
                if (JsonSerializer.SerializeToUtf8Bytes(page, ActivityJson.Options).Length <= 262_144) break;
                events.RemoveAt(0); more = true;
            } while (events.Count > 0);
            return Results.Json(page, ActivityJson.Options);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Results.StatusCode(503); }
    }

    /// <summary>Clears the independent journal only after same-origin and antiforgery checks.</summary>
    private static async Task<IResult> ClearAsync(HttpContext context, IHostEnvironment environment, LocalAgentActivityFeature feature, IAntiforgery antiforgery)
    {
        if (!Allowed(context, environment, feature) || !SameOrigin(context, requireOrigin: true)) return Results.StatusCode(403);
        try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
        catch (AntiforgeryValidationException) { return Results.StatusCode(403); }
        try { return Results.Json(feature.Clear(), ActivityJson.Options); }
        catch { return Results.StatusCode(503); }
    }

    /// <summary>One outstanding packet gives explicit browser credit; an invalid ACK, expired cursor or stalled consumer closes only the subscription.</summary>
    private static async Task StreamAsync(HttpContext context, IHostEnvironment environment, LocalAgentActivityFeature feature)
    {
        if (!Allowed(context, environment, feature) || !SameOrigin(context, requireOrigin: true)) { context.Response.StatusCode = 403; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        if (!StreamSlots.Wait(0)) { context.Response.StatusCode = 429; return; }
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await PumpAsync(socket, context, feature).ConfigureAwait(false);
        }
        finally { StreamSlots.Release(); }
    }

    private static async Task PumpAsync(WebSocket socket, HttpContext context, LocalAgentActivityFeature feature)
    {
        using var receiverStop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var controls = Channel.CreateBounded<ClientMessage>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var receiver = ReadControlsAsync(socket, controls.Writer, receiverStop.Token);
        try
        {
            var subscription = await ReadControlAsync(controls.Reader, context.RequestAborted).ConfigureAwait(false);
            if (subscription?.Type != "subscribe") { await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "subscribe_required", context.RequestAborted); return; }
            var cursor = subscription.Cursor;
            var snapshot = cursor is null;
            long? revision = null;
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                if (!snapshot && controls.Reader.TryRead(out _))
                { await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "unexpected_control", context.RequestAborted); return; }
                var currentRevision = feature.Revision;
                if (!snapshot && revision == currentRevision)
                {
                    await Task.WhenAny(Task.Delay(250, context.RequestAborted), controls.Reader.Completion).ConfigureAwait(false);
                    if (controls.Reader.Completion.IsCompleted) return;
                    continue;
                }
                var result = await feature.ReadAsync(128, snapshot ? null : CursorSequence(cursor), null, null, null, null, context.RequestAborted).ConfigureAwait(false);
                var status = result.Read.Status;
                revision = currentRevision;
                if (!snapshot && !ValidCursor(cursor, status))
                {
                    await SendAsync(socket, new { type = "resetRequired", reason = "cursor_unavailable", journalEpoch = status.JournalEpoch }, context.RequestAborted);
                    await CloseAsync(socket, (WebSocketCloseStatus)4009, "resetRequired", context.RequestAborted); return;
                }
                if (!snapshot && result.Events.Count == 0)
                {
                    cursor = status.Cursor;
                    await Task.WhenAny(Task.Delay(250, context.RequestAborted), controls.Reader.Completion).ConfigureAwait(false);
                    if (controls.Reader.Completion.IsCompleted) return;
                    continue;
                }
                var events = result.Events.ToList();
                var omitted = result.Read.HasMore ? 1 : 0;
                var messageId = Guid.NewGuid().ToString("N");
                byte[] bytes;
                ActivityCursor next;
                do
                {
                    next = snapshot || events.Count == 0 ? status.Cursor : new(status.JournalEpoch, events[^1].Sequence);
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = snapshot ? "snapshot" : "events", messageId,
                        journalEpoch = status.JournalEpoch, cursor = next, events, health = status.Health, omittedCount = omitted }, ActivityJson.Options);
                    if (bytes.Length <= PacketLimit) break;
                    if (events.Count == 0) throw new InvalidOperationException("packet_too_large");
                    events.RemoveAt(snapshot ? 0 : events.Count - 1); omitted++;
                } while (true);
                await SendBytesAsync(socket, bytes, context.RequestAborted).ConfigureAwait(false);
                var ack = await ReadControlAsync(controls.Reader, context.RequestAborted).ConfigureAwait(false);
                if (ack?.Type != "ack" || ack.MessageId != messageId || ack.Cursor != next)
                { await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "invalid_ack", context.RequestAborted); return; }
                cursor = next;
                revision = snapshot || !result.Read.HasMore && omitted == 0 ? currentRevision : null;
                snapshot = false;
            }
        }
        catch (OperationCanceledException) { socket.Abort(); }
        catch (WebSocketException) { socket.Abort(); }
        catch { if (socket.State == WebSocketState.Open) await CloseAsync(socket, WebSocketCloseStatus.InternalServerError, "stream_unavailable", CancellationToken.None); }
        finally { receiverStop.Cancel(); socket.Abort(); await receiver.ConfigureAwait(false); }
    }

    internal static bool SameOrigin(HttpContext context, bool requireOrigin)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length == 0) return !requireOrigin;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == context.Request.Scheme &&
            string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query);
    }

    internal static bool Allowed(HttpContext context, IHostEnvironment environment, LocalAgentActivityFeature feature) =>
        MemoryGraphAccessPolicy.AllowsStoreAccess(feature.IsConfigured, environment.IsDevelopment(), context.Connection.RemoteIpAddress) &&
        (context.Request.Host.Host == "localhost" || IPAddress.TryParse(context.Request.Host.Host, out var ip) && IPAddress.IsLoopback(ip)) &&
        context.Request.Headers["Sec-Fetch-Site"].ToString() != "cross-site" && SameOrigin(context, requireOrigin: false);

    private static bool TrySequence(string value, out long sequence) => long.TryParse(value, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out sequence) && sequence >= 0 && ActivityJson.Number(sequence) == value;
    private static long CursorSequence(ActivityCursor? cursor) => cursor is not null && TrySequence(cursor.Sequence, out var value) ? value : -1;
    private static bool ValidCursor(ActivityCursor? cursor, ActivityStatus status) => cursor is not null && cursor.JournalEpoch == status.JournalEpoch &&
        CursorSequence(cursor) >= ActivityJson.Sequence(status.OldestSequence) - 1 && CursorSequence(cursor) <= ActivityJson.Sequence(status.LatestSequence);

    /// <summary>Bounds fragmented messages and gives subscription/ACK five seconds; socket buffering alone is never considered progress.</summary>
    private static async Task<ClientMessage?> ReceiveWireAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        var count = 0;
        while (count < buffer.Length)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Text) return null;
            count += result.Count;
            if (result.EndOfMessage) return JsonSerializer.Deserialize<ClientMessage>(buffer.AsSpan(0, count), ActivityJson.Options);
        }
        return null;
    }

    /// <summary>One permanent receive loop observes idle close frames and bounds unsolicited control traffic.</summary>
    private static async Task ReadControlsAsync(WebSocket socket, ChannelWriter<ClientMessage> controls, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveWireAsync(socket, cancellationToken).ConfigureAwait(false);
                if (message is null || !controls.TryWrite(message)) return;
            }
        }
        catch { /* Completion signals peer disconnect without leaking transport errors. */ }
        finally { controls.TryComplete(); }
    }

    private static async Task<ClientMessage?> ReadControlAsync(ChannelReader<ClientMessage> controls, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await controls.ReadAsync(timeout.Token).ConfigureAwait(false); }
        catch (ChannelClosedException) { return null; }
    }

    private static Task SendAsync(WebSocket socket, object message, CancellationToken cancellationToken) =>
        SendBytesAsync(socket, JsonSerializer.SerializeToUtf8Bytes(message, ActivityJson.Options), cancellationToken);

    /// <summary>Bounds transport progress as well as application ACK progress, keeping slow sockets outside the writer path.</summary>
    private static async Task SendBytesAsync(WebSocket socket, byte[] bytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try { await socket.CloseAsync(status, reason, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { socket.Abort(); }
        catch (WebSocketException) { socket.Abort(); }
    }

    private sealed record ClientMessage(string Type, string? MessageId, ActivityCursor? Cursor);
}
