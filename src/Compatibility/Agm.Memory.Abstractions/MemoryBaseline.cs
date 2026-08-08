namespace Agm.Memory.Abstractions;

public sealed record MemorySourceAccessRequest(
    MemoryScope RequestScope,
    MemoryScope SourceScope,
    MemorySourceDescriptor Source);

public enum MemorySourceAccessOutcome
{
    Allowed = 0,
    TenantMismatch = 1,
    ProjectMismatch = 2,
    SourceKindDisabled = 3
}

public interface IMemorySourceAccessPolicy
{
    MemorySourceAccessOutcome Evaluate(MemorySourceAccessRequest request);
}

public sealed record MemoryFeatureFlags(
    bool CanonicalMemory = false,
    bool HybridRetrieval = false,
    bool GraphReranking = false,
    bool NeuralReranking = false,
    bool SessionContextBoost = false,
    bool QualityCanary = false)
{
    public static MemoryFeatureFlags Disabled { get; } = new();
}

public readonly record struct MemoryTraceId
{
    public const int Length = 32;

    private MemoryTraceId(string value) => Value = value;

    public string Value { get; }

    public static MemoryTraceId Create() => new(Guid.NewGuid().ToString("N"));

    public static MemoryTraceId Parse(string value) =>
        TryParse(value, out var traceId)
            ? traceId
            : throw new FormatException("A memory trace ID must be 32 lowercase hexadecimal characters and not all zeroes.");

    public static bool TryParse(string? value, out MemoryTraceId traceId)
    {
        traceId = default;
        if (value is null || value.Length != Length) return false;

        var hasNonZeroCharacter = false;
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
            hasNonZeroCharacter |= character != '0';
        }

        if (!hasNonZeroCharacter) return false;
        traceId = new MemoryTraceId(value);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}
