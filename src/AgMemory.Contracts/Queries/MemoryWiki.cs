using System.Text.RegularExpressions;

namespace AgMemory.Contracts;

/// <summary>One editable, exact-version wiki description. It is durable source data, never a browser authority token.</summary>
public sealed record MemoryWikiMetadata(
    MemoryScope Scope,
    MemoryId MemoryId,
    long RecordVersion,
    string? Title,
    string? Namespace,
    string? Slug,
    IReadOnlyList<string> Tags)
{
    private static readonly Regex SlugPattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex NamespaceSegmentPattern = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant);

    public void Validate()
    {
        Scope.Validate();
        MemoryId.Validate(nameof(MemoryId));
        if (RecordVersion <= 0) throw new ArgumentOutOfRangeException(nameof(RecordVersion));
        if (Title is { Length: > MemoryWikiLimits.MaximumTitleCharacters }) throw new ArgumentOutOfRangeException(nameof(Title));
        if (Namespace is not null && !MemoryWikiLimits.IsNamespace(Namespace, NamespaceSegmentPattern)) throw new ArgumentException("Namespace is not canonical.", nameof(Namespace));
        if (Slug is not null && (Slug.Length > MemoryWikiLimits.MaximumSlugCharacters || !SlugPattern.IsMatch(Slug)))
            throw new ArgumentException("Slug is not canonical.", nameof(Slug));
        ArgumentNullException.ThrowIfNull(Tags);
        if (Tags.Count > MemoryWikiLimits.MaximumTagsPerDocument || Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > MemoryWikiLimits.MaximumTagCharacters) ||
            Tags.Distinct(StringComparer.Ordinal).Count() != Tags.Count)
            throw new ArgumentException("Tags are invalid.", nameof(Tags));
    }
}

public static class MemoryWikiLimits
{
    public const int DocumentsPerPage = 20;
    public const int MaximumFacetEntries = 50;
    public const int MaximumTagsPerDocument = 16;
    public const int MaximumTagCharacters = 48;
    public const int MaximumNamespaceSegments = 6;
    public const int MaximumNamespaceSegmentCharacters = 32;
    public const int MaximumTitleCharacters = 160;
    public const int MaximumSlugCharacters = 64;
    public const int MaximumChildrenPerDocument = 64;
    public const int MaximumRelatedPerDocument = 64;
    public const int MaximumBacklinksPerPage = 50;

    internal static bool IsNamespace(string value, Regex segmentPattern) =>
        value.Length > 0 && value.Split('/').Length <= MaximumNamespaceSegments &&
        value.Split('/').All(segment => segmentPattern.IsMatch(segment));
}

/// <summary>Durable editable metadata source. Writes are server-side and invalidate the matching reader generation.</summary>
public interface IMemoryWikiMetadataStore
{
    Task UpsertWikiMetadataAsync(MemoryWikiMetadata metadata, CancellationToken cancellationToken);
    Task<MemoryWikiMetadata?> ReadWikiMetadataAsync(MemoryScope scope, MemoryId memoryId, long recordVersion, CancellationToken cancellationToken);
}

/// <summary>Explicit wiki topology: Child is hierarchy, Related is a cross-link, Backlink is the hierarchy inverse.</summary>
public enum MemoryWikiRelationKind { Child, Related, Backlink }

/// <summary>Server-only resolved relation from one immutable reader generation.</summary>
public sealed record MemoryWikiRelationRecord(
    MemoryWikiRelationKind Kind,
    MemoryId TargetMemoryId,
    string Label,
    string Title,
    string Namespace,
    long TargetVersion,
    int SharedEntityCount);

public interface IMemoryReaderWikiRelationSource
{
    Task<IReadOnlyList<MemoryWikiRelationRecord>> ReadWikiRelationsAsync(
        MemorySearchEligibility eligibility,
        string generationKey,
        MemoryId sourceMemoryId,
        MemoryWikiRelationKind kind,
        CancellationToken cancellationToken);
}
