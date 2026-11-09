using System.Globalization;
using System.Text.Json;

namespace AgMemory.Web.Features.TechnicalLogs;

/// <summary>Bounded, redacted native diagnostics; nullable context is explicit and never inferred.</summary>
public sealed record TechnicalLogEvent
{
    public string SchemaVersion { get; init; } = "technical-log/v1";
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public string JournalEpoch { get; init; } = "";
    public string Sequence { get; init; } = "0";
    public string Application { get; init; } = "agmemory";
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Level { get; init; } = "INFO";
    public string Category { get; init; } = "";
    public string? EventName { get; init; }
    public string? MessageTemplate { get; init; }
    public string Message { get; init; } = "";
    public TechnicalLogException? Exception { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public string? OperationId { get; init; }
    public string? SessionId { get; init; }
    public string? TaskId { get; init; }
    public IReadOnlyDictionary<string, object?> Properties { get; init; } = new Dictionary<string, object?>();
}

/// <summary>Sanitized exception evidence without local variables or arbitrary object serialization.</summary>
public sealed record TechnicalLogException(string Type, string Message, string StackTrace);
public sealed record TechnicalLogCursor(string JournalEpoch, string Sequence);
public sealed record TechnicalLogHealth(string Status, long? DroppedTotal, int? QueueItems, long? QueueBytes, string? ErrorCode);
public sealed record TechnicalLogLimits(int MaxPageEvents = 100, int MaxPageBytes = 262144);
public sealed record TechnicalLogStatus(string SchemaVersion, string Application, string EventSchemaVersion, string JournalEpoch,
    string OldestSequence, string LatestSequence, long MaxSizeBytes, long UsedBytes, TechnicalLogCursor Cursor,
    TechnicalLogLimits Limits, TechnicalLogHealth Health);
public sealed record TechnicalLogPage(string SchemaVersion, string Application, string Direction, IReadOnlyList<TechnicalLogEvent> Events,
    TechnicalLogCursor NextCursor, TechnicalLogCursor HighWatermark, string OldestSequence, bool HasMore, bool Truncated, TechnicalLogHealth Health);
public sealed record TechnicalLogFailure(string Code, string Message, bool Retryable);
public sealed record TechnicalLogError(string SchemaVersion, string Application, TechnicalLogFailure Error, TechnicalLogCursor? ResetCursor);
internal sealed record TechnicalLogRead(IReadOnlyList<TechnicalLogEvent> Events, TechnicalLogStatus Status, bool HasMore);

/// <summary>Separate technical retention and bounded delivery settings, activated on process restart.</summary>
public sealed record TechnicalLogOptions
{
    public long MaxSizeBytes { get; init; } = 100_000_000;
    public int QueueCapacity { get; init; } = 1024;
    public long QueueMaxBytes { get; init; } = 1_048_576;
    public int BatchBytes { get; init; } = 65_536;
    public int BatchDelayMs { get; init; } = 250;
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    /// <summary>Rejects settings that cannot accommodate an event and bounded manifest replacement.</summary>
    public void Validate()
    {
        if (MaxSizeBytes is < 131072 or > 10_000_000_000 || QueueCapacity is < 2 or > 100000 ||
            QueueMaxBytes is < 131072 or > 100_000_000 || BatchBytes is < 16384 or > 65536 ||
            BatchDelayMs is < 1 or > 1000 || !Enum.IsDefined(MinimumLevel)) throw new ArgumentException("Invalid TechnicalLogs settings.");
    }
}

internal static class TechnicalLogJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    public static long Sequence(string value) => long.Parse(value, CultureInfo.InvariantCulture);
}
