using AgMemory.ActivityJournal;
using System.Text.Json.Serialization;

namespace AgMemory.Web.Features.AgentActivity;

/// <summary>Read limits shared by both services, measured over the complete UTF-8 response envelope.</summary>
public sealed record ActivityHttpLimits(int MaxPageEvents = 100, int MaxPageBytes = 262_144);

/// <summary>Normalized durable health; unavailable cross-process queue gauges remain explicit null.</summary>
public sealed record ActivityHttpHealth(string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? DroppedTotal,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? QueueItems,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? QueueBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ErrorCode);

/// <summary>Durable journal head and quota for a reusable read-only HTTP client.</summary>
public sealed record ActivityHttpStatus(string SchemaVersion, string Application, string EventSchemaVersion, string JournalEpoch,
    string OldestSequence, string LatestSequence, long MaxSizeBytes, long UsedBytes, ActivityCursor Cursor,
    ActivityHttpLimits Limits, ActivityHttpHealth Health);

/// <summary>One coherent page; exclusive continuation is deliberately separate from the captured durable head.</summary>
public sealed record ActivityHttpPage(string SchemaVersion, string Application, string Direction, IReadOnlyList<ActivityEvent> Events,
    ActivityCursor NextCursor, ActivityCursor HighWatermark, string OldestSequence, bool HasMore, bool Truncated, ActivityHttpHealth Health);

/// <summary>Fixed safe error descriptions never echo input, authority, storage paths or exception text.</summary>
public sealed record ActivityHttpFailure(string Code, string Message, bool Retryable);

/// <summary>Only an expired cursor has a reset head; all other handled errors explicitly carry null.</summary>
public sealed record ActivityHttpError(string SchemaVersion, string Application, ActivityHttpFailure Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ActivityCursor? ResetCursor);

/// <summary>Validated HTTP filters cannot select a server scope or a storage directory.</summary>
internal sealed record ActivityHttpQuery(int Limit, string? JournalEpoch, long? After, long? Before, string? Session,
    string? Operation, string? Query, string? Project)
{
    public bool Forward => After is not null;

    /// <summary>Checks only the captured window, before authorized graph projection can open storage.</summary>
    public bool IsCurrent(ActivityStatus status)
    {
        var sequence = After ?? Before;
        return sequence is null || JournalEpoch == status.JournalEpoch &&
            sequence >= ActivityJson.Sequence(status.OldestSequence) - 1 && sequence <= ActivityJson.Sequence(status.LatestSequence);
    }

    /// <summary>Matches common literal fields before sampling. Memory events have no code-project target.</summary>
    public bool Matches(ActivityEvent value)
    {
        if (Project is not null) return false;
        if (Query is null) return true;
        return value.Operation.Contains(Query, StringComparison.Ordinal) ||
            (value.Query.Preview?.Contains(Query, StringComparison.Ordinal) ?? false) ||
            (value.Request?.Summary.Contains(Query, StringComparison.Ordinal) ?? false) ||
            (value.Response?.Summary.Contains(Query, StringComparison.Ordinal) ?? false);
    }
}
