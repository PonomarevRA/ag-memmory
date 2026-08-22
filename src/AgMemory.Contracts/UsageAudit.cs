using System.Text.RegularExpressions;

namespace AgMemory.Contracts;

/// <summary>Privacy-limited local audit contract; it never carries scopes, durable IDs or content.</summary>
public sealed record UsageAuditQueryDescriptor(string Hash, int LengthChars)
{
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public void Validate()
    {
        if (!HashPattern.IsMatch(Hash)) throw new ArgumentException("A usage audit query hash must be a lowercase SHA-256 hex value.", nameof(Hash));
        if (LengthChars < 0) throw new ArgumentOutOfRangeException(nameof(LengthChars));
    }
}

public enum UsageAuditSource { Mcp, LocalChat, Unknown }
public enum UsageAuditOperation { Recall, ContextBuild, Unknown }
public enum UsageAuditClientLabel { Codex, Cursor, Claude, LocalChat, Unknown }
public enum UsageAuditOutcome { Succeeded, Failed, Degraded, Unknown }
public enum UsageAuditFailureClass { Validation, Authorization, Storage, Timeout, Unknown }

public static class UsageAuditPolicy
{
    public const string SchemaVersion = "usage-audit/v1";
    public const string EstimationMethodVersion = "context-chars-div4-cap800/v1";
    public const int RetentionDays = 30;
    public const int MaximumAreaIdLength = 64;

    public static bool IsSafeAreaId(string? value) => value is null ||
        value.Length is > 0 and <= MaximumAreaIdLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    public static int EstimateDeliveredContextTokens(UsageAuditOperation operation, UsageAuditOutcome outcome, int selectedContextChars)
    {
        if (selectedContextChars < 0) throw new ArgumentOutOfRangeException(nameof(selectedContextChars));
        return operation == UsageAuditOperation.ContextBuild && outcome == UsageAuditOutcome.Succeeded
            ? Math.Min(800, (selectedContextChars + 3) / 4)
            : 0;
    }
}

public sealed record UsageAuditEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    UsageAuditSource Source,
    UsageAuditOperation Operation,
    UsageAuditClientLabel ClientLabel,
    string? AreaId,
    UsageAuditQueryDescriptor Query,
    int ResultCount,
    int SelectedContextChars,
    int DeliveredTokensEstimate,
    string EstimationMethodVersion,
    UsageAuditOutcome Outcome,
    UsageAuditFailureClass? FailureClass,
    string SchemaVersion = UsageAuditPolicy.SchemaVersion)
{
    public void Validate()
    {
        if (EventId == Guid.Empty) throw new ArgumentException("A usage audit event id is required.", nameof(EventId));
        if (OccurredAt.Offset != TimeSpan.Zero) throw new ArgumentException("Usage audit timestamps must be UTC.", nameof(OccurredAt));
        // Unknown is itself an approved safe fallback for an unmapped external value; raw values never cross this boundary.
        if (!Enum.IsDefined(Source) || !Enum.IsDefined(Operation) || !Enum.IsDefined(Outcome) || !Enum.IsDefined(ClientLabel) ||
            (FailureClass is not null && !Enum.IsDefined(FailureClass.Value)))
            throw new ArgumentOutOfRangeException("Usage audit values must be mapped to the closed allowlists.");
        if (!UsageAuditPolicy.IsSafeAreaId(AreaId)) throw new ArgumentException("The usage audit area must be an opaque safe label.", nameof(AreaId));
        ArgumentNullException.ThrowIfNull(Query);
        Query.Validate();
        if (ResultCount < 0 || SelectedContextChars < 0 || DeliveredTokensEstimate < 0) throw new ArgumentOutOfRangeException("Usage audit counts cannot be negative.");
        if (!string.Equals(EstimationMethodVersion, UsageAuditPolicy.EstimationMethodVersion, StringComparison.Ordinal))
            throw new ArgumentException("The usage audit estimation method is not recognized.", nameof(EstimationMethodVersion));
        if (DeliveredTokensEstimate != UsageAuditPolicy.EstimateDeliveredContextTokens(Operation, Outcome, SelectedContextChars))
            throw new ArgumentException("The usage audit token estimate does not match the approved method.", nameof(DeliveredTokensEstimate));
        if (Outcome == UsageAuditOutcome.Succeeded && FailureClass is not null)
            throw new ArgumentException("A successful usage audit event cannot have a failure class.", nameof(FailureClass));
        if (Outcome != UsageAuditOutcome.Succeeded && FailureClass is null)
            throw new ArgumentException("A non-success usage audit event requires a safe failure class.", nameof(FailureClass));
        if (!string.Equals(SchemaVersion, UsageAuditPolicy.SchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException("The usage audit schema version is not recognized.", nameof(SchemaVersion));
    }
}

public enum UsageAuditAppendResult { Appended, IdempotencyReplay }

/// <summary>Append-only local audit port. It intentionally offers no browsing/read operation.</summary>
public interface IUsageAuditStore
{
    Task<UsageAuditAppendResult> AppendAsync(UsageAuditEvent usageEvent, CancellationToken cancellationToken);
    Task<int> DeleteExpiredAsync(DateTimeOffset cutoffExclusiveUtc, CancellationToken cancellationToken);
}

public sealed class UsageAuditEventConflictException(Guid eventId) : InvalidOperationException($"Usage audit event '{eventId}' conflicts with an existing append-only event.")
{
    public Guid EventId { get; } = eventId;
}
