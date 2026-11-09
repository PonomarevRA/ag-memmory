using AgMemory.Contracts;

namespace AgMemory.Core;

internal sealed class HotMemoryPromotionService(
    IMemoryCommandService commands,
    IClock clock,
    HotMemoryPolicy policy,
    ContractVersion contractVersion)
{
    public async Task<HotMemoryPromotionResult> PromoteAsync(
        HotMemoryPromotionCommand command,
        HotMemoryStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var entry = snapshot.State.Entries.SingleOrDefault(candidate =>
            string.Equals(candidate.Key, command.EntryKey, StringComparison.Ordinal));
        if (entry is null)
            return Result(HotMemoryPromotionOutcome.NotFound, null, snapshot.Memory.Version,
                new(MemoryErrorCode.NotFound, nameof(command.EntryKey)));
        if (entry.Kind != HotMemoryEntryKind.WorkingFact || !policy.PromotableKinds.Contains(entry.Kind) ||
            HotMemoryDecayEvaluator.Score(entry, UtcNow(), policy.DecayHalfLife) < policy.PromotionScoreThreshold)
            return Result(HotMemoryPromotionOutcome.Ineligible, null, snapshot.Memory.Version, null);

        var score = HotMemoryDecayEvaluator.Score(entry, UtcNow(), policy.DecayHalfLife);
        var remember = await commands.RememberAsync(new(
            command.Envelope,
            new(MemoryRecordType.Fact, entry.Content, null, score, entry.Confidence, [], snapshot.Memory.Provenance)),
            cancellationToken).ConfigureAwait(false);
        return remember.Outcome switch
        {
            RememberOutcome.Created or RememberOutcome.Reinforced => Result(
                HotMemoryPromotionOutcome.Promoted, remember.Memory, snapshot.Memory.Version, null),
            RememberOutcome.IdempotencyReplay => Result(
                HotMemoryPromotionOutcome.IdempotencyReplay, remember.Memory, snapshot.Memory.Version, null),
            _ => Result(HotMemoryPromotionOutcome.Failed, null, snapshot.Memory.Version, remember.Error)
        };
    }

    private DateTimeOffset UtcNow()
    {
        var now = clock.UtcNow;
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }

    private HotMemoryPromotionResult Result(
        HotMemoryPromotionOutcome outcome,
        MemoryRecord? memory,
        long? version,
        MemoryError? error) => new(outcome, memory, version, error, contractVersion);
}
