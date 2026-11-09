using AgMemory.Contracts;

namespace AgMemory.Core;

internal static class HotMemoryDecayEvaluator
{
    public static double Score(HotMemoryEntry entry, DateTimeOffset asOfUtc, TimeSpan halfLife)
    {
        if (asOfUtc.Offset != TimeSpan.Zero) throw new ArgumentException("The evaluation instant must be UTC.", nameof(asOfUtc));
        if (halfLife <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(halfLife));
        var age = asOfUtc - entry.CapturedAt;
        if (age <= TimeSpan.Zero) return entry.Importance;
        var score = entry.Importance * Math.Pow(.5d, age.TotalSeconds / halfLife.TotalSeconds);
        return double.IsFinite(score) ? score : 0d;
    }
}
