namespace AgMemory.Contracts;

/// <summary>Bounds for current, content-bearing reader cards; routes never grant authority.</summary>
public static class MemoryReaderCardLimits
{
    public const int MaximumCards = 25;
    public const int MaximumTitleCharacters = 96;
    public const int MaximumPreviewCharacters = 320;

    /// <summary>Accepts only the opaque route alphabet, before any storage access.</summary>
    public static bool IsRouteKey(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}

/// <summary>Current selected content from an exact eligible scope, never a cached catalog label.</summary>
public sealed record MemoryReaderCardSource(string RouteKey, MemoryRecordType Type, long Version, string CanonicalText);

/// <summary>Reads at most25 existing routes in one bounded batch; unknown/ineligible routes are omitted.</summary>
public interface IMemoryReaderCardSource
{
    Task<IReadOnlyList<MemoryReaderCardSource>> ReadCardsAsync(
        MemorySearchEligibility eligibility, IReadOnlyList<string> routeKeys, CancellationToken cancellationToken);
}

/// <summary>Plain reader content. It contains no durable IDs, scope, actor or storage configuration fields.</summary>
public sealed record MemoryReaderCard(string RouteKey, string Title, string Preview, long Version);
