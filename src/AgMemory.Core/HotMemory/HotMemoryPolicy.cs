using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Explicit product limits for structured session memory; no retention default is hidden in Core.</summary>
public sealed record HotMemoryPolicy(
    TimeSpan MaximumTtl,
    TimeSpan DecayHalfLife,
    int MaximumEntries,
    int MaximumEntriesPerKind,
    int MaximumEstimatedTokenCost,
    int MaximumEntryCharacters,
    double PromotionScoreThreshold,
    IReadOnlySet<HotMemoryEntryKind> PromotableKinds)
{
    public void Validate()
    {
        if (MaximumTtl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MaximumTtl));
        if (DecayHalfLife <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(DecayHalfLife));
        if (MaximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumEntries));
        if (MaximumEntriesPerKind <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumEntriesPerKind));
        if (MaximumEstimatedTokenCost <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumEstimatedTokenCost));
        if (MaximumEntryCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumEntryCharacters));
        if (!double.IsFinite(PromotionScoreThreshold) || PromotionScoreThreshold is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(PromotionScoreThreshold));
        ArgumentNullException.ThrowIfNull(PromotableKinds);
        if (PromotableKinds.Any(kind => !Enum.IsDefined(kind)))
            throw new ArgumentException("Promotion kinds must be defined.", nameof(PromotableKinds));
    }
}
