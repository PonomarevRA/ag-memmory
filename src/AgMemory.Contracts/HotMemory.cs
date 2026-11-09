namespace AgMemory.Contracts;

public enum HotMemoryEntryKind
{
    CurrentGoal,
    ActiveEntity,
    RecentDecision,
    OpenQuestion,
    WorkingFact
}

/// <summary>Compact, session-scoped working knowledge; it must never contain a transcript or raw log.</summary>
public sealed record HotMemoryEntry(
    string Key,
    HotMemoryEntryKind Kind,
    string Content,
    double Importance,
    double Confidence,
    DateTimeOffset CapturedAt)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Key, nameof(Key));
        if (Key != Key.Trim() || Key.Length > 128) throw new ArgumentException("A normalized bounded key is required.", nameof(Key));
        ArgumentException.ThrowIfNullOrWhiteSpace(Content, nameof(Content));
        if (Content.Any(char.IsControl)) throw new ArgumentException("Hot-memory content must be one compact line.", nameof(Content));
        MemoryRecord.ValidateUnitInterval(Importance, nameof(Importance));
        MemoryRecord.ValidateUnitInterval(Confidence, nameof(Confidence));
        if (CapturedAt.Offset != TimeSpan.Zero) throw new ArgumentException("Hot-memory timestamps must be UTC.", nameof(CapturedAt));
    }
}

public sealed record HotMemoryState(IReadOnlyList<HotMemoryEntry> Entries)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Entries);
        if (Entries.Count == 0) throw new ArgumentException("At least one hot-memory entry is required.", nameof(Entries));
        foreach (var entry in Entries) entry.Validate();
        if (Entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count() != Entries.Count)
            throw new ArgumentException("Hot-memory entry keys must be unique.", nameof(Entries));
    }
}

public sealed record UpdateHotMemoryStateCommand(
    CommandEnvelope Envelope,
    HotMemoryState State,
    MemoryProvenance Provenance,
    DateTimeOffset ExpiresAt,
    long ExpectedVersion);

public sealed record HotMemoryStateSnapshot(SessionHotMemory Memory, HotMemoryState State);

public sealed record HotMemoryStateResult(
    HotMemoryOutcome Outcome,
    SessionHotMemory? Memory,
    HotMemoryState? State,
    long? CurrentVersion,
    MemoryError? Error,
    ContractVersion ContractVersion);

public sealed record HotMemoryPromotionCommand(
    CommandEnvelope Envelope,
    string EntryKey,
    long ExpectedHotMemoryVersion);

public enum HotMemoryPromotionOutcome { Promoted, IdempotencyReplay, Ineligible, NotFound, StaleVersion, Failed }

public sealed record HotMemoryPromotionResult(
    HotMemoryPromotionOutcome Outcome,
    MemoryRecord? Memory,
    long? CurrentHotMemoryVersion,
    MemoryError? Error,
    ContractVersion ContractVersion);
