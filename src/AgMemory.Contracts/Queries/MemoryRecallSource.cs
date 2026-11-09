namespace AgMemory.Contracts;

/// <summary>Server-only current canonical projection. The adapter enforces exact scope/type/lifecycle/expiry before returning it; no embeddings or provenance JSON.</summary>
public sealed record MemoryRecallSource(MemoryId Id, MemoryRecordType Type, string Content, long Revision,
    DateTimeOffset UpdatedAt, DateTimeOffset? ExpiresAt);
