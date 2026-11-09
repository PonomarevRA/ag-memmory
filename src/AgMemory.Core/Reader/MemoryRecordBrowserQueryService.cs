using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Projects an exact authorised scope into a bounded, non-sensitive operational list.</summary>
public sealed class MemoryRecordBrowserQueryService : IMemoryRecordBrowserQueryService
{
    private readonly IMemoryRecordBrowserSource _source;
    private readonly IAuthorizationScopeValidator _authorization;
    private readonly IClock _clock;
    private readonly ContractVersion _version;

    public MemoryRecordBrowserQueryService(IMemoryRecordBrowserSource source, IAuthorizationScopeValidator authorization, IClock clock, ContractVersion version)
    { _source = source; _authorization = authorization; _clock = clock; _version = version; }

    public async Task<MemoryRecordBrowserPage> BrowseAsync(MemoryRecordBrowserRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request)) return Result(MemoryRecordBrowserState.Unavailable, [], null, request?.ContractVersion);
        var allowed = await _authorization.AuthorizeAsync(request.Actor, MemoryOperation.RecordBrowserRead, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (!allowed.IsAllowed || !allowed.AuthorizedScopes!.Contains(request.RequestedScope)) return Result(MemoryRecordBrowserState.NotFound, [], null, request.ContractVersion);
        try
        {
            var eligibility = new MemorySearchEligibility(new AuthorizedScopeSet([new ScopeSelector(request.RequestedScope)]), null, UtcNow());
            var candidates = await _source.ReadAsync(new(eligibility), cancellationToken).ConfigureAwait(false);
            // The adapter is expected to apply the exact predicate; retain the same check here so an over-broad adapter cannot leak a neighbouring scope.
            var filtered = candidates.Where(record => record.Scope == request.RequestedScope && Matches(record, request.Filter)).OrderBy(record => record, Comparer<MemoryRecordBrowserSourceRecord>.Create((a,b) => Compare(a,b,request.Filter.Sort))).ToArray();
            // Bind continuations to the selected-column snapshot, not merely its scope. Any insert, removal or record update makes the old offset unsafe.
            var generation = Generation(request.RequestedScope, request.Filter, filtered);
            if (request.Cursor is not null && (request.Cursor.GenerationKey != generation || request.Cursor.Offset < 0)) return Result(MemoryRecordBrowserState.Changed, [], null, request.ContractVersion);
            var start = request.Cursor?.Offset ?? 0;
            if (start > filtered.Length) return Result(MemoryRecordBrowserState.Changed, [], null, request.ContractVersion);
            var selected = filtered.Skip(start).Take(MemoryRecordBrowserLimits.PageSize).ToArray();
            var items = new List<MemoryRecordBrowserItem>(selected.Length);
            foreach (var record in selected)
            {
                var (title, preview) = Text(record.CanonicalText);
                items.Add(new(record.Type, title, Namespace(record.Type), Tags(record.Entities), preview, record.UpdatedAt));
            }
            var next = start + selected.Length < filtered.Length ? new MemoryRecordBrowserCursor(generation, start + selected.Length) : null;
            return new(MemoryRecordBrowserState.Available, items, next, request.ContractVersion, generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Result(MemoryRecordBrowserState.Unavailable, [], null, request.ContractVersion); }
    }

    private bool IsValid(MemoryRecordBrowserRequest? request)
    {
        if (request is null || request.ContractVersion != _version || request.Filter is null || !Enum.IsDefined(request.Filter.Sort) || request.Cursor?.GenerationKey.Length > 128) return false;
        if (request.Filter.Search?.Length > MemoryRecordBrowserLimits.MaximumSearchCharacters || !Valid(request.Filter.Namespace) || !Valid(request.Filter.Tag)) return false;
        try { request.Actor.Validate(nameof(request.Actor)); request.RequestedScope.Validate(); return true; } catch (ArgumentException) { return false; }
    }
    private static bool Valid(string? value) => value is null || (value.Length is > 0 and <= 120 && value == value.Trim());
    private static string Namespace(MemoryRecordType type) => $"type/{type.ToString().ToLowerInvariant()}";
    private static IReadOnlyList<string> Tags(IReadOnlyList<string> entities) => entities.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).Take(16).ToArray();
    private static bool Matches(MemoryRecordBrowserSourceRecord record, MemoryRecordBrowserFilter filter)
    {
        var tags = Tags(record.Entities);
        return (filter.Type is null || record.Type == filter.Type) && (filter.Namespace is null || Namespace(record.Type) == filter.Namespace) &&
               (filter.Tag is null || tags.Contains(filter.Tag, StringComparer.Ordinal)) &&
               (filter.Search is null || string.Concat(record.CanonicalText, " ", string.Join(' ', tags)).Contains(filter.Search, StringComparison.OrdinalIgnoreCase));
    }
    private static int Compare(MemoryRecordBrowserSourceRecord a, MemoryRecordBrowserSourceRecord b, MemoryRecordBrowserSort sort)
    { var compared = a.UpdatedAt.CompareTo(b.UpdatedAt); if (sort == MemoryRecordBrowserSort.UpdatedDescending) compared = -compared; return compared != 0 ? compared : string.CompareOrdinal(a.MemoryId.Value, b.MemoryId.Value); }
    private static (string Title, string Preview) Text(string raw)
    { var line = raw.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Запись памяти"; var preview = raw.Replace('\r',' ').Replace('\n',' ').Trim(); return (Bound(line), Bound(preview)); }
    private static string Bound(string value) => value.Length <= MemoryRecordBrowserLimits.MaximumPreviewCharacters ? value : string.Concat(value.AsSpan(0, MemoryRecordBrowserLimits.MaximumPreviewCharacters - 1), "…");
    private static string Generation(MemoryScope scope, MemoryRecordBrowserFilter filter, IReadOnlyList<MemoryRecordBrowserSourceRecord> records)
    {
        var snapshot = string.Join("\u001e", records.Select(record => string.Join("\u001f", record.MemoryId.Value, record.Version, record.UpdatedAt.UtcTicks)));
        var input = string.Join("\u001d", scope.TenantId.Value, scope.ProjectId?.Value, scope.WorkspaceId?.Value, scope.ChatId?.Value, scope.RunId?.Value,
            filter.Type?.ToString(), filter.Namespace, filter.Tag, filter.Search, filter.Sort.ToString(), snapshot);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }
    private DateTimeOffset UtcNow() => _clock.UtcNow.ToUniversalTime();
    private static MemoryRecordBrowserPage Result(MemoryRecordBrowserState state, IReadOnlyList<MemoryRecordBrowserItem> items, MemoryRecordBrowserCursor? cursor, ContractVersion? version) => new(state, items, cursor, version ?? new("invalid"));
}
