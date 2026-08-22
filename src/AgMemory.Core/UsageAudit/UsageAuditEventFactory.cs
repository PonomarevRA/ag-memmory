using System.Security.Cryptography;
using System.Text;
using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Builds the privacy-limited event from transient query text without retaining that text.</summary>
public sealed class UsageAuditEventFactory
{
    private readonly byte[] _hmacKey;

    public UsageAuditEventFactory(byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(hmacKey);
        if (hmacKey.Length < 16) throw new ArgumentException("Usage audit HMAC keys must contain at least 128 bits.", nameof(hmacKey));
        _hmacKey = hmacKey.ToArray();
    }

    public UsageAuditQueryDescriptor DescribeQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = query.Normalize(NormalizationForm.FormKC);
        var hash = HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(normalized));
        return new(Convert.ToHexString(hash).ToLowerInvariant(), normalized.Length);
    }

    public UsageAuditEvent Create(
        Guid eventId, DateTimeOffset occurredAt, UsageAuditSource source, UsageAuditOperation operation,
        UsageAuditClientLabel clientLabel, string? areaId, string query, int resultCount, int selectedContextChars,
        UsageAuditOutcome outcome, UsageAuditFailureClass? failureClass = null)
    {
        var utcOccurredAt = occurredAt.ToUniversalTime();
        var usageEvent = new UsageAuditEvent(eventId, utcOccurredAt, source, operation, clientLabel, areaId, DescribeQuery(query),
            resultCount, selectedContextChars,
            UsageAuditPolicy.EstimateDeliveredContextTokens(operation, outcome, selectedContextChars),
            UsageAuditPolicy.EstimationMethodVersion, outcome, failureClass);
        usageEvent.Validate();
        return usageEvent;
    }
}
