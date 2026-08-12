namespace AgMemory.Contracts;

/// <summary>Hard ceilings for the local, content-bearing reader. Browser callers cannot override them.</summary>
public static class MemoryReaderLimits
{
    public const int BlocksPerPage = 8;
    public const int DocumentsPerPage = 20;
    public const int MaximumBlocksPerDocument = 256;
    public const int MaximumLinksPerBlock = 64;
    public const int MaximumBlockCharacters = 8_000;
    public const int MaximumDocumentCharacters = 120_000;
    public const int MaximumCatalogPreviewCharacters = 320;
    public const int MaximumCatalogLeafScansPerPage = 8;
    public const int MaximumCatalogTagFacets = 64;
    public const int MaximumCatalogTagEntityCharacters = 256;
    public const int MaximumCatalogTagLabelCharacters = 80;
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

/// <summary>
/// Server-side continuation state for immutable catalog leaves. The Web feature protects it before browser delivery.
/// </summary>
public sealed record MemoryReaderCatalogCursor(string GenerationKey, int NextLeafPosition);

/// <summary>Requests one fixed-size page from the ready catalog for exactly one authorised scope.</summary>
public sealed record MemoryReaderCatalogRequest(
    ActorId Actor,
    MemoryScope RequestedScope,
    MemoryReaderCatalogCursor? Cursor,
    MemoryReaderCatalogFilter Filter,
    ContractVersion ContractVersion);

/// <summary>Canonical server-validated facet locators. They never contain a durable memory identifier.</summary>
public sealed record MemoryReaderCatalogFilter(string? Namespace, string? Tag)
{
    public static MemoryReaderCatalogFilter Empty { get; } = new(null, null);
}

/// <summary>One bounded source record used only while the server builds immutable catalog leaves.</summary>
public sealed record MemoryReaderCatalogSourceRecord(
    MemoryId MemoryId,
    MemoryScope Scope,
    MemoryRecordType Type,
    MemoryLifecycleStatus Status,
    string CanonicalText,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Entities);

/// <summary>Internal server cursor for the selected-column source traversal that builds a catalog generation.</summary>
public sealed record MemoryReaderCatalogBuildCursor(long NextSourceOffset);

/// <summary>One fixed-size source portion for a catalog builder. The caller never supplies its limit.</summary>
public sealed record MemoryReaderCatalogBuildRequest(
    MemorySearchEligibility Eligibility,
    MemoryReaderCatalogBuildCursor? Cursor);

/// <summary>One bounded selected-column traversal result for a server-side catalog builder.</summary>
public sealed record MemoryReaderCatalogBuildPortion(
    IReadOnlyList<MemoryReaderCatalogSourceRecord> Records,
    MemoryReaderCatalogBuildCursor? NextCursor);

/// <summary>
/// A server-only input row for a durable immutable leaf. It contains no browser-facing authority and is persisted
/// under the generation selected by the catalog builder.
/// </summary>
public sealed record MemoryReaderCatalogLeafEntry(
    int Position,
    MemoryId MemoryId,
    MemoryRecordType Type,
    string Preview,
    DateTimeOffset UpdatedAt,
    long Version);

/// <summary>One immutable ready leaf with internal durable identifiers for Core route resolution only.</summary>
public sealed record MemoryReaderCatalogLeafPage(
    string GenerationKey,
    IReadOnlyList<MemoryReaderCatalogLeafEntry> Entries,
    MemoryReaderCatalogCursor? NextCursor);

public enum MemoryReaderCatalogState
{
    Available,
    Stale,
    Changed,
    NotFound,
    Unavailable,
    CatalogNotReady
}

/// <summary>One browser-facing catalog leaf item. RecordHref is an opaque local reader route, never a durable id.</summary>
public sealed record MemoryReaderCatalogDocument(
    string RecordHref,
    MemoryRecordType Type,
    string Title,
    string Namespace,
    IReadOnlyList<string> Tags,
    string Preview,
    DateTimeOffset UpdatedAt);

/// <summary>One browser-safe namespace or tag locator, label and exact-generation eligible count.</summary>
public sealed record MemoryReaderCatalogFacet(string Locator, string Label, int Count);

/// <summary>Contains one immutable catalog leaf or a privacy-safe state without source identifiers.</summary>
public sealed record MemoryReaderCatalogPage(
    MemoryReaderCatalogState State,
    IReadOnlyList<MemoryReaderCatalogDocument> Documents,
    IReadOnlyList<MemoryReaderCatalogFacet> Namespaces,
    IReadOnlyList<MemoryReaderCatalogFacet> Tags,
    MemoryReaderCatalogCursor? NextCursor,
    ContractVersion ContractVersion);

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
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<string> Entities);

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

public enum MemoryReaderDocumentState { Available, NotFound, Changed, Stale, Unavailable, CatalogNotReady }

public sealed record MemoryReaderDocumentPage(
    MemoryReaderDocumentState State,
    string RouteKey,
    IReadOnlyList<MemoryReaderBlock> Blocks,
    MemoryReaderBlockCursor? NextCursor,
    long? RecordVersion,
    ContractVersion ContractVersion);
