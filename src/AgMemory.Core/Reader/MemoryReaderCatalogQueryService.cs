using System.Text;
using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Builds browser-safe pages from immutable catalog leaves and compiled wiki metadata.</summary>
public sealed class MemoryReaderCatalogQueryService : IMemoryReaderCatalogQueryService
{
    private readonly IMemoryReaderCatalogSource _catalog;
    private readonly IMemoryReaderSource _reader;
    private readonly IAuthorizationScopeValidator _authorization;
    private readonly IClock _clock;
    private readonly ContractVersion _supportedContractVersion;
    private readonly IMemoryWikiMetadataStore? _metadata;

    public MemoryReaderCatalogQueryService(
        IMemoryReaderCatalogSource catalog,
        IMemoryReaderSource reader,
        IAuthorizationScopeValidator authorization,
        IClock clock,
        ContractVersion supportedContractVersion,
        IMemoryWikiMetadataStore? metadata = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        supportedContractVersion.Validate(nameof(supportedContractVersion));
        _supportedContractVersion = supportedContractVersion;
        _metadata = metadata;
    }

    public async Task<MemoryReaderCatalogPage> BrowseAsync(MemoryReaderCatalogRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request)) return Result(MemoryReaderCatalogState.Unavailable, [], [], [], null, request?.ContractVersion);
        var eligibility = await AuthorizeExactAsync(request.Actor, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (eligibility is null) return Result(MemoryReaderCatalogState.NotFound, [], [], [], null, request.ContractVersion);

        try
        {
            // This leaf anchors all facet counts to one immutable generation before anything is disclosed.
            var firstLeaf = await _catalog.ReadReadyLeafPageAsync(eligibility, null, cancellationToken).ConfigureAwait(false);
            if (firstLeaf is null)
                return Result(request.Cursor is null ? MemoryReaderCatalogState.CatalogNotReady : MemoryReaderCatalogState.Changed,
                    [], [], [], null, request.ContractVersion);
            if (request.Cursor is not null && !string.Equals(request.Cursor.GenerationKey, firstLeaf.GenerationKey, StringComparison.Ordinal))
                return Result(MemoryReaderCatalogState.Changed, [], [], [], null, request.ContractVersion);

            var facets = await ReadFacetsAsync(eligibility, firstLeaf, cancellationToken).ConfigureAwait(false);
            if (facets is null) return Result(MemoryReaderCatalogState.Changed, [], [], [], null, request.ContractVersion);

            var documents = new List<MemoryReaderCatalogDocument>(MemoryReaderLimits.DocumentsPerPage);
            var cursor = request.Cursor;
            MemoryReaderCatalogCursor? next = null;
            for (var scan = 0; scan < MemoryReaderLimits.MaximumCatalogLeafScansPerPage && documents.Count < MemoryReaderLimits.DocumentsPerPage; scan++)
            {
                var leaves = await _catalog.ReadReadyLeafPageAsync(eligibility, cursor, cancellationToken).ConfigureAwait(false);
                if (leaves is null)
                    return Result(MemoryReaderCatalogState.Changed, [], [], [], null, request.ContractVersion);

                foreach (var leaf in leaves.Entries)
                {
                    var document = await DescribeLeafAsync(eligibility, leaf, cancellationToken).ConfigureAwait(false);
                    // The reader is a curated Wiki view, not a dump of transport events. Records without metadata
                    // need meaningful preview text before they can be shown through the safe fallback projection.
                    if (document is null) continue;
                    if (!Matches(document, leaf.Preview, request.Filter)) continue;
                    var route = await _reader.GetOrCreateRouteAsync(leaf.MemoryId, cancellationToken).ConfigureAwait(false);
                    documents.Add(new($"/memory-reader/{Uri.EscapeDataString(route.RouteKey)}", leaf.Type, document.Title,
                        document.Namespace, document.Tags, Preview(leaf.Preview), leaf.UpdatedAt));
                    if (documents.Count == MemoryReaderLimits.DocumentsPerPage)
                    {
                        next = new(leaves.GenerationKey, leaf.Position + 1);
                        break;
                    }
                }

                if (documents.Count == MemoryReaderLimits.DocumentsPerPage) break;
                next = leaves.NextCursor;
                if (next is null) break;
                cursor = next;
            }
            return Result(MemoryReaderCatalogState.Available, documents, facets.Namespaces, facets.Tags, next, request.ContractVersion,
                firstLeaf.GenerationKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(MemoryReaderCatalogState.Unavailable, [], [], [], null, request.ContractVersion);
        }
    }

    private async Task<MemorySearchEligibility?> AuthorizeExactAsync(ActorId actor, MemoryScope scope, CancellationToken cancellationToken)
    {
        try
        {
            var authorization = await _authorization.AuthorizeAsync(actor, MemoryOperation.ReaderCatalogRead, scope, cancellationToken).ConfigureAwait(false);
            if (!authorization.IsAllowed || !authorization.AuthorizedScopes!.Contains(scope)) return null;
            return new(new AuthorizedScopeSet([new ScopeSelector(scope)]), null, UtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private bool IsValid(MemoryReaderCatalogRequest? request)
    {
        if (request is null || request.ContractVersion != _supportedContractVersion || request.Filter is null ||
            request.Cursor is { NextLeafPosition: < 0 } || request.Cursor is { GenerationKey.Length: > 128 }) return false;
        if (!TryNormalizeFilter(request.Filter.Namespace, request.Filter.Tag, request.Filter.Search, out var normalizedFilter) || normalizedFilter != request.Filter)
            return false;
        try
        {
            request.Actor.Validate(nameof(request.Actor));
            request.RequestedScope.Validate();
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private DateTimeOffset UtcNow() => _clock.UtcNow.Offset == TimeSpan.Zero ? _clock.UtcNow : _clock.UtcNow.ToUniversalTime();

    private async Task<FacetSet?> ReadFacetsAsync(
        MemorySearchEligibility eligibility,
        MemoryReaderCatalogLeafPage firstLeaf,
        CancellationToken cancellationToken)
    {
        var namespaces = new Dictionary<string, int>(StringComparer.Ordinal);
        var tags = new SortedDictionary<string, FacetCounter>(StringComparer.Ordinal);
        var leaves = firstLeaf;
        while (true)
        {
            foreach (var leaf in leaves.Entries)
            {
                var document = await DescribeLeafAsync(eligibility, leaf, cancellationToken).ConfigureAwait(false);
                if (document is null) continue;
                foreach (var @namespace in NamespaceHierarchy(document.Namespace))
                    namespaces[@namespace] = namespaces.GetValueOrDefault(@namespace) + 1;
                foreach (var tag in document.Tags.Select(TagFacet.FromTag)) AddBoundedTag(tags, tag);
            }

            if (leaves.NextCursor is null) break;
            var next = await _catalog.ReadReadyLeafPageAsync(eligibility, leaves.NextCursor, cancellationToken).ConfigureAwait(false);
            if (next is null || !string.Equals(next.GenerationKey, firstLeaf.GenerationKey, StringComparison.Ordinal)) return null;
            leaves = next;
        }

        return new(
            namespaces.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new MemoryReaderCatalogFacet(pair.Key, pair.Key, pair.Value)).ToArray(),
            tags.Select(pair => new MemoryReaderCatalogFacet(pair.Key, pair.Value.Label, pair.Value.Count)).ToArray());
    }

    private static void AddBoundedTag(SortedDictionary<string, FacetCounter> tags, TagFacet tag)
    {
        if (tags.TryGetValue(tag.Locator, out var existing))
        {
            tags[tag.Locator] = existing with { Count = existing.Count + 1 };
            return;
        }

        if (tags.Count < MemoryReaderLimits.MaximumCatalogTagFacets)
        {
            tags.Add(tag.Locator, new(tag.Label, 1));
            return;
        }

        var largest = tags.Last();
        if (string.CompareOrdinal(tag.Locator, largest.Key) >= 0) return;
        tags.Remove(largest.Key);
        tags.Add(tag.Locator, new(tag.Label, 1));
    }

    private static bool Matches(WikiDocument document, string preview, MemoryReaderCatalogFilter filter) =>
        (filter.Namespace is null || string.Equals(filter.Namespace, document.Namespace, StringComparison.Ordinal) ||
         document.Namespace.StartsWith(string.Concat(filter.Namespace, "/"), StringComparison.Ordinal)) &&
        (filter.Tag is null || document.Tags.Select(TagFacet.FromTag).Any(tag => string.Equals(tag.Locator, filter.Tag, StringComparison.Ordinal))) &&
        (filter.Search is null || string.Concat(document.Title, "\n", Preview(preview)).Contains(filter.Search, StringComparison.OrdinalIgnoreCase));

    public static string NamespaceOf(MemoryRecordType type) => $"type/{type.ToString().ToLowerInvariant()}";

    public static bool TryNormalizeFilter(string? @namespace, string? tag, out MemoryReaderCatalogFilter filter)
        => TryNormalizeFilter(@namespace, tag, null, out filter);

    public static bool TryNormalizeFilter(string? @namespace, string? tag, string? search, out MemoryReaderCatalogFilter filter)
    {
        filter = MemoryReaderCatalogFilter.Empty;
        @namespace = string.IsNullOrEmpty(@namespace) ? null : @namespace;
        tag = string.IsNullOrEmpty(tag) ? null : tag;
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        if (!string.IsNullOrEmpty(@namespace) && !IsCanonicalNamespace(@namespace)) return false;
        if (!string.IsNullOrEmpty(tag) && !IsTagLocator(tag)) return false;
        if (search is { Length: > MemoryReaderLimits.MaximumCatalogSearchCharacters } || search?.Any(char.IsControl) == true) return false;
        filter = new(@namespace, tag, search);
        return true;
    }

    private static bool IsTagLocator(string value)
    {
        if (value.Length != 68 || !value.StartsWith("tag/", StringComparison.Ordinal)) return false;
        return value[4..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private async Task<WikiDocument?> DescribeLeafAsync(
        MemorySearchEligibility eligibility,
        MemoryReaderCatalogLeafEntry leaf,
        CancellationToken cancellationToken)
    {
        var scope = eligibility.AuthorizedScopes.Selectors[0].Scope;
        var metadata = _metadata is null
            ? null
            : await _metadata.ReadWikiMetadataAsync(scope, leaf.MemoryId, leaf.Version, cancellationToken).ConfigureAwait(false);
        // Events are operational history. Show them only when intentionally promoted to a Wiki page via metadata.
        // Other records can use the fallback only when the stored preview contains real visible content.
        if (metadata is null && (leaf.Type == MemoryRecordType.Event || string.IsNullOrWhiteSpace(leaf.Preview))) return null;
        return new(
            metadata?.Title ?? FallbackTitle(leaf.Preview),
            metadata?.Namespace ?? "inbox",
            metadata?.Tags ?? []);
    }

    private static IEnumerable<string> NamespaceHierarchy(string @namespace)
    {
        var segments = @namespace.Split('/');
        for (var index = 1; index <= segments.Length; index++) yield return string.Join('/', segments.Take(index));
    }

    private static bool IsCanonicalNamespace(string value)
    {
        var segments = value.Split('/');
        return segments.Length is > 0 and <= MemoryWikiLimits.MaximumNamespaceSegments &&
            segments.All(segment => segment.Length is > 0 and <= MemoryWikiLimits.MaximumNamespaceSegmentCharacters &&
                segment.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'));
    }

    private static string FallbackTitle(string text)
    {
        var heading = text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal));
        var title = heading is null ? Preview(text) : heading[2..].Trim();
        return title.Length <= MemoryWikiLimits.MaximumTitleCharacters
            ? title
            : string.Concat(title.AsSpan(0, MemoryWikiLimits.MaximumTitleCharacters - 1), "…");
    }

    private sealed record FacetSet(IReadOnlyList<MemoryReaderCatalogFacet> Namespaces, IReadOnlyList<MemoryReaderCatalogFacet> Tags);
    private sealed record WikiDocument(string Title, string Namespace, IReadOnlyList<string> Tags);
    private sealed record FacetCounter(string Label, int Count);
    private sealed record TagFacet(string Locator, string Label)
    {
        public static TagFacet FromTag(string tag)
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(tag))).ToLowerInvariant();
            return new($"tag/{hash}", tag);
        }
    }

    private static string Preview(string content)
    {
        var normalized = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length == 0) return "Запись без текста";
        return normalized.Length <= MemoryReaderLimits.MaximumCatalogPreviewCharacters
            ? normalized
            : string.Concat(normalized.AsSpan(0, MemoryReaderLimits.MaximumCatalogPreviewCharacters - 1), "…");
    }

    private MemoryReaderCatalogPage Result(
        MemoryReaderCatalogState state,
        IReadOnlyList<MemoryReaderCatalogDocument> documents,
        IReadOnlyList<MemoryReaderCatalogFacet> namespaces,
        IReadOnlyList<MemoryReaderCatalogFacet> tags,
        MemoryReaderCatalogCursor? next,
        ContractVersion? version,
        string? generationKey = null) => new(state, documents, namespaces, tags, next, version ?? _supportedContractVersion, generationKey);
}
