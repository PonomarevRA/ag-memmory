using System.Text.Json;
using AgMemory.Web.Features.AgentActivity;
using AgMemory.Web.Features.AppVersion;
using AgMemory.Web.Features.TechnicalLogs;

namespace AgMemory.Web.Features.Desktop;

/// <summary>A timestamped bounded snapshot, not a live stream or an authenticated subagent roster.</summary>
public sealed record DesktopWidgetResponse(string SchemaVersion, string AppId, string ApplicationVersion,
    DateTimeOffset CapturedAt, string Status, string Origin, IReadOnlyList<DesktopWidgetRequest> Requests, string Source);
/// <summary>Plain operation summaries from the existing activity journal; no record body or authority is exported.</summary>
public sealed record DesktopWidgetRequest(DateTimeOffset Timestamp, string Operation, string ActorLabel,
    string Status, double? DurationMs, string RequestSummary, string ResponseSummary);

public interface IDesktopWidget
{
    /// <summary>Returns local metadata coverage at the supplied actual loopback origin; cancellation remains observable.</summary>
    Task<DesktopWidgetResponse> ReadAsync(string origin, CancellationToken cancellationToken);
}

/// <summary>Coalesces the newest 64 journal events into at most eight operations without graph or source-body projection.</summary>
public sealed class DesktopWidget(LocalAgentActivityFeature activity) : IDesktopWidget
{
    /// <summary>Folds the authorized journal window into redacted operation summaries, preserving unavailable versus empty coverage.</summary>
    public async Task<DesktopWidgetResponse> ReadAsync(string origin, CancellationToken cancellationToken)
    {
        var version = AppVersionInfo.From(typeof(DesktopWidget).Assembly).Version;
        const string source = "Snapshot of up to eight operations from the last 64 authorized activity events. Client/session labels are captured provenance, not verified subagents. Empty means no observed operations; unavailable means journal coverage is unknown.";
        try
        {
            var read = await activity.ReadWidgetEventsAsync(cancellationToken).ConfigureAwait(false);
            var selected = new Dictionary<string, AgMemory.ActivityJournal.ActivityEvent>(StringComparer.Ordinal);
            foreach (var row in read.Events)
                selected[string.IsNullOrEmpty(row.Event.OperationId) ? row.Event.EventId : row.Event.OperationId] = row.Event;
            var rows = new List<DesktopWidgetRequest>();
            foreach (var value in selected.Values.OrderBy(value => value.OccurredAt).TakeLast(8))
            {
                var actor = !string.IsNullOrWhiteSpace(value.AgentId) ? "declared agent " + value.AgentId
                    : "client " + value.ClientKind + " / session " + (value.SessionId.Length == 0 ? "unknown" : value.SessionId[..Math.Min(12, value.SessionId.Length)]);
                rows.Add(new(value.OccurredAt, Text(value.Operation), Text(actor),
                    value.Phase == "started" ? "running" : Text(value.Outcome ?? "unknown"),
                    value.DurationMs is double duration && duration >= 0 && double.IsFinite(duration) ? duration : null,
                    Text(value.Request?.Summary), Text(value.Response?.Summary)));
            }
            var snapshot = new DesktopWidgetResponse("ag-desktop/widget-v1", "agmemory", version, DateTimeOffset.UtcNow,
                read.Status.Health.Status == "healthy" ? "ok" : "degraded", origin, rows, source);
            return FitBudget(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new("ag-desktop/widget-v1", "agmemory", version, DateTimeOffset.UtcNow, "unavailable", origin, [], source); }
    }

    /// <summary>Reserves transport headroom under the consumer's 64KiB cap, reporting every operation omitted by the byte budget.</summary>
    internal static DesktopWidgetResponse FitBudget(DesktopWidgetResponse snapshot)
    {
        var rows = snapshot.Requests.ToList();
        var source = snapshot.Source;
        var originalCount = rows.Count;
        // At most eight rows enter this loop. Use the same encoder as the public Web JSON response, including Unicode escaping.
        while (rows.Count != 0 && JsonSerializer.SerializeToUtf8Bytes(snapshot, WidgetJson).Length > 60 * 1024)
        {
            rows.RemoveAt(0);
            snapshot = snapshot with { Requests = rows, Source = source + $" Omitted {originalCount - rows.Count} older operations to fit the 60KiB response budget." };
        }
        return snapshot;
    }

    private static readonly JsonSerializerOptions WidgetJson = new(JsonSerializerDefaults.Web);

    /// <summary>Reuses technical-log redaction and bounds Unicode text before export.</summary>
    private static string Text(string? value) => TechnicalLogRedaction.Text(value, 512);
}
