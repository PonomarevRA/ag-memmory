using AgMemory.Contracts;

namespace AgMemory.Core;

internal static class HotMemoryStateBounder
{
    public static HotMemoryState Bind(HotMemoryState state, HotMemoryPolicy policy, DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(policy);
        state.Validate();
        policy.Validate();
        if (asOfUtc.Offset != TimeSpan.Zero) throw new ArgumentException("The evaluation instant must be UTC.", nameof(asOfUtc));

        foreach (var entry in state.Entries)
        {
            if (entry.Content.Length > policy.MaximumEntryCharacters)
                throw new ArgumentOutOfRangeException(nameof(state), "A hot-memory entry exceeds the configured compact-content limit.");
            if (entry.CapturedAt > asOfUtc)
                throw new ArgumentException("A hot-memory entry cannot be captured in the future.", nameof(state));
        }

        var perKind = state.Entries
            .GroupBy(entry => entry.Kind)
            .SelectMany(group => group
                .OrderByDescending(entry => HotMemoryDecayEvaluator.Score(entry, asOfUtc, policy.DecayHalfLife))
                .ThenByDescending(entry => entry.CapturedAt)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(group.Key == HotMemoryEntryKind.CurrentGoal ? 1 : policy.MaximumEntriesPerKind))
            .OrderBy(entry => entry.Kind)
            .ThenByDescending(entry => HotMemoryDecayEvaluator.Score(entry, asOfUtc, policy.DecayHalfLife))
            .ThenByDescending(entry => entry.CapturedAt)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(policy.MaximumEntries)
            .ToArray();

        var bounded = new List<HotMemoryEntry>(perKind.Length);
        var cost = 0;
        foreach (var entry in perKind)
        {
            var next = CommandValueSupport.EstimateTokenCost(HotMemoryStateCodec.RenderEntry(entry));
            if (next > policy.MaximumEstimatedTokenCost - cost) continue;
            bounded.Add(entry);
            cost += next;
        }
        if (bounded.Count == 0)
            throw new ArgumentException("No hot-memory entry fits the configured token bound.", nameof(state));
        return new(bounded);
    }
}
