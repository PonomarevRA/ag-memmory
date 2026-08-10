using System.Text;
using System.Text.RegularExpressions;
using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>
/// Builds a bounded, content-bearing view of one exact-scope memory record. The caller owns transport-token
/// protection; this service never accepts browser-supplied authority or exposes storage IDs as routes.
/// </summary>
public sealed class MemoryReaderQueryService : IMemoryReaderQueryService
{
    private static readonly Regex AnchoredHeading = new(
        @"^(?<hashes>#{1,6})\s+(?<heading>.*?)\s+\^(?<key>[a-z][a-z0-9-]{0,63})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(
        @"^#{1,6}\s+(?<heading>.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WikiLink = new(
        @"\[\[(?<target>[^\]|]{1,320})(?:\|(?<label>[^\]]{0,256}))?\]\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ParagraphBoundary = new(
        @"\n[ \t]*\n",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BlockKey = new(
        @"^[a-z][a-z0-9-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IMemoryReaderSource _source;
    private readonly IAuthorizationScopeValidator _authorization;
    private readonly IClock _clock;
    private readonly ContractVersion _supportedContractVersion;

    public MemoryReaderQueryService(
        IMemoryReaderSource source,
        IAuthorizationScopeValidator authorization,
        IClock clock,
        ContractVersion supportedContractVersion)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        supportedContractVersion.Validate(nameof(supportedContractVersion));
        _supportedContractVersion = supportedContractVersion;
    }

    public async Task<MemoryReaderDocumentPage> ReadHomeAsync(
        MemoryReaderHomeRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsValid(request, out _)) return Unavailable(request?.ContractVersion);
        var eligibility = await AuthorizeExactAsync(request.Actor, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (eligibility is null) return NotFound(string.Empty, request.ContractVersion);

        try
        {
            var record = await _source.ReadByIdAsync(eligibility, request.HomeMemoryId, cancellationToken).ConfigureAwait(false);
            if (record is null || !IsEligible(record, eligibility)) return NotFound(string.Empty, request.ContractVersion);
            var route = await _source.GetOrCreateRouteAsync(record.MemoryId, cancellationToken).ConfigureAwait(false);
            return await BuildPageAsync(record, route.RouteKey, null, null, eligibility, request.ContractVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable(request.ContractVersion);
        }
    }

    public async Task<MemoryReaderDocumentPage> ReadDocumentAsync(
        MemoryReaderDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsValid(request, out _)) return Unavailable(request?.ContractVersion);
        var eligibility = await AuthorizeExactAsync(request.Actor, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (eligibility is null) return NotFound(request.RouteKey, request.ContractVersion);

        try
        {
            var memoryId = await _source.ResolveRouteAsync(request.RouteKey, cancellationToken).ConfigureAwait(false);
            if (memoryId is null) return NotFound(request.RouteKey, request.ContractVersion);
            var record = await _source.ReadByIdAsync(eligibility, memoryId.Value, cancellationToken).ConfigureAwait(false);
            if (record is null || !IsEligible(record, eligibility)) return NotFound(request.RouteKey, request.ContractVersion);
            return await BuildPageAsync(record, request.RouteKey, request.RequestedBlockKey, request.BlockCursor,
                eligibility, request.ContractVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable(request.ContractVersion);
        }
    }

    private async Task<MemoryReaderDocumentPage> BuildPageAsync(
        MemoryReaderSourceRecord record,
        string routeKey,
        string? requestedBlockKey,
        MemoryReaderBlockCursor? cursor,
        MemorySearchEligibility eligibility,
        ContractVersion contractVersion,
        CancellationToken cancellationToken)
    {
        if (record.CanonicalText.Length > MemoryReaderLimits.MaximumDocumentCharacters)
            return Unavailable(contractVersion);

        var blocks = ParseBlocks(record.CanonicalText);
        if (blocks is null) return Unavailable(contractVersion);
        if (cursor is not null && cursor.Version != record.Version)
            return new(MemoryReaderDocumentState.Changed, routeKey, [], null, contractVersion);

        var start = cursor?.NextBlockIndex ?? FindStart(blocks, requestedBlockKey);
        if (start < 0 || start >= blocks.Count)
            return NotFound(routeKey, contractVersion);

        var routes = new Dictionary<(MemoryId MemoryId, string BlockKey), MemoryReaderRoute?>();
        var localBlockKeys = blocks.Select(block => block.Key).ToHashSet(StringComparer.Ordinal);
        var visible = new List<MemoryReaderBlock>(MemoryReaderLimits.BlocksPerPage);
        foreach (var block in blocks.Skip(start).Take(MemoryReaderLimits.BlocksPerPage))
        {
            var content = await RenderInlinesAsync(block.Content, routeKey, localBlockKeys, eligibility, routes, cancellationToken)
                .ConfigureAwait(false);
            visible.Add(new(block.Key, block.Heading, content));
        }

        var nextIndex = start + visible.Count;
        var next = nextIndex < blocks.Count ? new MemoryReaderBlockCursor(record.Version, nextIndex) : null;
        return new(MemoryReaderDocumentState.Available, routeKey, visible, next, contractVersion);
    }

    private async Task<IReadOnlyList<MemoryReaderInline>> RenderInlinesAsync(
        string content,
        string currentRouteKey,
        IReadOnlySet<string> localBlockKeys,
        MemorySearchEligibility eligibility,
        IDictionary<(MemoryId MemoryId, string BlockKey), MemoryReaderRoute?> routes,
        CancellationToken cancellationToken)
    {
        var inlines = new List<MemoryReaderInline>();
        var position = 0;
        var linkCount = 0;
        foreach (Match match in WikiLink.Matches(content))
        {
            if (match.Index > position)
                inlines.Add(new(MemoryReaderInlineKind.Text, content[position..match.Index]));
            position = match.Index + match.Length;
            var link = linkCount++ < MemoryReaderLimits.MaximumLinksPerBlock
                ? await TryRenderLinkAsync(match, currentRouteKey, localBlockKeys, eligibility, routes, cancellationToken).ConfigureAwait(false)
                : null;
            if (link is null)
            {
                inlines.Add(new(MemoryReaderInlineKind.Text, match.Value));
            }
            else
            {
                inlines.Add(link);
            }
        }

        if (position < content.Length)
            inlines.Add(new(MemoryReaderInlineKind.Text, content[position..]));
        return inlines;
    }

    private static int FindStart(IReadOnlyList<ParsedBlock> blocks, string? requestedBlockKey)
    {
        if (string.IsNullOrWhiteSpace(requestedBlockKey)) return 0;
        for (var index = 0; index < blocks.Count; index++)
            if (string.Equals(blocks[index].Key, requestedBlockKey, StringComparison.Ordinal)) return index;
        return -1;
    }

    private async Task<MemoryReaderInline?> TryRenderLinkAsync(
        Match match,
        string currentRouteKey,
        IReadOnlySet<string> localBlockKeys,
        MemorySearchEligibility eligibility,
        IDictionary<(MemoryId MemoryId, string BlockKey), MemoryReaderRoute?> routes,
        CancellationToken cancellationToken)
    {
        var target = match.Groups["target"].Value.Trim();
        var label = match.Groups["label"].Success ? match.Groups["label"].Value.Trim() : string.Empty;
        if (label.Length == 0)
            label = target.StartsWith("memory:", StringComparison.Ordinal) ? "Открыть связанный документ" : "Открыть связанный блок";
        if (label.Length == 0 || label.Any(char.IsControl)) return null;

        if (target.StartsWith('#'))
        {
            var key = target[1..];
            return BlockKey.IsMatch(key) && localBlockKeys.Contains(key)
                ? new(MemoryReaderInlineKind.Link, label, currentRouteKey, key)
                : null;
        }

        const string prefix = "memory:";
        if (!target.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var targetValue = target[prefix.Length..];
        var separator = targetValue.IndexOf('#');
        if (separator <= 0 || separator == targetValue.Length - 1) return null;
        var encodedId = targetValue[..separator];
        var blockKey = targetValue[(separator + 1)..];
        if (!BlockKey.IsMatch(blockKey) || !TryDecodeMemoryId(encodedId, out var memoryId)) return null;

        var routeKey = (memoryId, blockKey);
        if (!routes.TryGetValue(routeKey, out var route))
        {
            var record = await _source.ReadByIdAsync(eligibility, memoryId, cancellationToken).ConfigureAwait(false);
            var remoteBlocks = record is null || !IsEligible(record, eligibility) ||
                               record.CanonicalText.Length > MemoryReaderLimits.MaximumDocumentCharacters
                ? null
                : ParseBlocks(record.CanonicalText);
            route = remoteBlocks?.Any(block => block.IsExplicit && string.Equals(block.Key, blockKey, StringComparison.Ordinal)) == true
                ? await _source.GetOrCreateRouteAsync(memoryId, cancellationToken).ConfigureAwait(false)
                : null;
            routes[routeKey] = route;
        }
        return route is null ? null : new(MemoryReaderInlineKind.Link, label, route.RouteKey, blockKey);
    }

    private static bool TryDecodeMemoryId(string encoded, out MemoryId memoryId)
    {
        memoryId = default;
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 256) return false;
        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            memoryId = new(value);
            memoryId.Validate(nameof(encoded));
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }
    }

    private static IReadOnlyList<ParsedBlock>? ParseBlocks(string content)
    {
        var source = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var blocks = new List<ParsedBlockBuilder> { new("root", null, false) };
        var keys = new HashSet<string>(StringComparer.Ordinal) { "root" };
        var auto = 0;
        foreach (var line in source.Split('\n'))
        {
            var anchored = AnchoredHeading.Match(line);
            if (anchored.Success)
            {
                var key = anchored.Groups["key"].Value;
                if (!keys.Add(key)) return null;
                blocks.Add(new(key, anchored.Groups["heading"].Value.Trim(), true));
                continue;
            }

            var heading = Heading.Match(line);
            if (heading.Success)
            {
                var key = $"auto-{++auto}";
                blocks.Add(new(key, heading.Groups["heading"].Value.Trim(), false));
                continue;
            }

            var builder = blocks[^1];
            if (builder.Content.Length > 0) builder.Content.Append('\n');
            builder.Content.Append(line);
        }

        var result = new List<ParsedBlock>();
        foreach (var block in blocks.Where(block => block.Content.Length > 0 || block.Heading is not null))
        {
            foreach (var part in SplitBlock(block.Key, block.Heading, block.IsExplicit, block.Content.ToString()))
            {
                result.Add(part);
                if (result.Count > MemoryReaderLimits.MaximumBlocksPerDocument) return null;
            }
        }
        return result.Count == 0 ? [new("root", null, false, string.Empty)] : result;
    }

    private static IEnumerable<ParsedBlock> SplitBlock(string key, string? heading, bool isExplicit, string content)
    {
        if (content.Length <= MemoryReaderLimits.MaximumBlockCharacters)
        {
            yield return new(key, heading, isExplicit, content);
            yield break;
        }

        var remaining = content;
        var part = 1;
        while (remaining.Length > 0)
        {
            var length = Math.Min(remaining.Length, MemoryReaderLimits.MaximumBlockCharacters);
            if (length < remaining.Length)
            {
                var paragraphBreaks = ParagraphBoundary.Matches(remaining[..length]);
                if (paragraphBreaks.Count > 0)
                    length = paragraphBreaks[paragraphBreaks.Count - 1].Index + paragraphBreaks[paragraphBreaks.Count - 1].Length;
                // A paragraph longer than the hard limit has no blank-line boundary in range. Splitting at the
                // limit is the explicit fallback; normal paragraphs are never split at a soft line break.
            }
            yield return new(part == 1 ? key : $"{key}--{part}", part == 1 ? heading : null, part == 1 && isExplicit, remaining[..length]);
            remaining = remaining[length..];
            part++;
        }
    }

    private async Task<MemorySearchEligibility?> AuthorizeExactAsync(
        ActorId actor,
        MemoryScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            var authorization = await _authorization.AuthorizeAsync(actor, MemoryOperation.ReaderRead, scope, cancellationToken)
                .ConfigureAwait(false);
            if (!authorization.IsAllowed || !authorization.AuthorizedScopes!.Contains(scope)) return null;
            return new(new AuthorizedScopeSet([new ScopeSelector(scope)]), null, UtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsEligible(MemoryReaderSourceRecord record, MemorySearchEligibility eligibility) =>
        eligibility.AuthorizedScopes.Contains(record.Scope) &&
        record.Status == MemoryLifecycleStatus.Active &&
        (record.ExpiresAt is null || record.ExpiresAt > eligibility.AsOfUtc);

    private bool IsValid(MemoryReaderHomeRequest? request, out MemoryError? error)
    {
        error = null;
        if (request is null || request.ContractVersion != _supportedContractVersion) return false;
        try
        {
            request.Actor.Validate(nameof(request.Actor));
            request.RequestedScope.Validate();
            request.HomeMemoryId.Validate(nameof(request.HomeMemoryId));
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private bool IsValid(MemoryReaderDocumentRequest? request, out MemoryError? error)
    {
        error = null;
        if (request is null || request.ContractVersion != _supportedContractVersion ||
            string.IsNullOrWhiteSpace(request.RouteKey) || request.RouteKey.Length > 128 ||
            (request.RequestedBlockKey is not null && !BlockKey.IsMatch(request.RequestedBlockKey))) return false;
        try
        {
            request.Actor.Validate(nameof(request.Actor));
            request.RequestedScope.Validate();
            return request.BlockCursor is null || request.BlockCursor.NextBlockIndex >= 0 && request.BlockCursor.Version > 0;
        }
        catch (ArgumentException) { return false; }
    }

    private DateTimeOffset UtcNow()
    {
        var now = _clock.UtcNow;
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }

    private MemoryReaderDocumentPage NotFound(string routeKey, ContractVersion contractVersion) =>
        new(MemoryReaderDocumentState.NotFound, routeKey, [], null, contractVersion);

    private MemoryReaderDocumentPage Unavailable(ContractVersion? contractVersion) =>
        new(MemoryReaderDocumentState.Unavailable, string.Empty, [], null, contractVersion ?? _supportedContractVersion);

    private sealed class ParsedBlockBuilder(string key, string? heading, bool isExplicit)
    {
        public string Key { get; } = key;
        public string? Heading { get; } = heading;
        public bool IsExplicit { get; } = isExplicit;
        public StringBuilder Content { get; } = new();
    }

    private sealed record ParsedBlock(string Key, string? Heading, bool IsExplicit, string Content);
}
