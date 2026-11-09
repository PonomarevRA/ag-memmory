namespace AgMemory.Contracts;

/// <summary>Hard ceilings for the local operational records browser.</summary>
public static class MemoryRecordBrowserLimits
{
    public const int PageSize = 25;
    public const int MaximumSourceRecords = 500;
    public const int MaximumPreviewCharacters = 320;
    public const int MaximumSearchCharacters = 120;
}

public enum MemoryRecordBrowserSort { UpdatedDescending, UpdatedAscending }

/// <summary>Server-only continuation. Web protects it before a browser receives it.</summary>
public sealed record MemoryRecordBrowserCursor(string GenerationKey, int Offset);
public sealed record MemoryRecordBrowserFilter(MemoryRecordType? Type, string? Namespace, string? Tag, string? Search, MemoryRecordBrowserSort Sort);
public sealed record MemoryRecordBrowserRequest(ActorId Actor, MemoryScope RequestedScope, MemoryRecordBrowserCursor? Cursor, MemoryRecordBrowserFilter Filter, ContractVersion ContractVersion);

/// <summary>Selected source columns only. This is never a browser DTO.</summary>
public sealed record MemoryRecordBrowserSourceRecord(MemoryId MemoryId, MemoryScope Scope, MemoryRecordType Type, MemoryLifecycleStatus Status,
    string CanonicalText, DateTimeOffset UpdatedAt, long Version, DateTimeOffset? ExpiresAt, IReadOnlyList<string> Entities);
public sealed record MemoryRecordBrowserSourceRequest(MemorySearchEligibility Eligibility);
/// <summary>Safe browser projection. It deliberately has no durable id or reader route.</summary>
public sealed record MemoryRecordBrowserItem(MemoryRecordType Type, string Title, string Namespace, IReadOnlyList<string> Tags, string Preview, DateTimeOffset UpdatedAt);
public enum MemoryRecordBrowserState { Available, NotFound, Changed, Unavailable }
public sealed record MemoryRecordBrowserPage(MemoryRecordBrowserState State, IReadOnlyList<MemoryRecordBrowserItem> Records, MemoryRecordBrowserCursor? NextCursor, ContractVersion ContractVersion, string? GenerationKey = null);
