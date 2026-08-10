namespace AgMemory.Contracts;

/// <summary>Hard ceilings for the local, content-bearing reader. Browser callers cannot override them.</summary>
public static class MemoryReaderLimits
{
    public const int BlocksPerPage = 8;
    public const int MaximumBlocksPerDocument = 256;
    public const int MaximumLinksPerBlock = 64;
    public const int MaximumBlockCharacters = 8_000;
    public const int MaximumDocumentCharacters = 120_000;
}

/// <summary>Requests the server-configured home document for exactly one authorised scope.</summary>
public sealed record MemoryReaderHomeRequest(
    ActorId Actor,
    MemoryScope RequestedScope,
    MemoryId HomeMemoryId,
    ContractVersion ContractVersion);

/// <summary>Requests one fixed-size page of blocks through a server-issued, opaque document route key.</summary>
public sealed record MemoryReaderDocumentRequest(
    ActorId Actor,
    MemoryScope RequestedScope,
    string RouteKey,
    string? RequestedBlockKey,
    MemoryReaderBlockCursor? BlockCursor,
    ContractVersion ContractVersion);

/// <summary>Server-side continuation state. The Web feature protects it before browser delivery.</summary>
public sealed record MemoryReaderBlockCursor(long Version, int NextBlockIndex);

/// <summary>One bounded source row. Adapters must not materialise embeddings for reader operations.</summary>
public sealed record MemoryReaderSourceRecord(
    MemoryId MemoryId,
    MemoryScope Scope,
    MemoryRecordType Type,
    MemoryLifecycleStatus Status,
    string CanonicalText,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    DateTimeOffset? ExpiresAt);

/// <summary>One internal reader document locator. RouteKey is random, persisted and never grants authority.</summary>
public sealed record MemoryReaderRoute(string RouteKey, MemoryId MemoryId);

public enum MemoryReaderInlineKind { Text, Link }

/// <summary>One escaped text run or an already resolved local reader link.</summary>
public sealed record MemoryReaderInline(
    MemoryReaderInlineKind Kind,
    string Text,
    string? RouteKey = null,
    string? BlockKey = null);

/// <summary>One bounded rendered piece of a memory document.</summary>
public sealed record MemoryReaderBlock(
    string BlockKey,
    string? Heading,
    IReadOnlyList<MemoryReaderInline> Content);

public enum MemoryReaderDocumentState { Available, NotFound, Changed, Unavailable }

public sealed record MemoryReaderDocumentPage(
    MemoryReaderDocumentState State,
    string RouteKey,
    IReadOnlyList<MemoryReaderBlock> Blocks,
    MemoryReaderBlockCursor? NextCursor,
    ContractVersion ContractVersion);
