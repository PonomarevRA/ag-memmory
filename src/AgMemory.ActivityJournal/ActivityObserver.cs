using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgMemory.ActivityJournal;

/// <summary>Records one actual invocation with allowlisted metadata. It never receives a query, payload or authority from browser input.</summary>
public sealed class ActivityObserver(ActivityWriter writer, string partition, string clientKind, bool captureQueryPreview = true)
{
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly AsyncLocal<TargetContext?> _target = new();

    /// <summary>Remembers only the last actually accessed authorized record in the current async operation.</summary>
    public void Target(string durableId)
    {
        if (_target.Value is { } context) context.Token = ActivityIdentity.Target(partition, durableId);
    }

    /// <summary>Invokes business work once and preserves its result, exception and cancellation even if observability fails.</summary>
    public async Task<T> ExecuteAsync<T>(string operation, Func<Task<T>> invoke, Func<T, ActivitySummary>? summarize = null, string? requestSummary = null, string? requestQuery = null)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var context = new TargetContext();
        if (requestQuery is not null) context.Query = SafePreview(requestQuery);
        var previous = _target.Value;
        _target.Value = context;
        var watch = Stopwatch.StartNew();
        Emit(operationId, operation, "started", null, null, null, requestSummary);
        try
        {
            var result = await invoke().ConfigureAwait(false);
            ActivitySummary? summary = null;
            try { summary = summarize?.Invoke(result); } catch { /* Optional summary does not change the result. */ }
            Emit(operationId, operation, "finished", summary ?? new("succeeded"), watch.Elapsed.TotalMilliseconds, context.Token, requestSummary);
            return result;
        }
        catch (Exception error)
        {
            var outcome = error is OperationCanceledException ? "cancelled" : "failed";
            Emit(operationId, operation, "finished", new(outcome, ErrorCode: error is ArgumentException ? "validation" : outcome), watch.Elapsed.TotalMilliseconds, context.Token, requestSummary);
            throw;
        }
        finally { _target.Value = previous; }
    }

    private void Emit(string operationId, string operation, string phase, ActivitySummary? summary, double? duration, string? targetToken, string? requestSummary)
    {
        try
        {
            var value = new ActivityEvent { SessionId = _session, ClientKind = SafeClient(clientKind), OperationId = operationId,
                Operation = operation, Phase = phase, Outcome = summary?.Outcome, ResultCount = summary?.Count,
                DurationMs = duration, ErrorCode = summary?.ErrorCode, Target = new(),
                Query = _target.Value?.Query ?? new("omitted"),
                Request = new(ToolName(operation), requestSummary ?? RequestDescription(operation)),
                Response = phase == "finished" ? new(ResponseDescription(operation, summary), summary?.Count, false, summary?.ErrorCode) : null };
            writer.TryAppend(new(partition, value, targetToken));
        }
        catch { /* Enqueue is optional; no private input or exception is logged. */ }
    }

    /// <summary>Uses fixed purpose templates. Caller payload and memory content never become an activity preview.</summary>
    private static string RequestDescription(string operation) => operation switch
    {
        "RecallAsync" or "AgentContextAsync" or "WorkingContextAsync" => "Поиск разрешённой памяти",
        "RememberAsync" or "AgentHandoffAsync" or "AgentDecisionAsync" => "Сохранение записи памяти; содержимое не сохраняется в журнал",
        "AgentResolveAsync" => "Чтение записи по защищённой ссылке; ссылка не сохраняется в журнал",
        "GetStatusAsync" => "Проверка доступности памяти и количества активных записей",
        _ => "Операция знаний; параметры с содержимым не сохраняются в журнал"
    };
    private static string ResponseDescription(string operation, ActivitySummary? summary)
    {
        if (summary?.Outcome != "succeeded") return "Операция завершилась ошибкой или была отменена";
        if (operation == "GetStatusAsync" && summary.Count is not null) return $"Активных записей: {summary.Count}";
        return summary.Count is null ? "Операция выполнена" : $"Операция выполнена; результатов: {summary.Count}";
    }

    /// <summary>Captures only explicitly routed search inputs; obvious credentials and control characters are removed before enqueue.</summary>
    public void Query(string query) { if (_target.Value is { } context) context.Query = SafePreview(query); }

    /// <summary>Falls back to omitted metadata if even bounded redaction cannot complete safely.</summary>
    private ActivityQuery SafePreview(string text)
    {
        try { return Preview(text); }
        catch { return new("omitted"); }
    }

    /// <summary>Bounds transient input before fixed credential masking, then limits the retained Unicode preview.</summary>
    private ActivityQuery Preview(string text)
    {
        if (!captureQueryPreview || string.IsNullOrWhiteSpace(text)) return new("omitted");
        var printable = new StringBuilder(Math.Min(text.Length, 2048));
        foreach (var character in text.Take(2048)) printable.Append(char.IsControl(character) ? ' ' : character);
        var redacted = Regex.Replace(printable.ToString(), @"(?i)(bearer\s+)[a-z0-9._~+/-]+|((?:api[_-]?key|token|password|secret|authorization)\s*[:=]\s*)[^\s,;]+", "[redacted]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10));
        redacted = Regex.Replace(redacted, @"(?i)(https?://)[^/\s@:]+:[^/\s@]+@", "$1[redacted]@", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10));
        return new("preview", Truncated: redacted.Length > 256 || text.Length > 2048, Preview: redacted[..Math.Min(256, redacted.Length)]);
    }

    /// <summary>Uses the actual advertised MCP tool name rather than exposing an implementation method as the request.</summary>
    private static string ToolName(string operation) => operation switch
    {
        "RecallAsync" => "memory_recall", "RememberAsync" => "memory_remember", "GetStatusAsync" => "memory_status",
        "AgentCapabilitiesAsync" => "memory_capabilities", "AgentContextAsync" => "memory_context", "AgentResolveAsync" => "memory_resolve",
        "AgentCorrectAsync" => "memory_correct", "AgentHandoffAsync" => "memory_handoff", "AgentDecisionAsync" => "memory_decision",
        "KnowledgeCapabilitiesAsync" => "knowledge_capabilities", "KnowledgeMetadataAsync" => "memory_metadata", "KnowledgeProceduresAsync" => "memory_procedures",
        "KnowledgeProcedureAsync" => "memory_procedure", "KnowledgeOccurrenceAsync" => "memory_occurrence", "KnowledgeApplicationAsync" => "memory_application",
        "KnowledgePresentAsync" => "memory_present", "KnowledgeFeedbackAsync" => "memory_feedback", "KnowledgeOccurrenceUpdateAsync" => "memory_occurrence_update",
        "KnowledgeWorkingAsync" => "working_update", "WorkingContextAsync" => "working_context", "KnowledgeReviewAsync" => "memory_review", "KnowledgeWarmExcludeAsync" => "memory_warm_exclude",
        _ => operation
    };

    private static string SafeClient(string value) => value.ToLowerInvariant() switch { "codex" => "codex", "cursor" => "cursor", "claude" => "claude", _ => "unknown" };
    private sealed class TargetContext { public string? Token { get; set; } public ActivityQuery? Query { get; set; } }
}

public sealed record ActivitySummary(string Outcome, int? Count = null, string? ErrorCode = null);
