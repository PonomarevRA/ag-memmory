using System.Text;
using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Builds browser-safe pages from immutable catalog leaves and rechecks every leaf before disclosure.</summary>
public sealed class MemoryReaderCatalogQueryService : IMemoryReaderCatalogQueryService
{
    private readonly IMemoryReaderCatalogSource _catalog;
    private readonly IMemoryReaderSource _reader;
    private readonly IAuthorizationScopeValidator _authorization;
    private readonly IClock _clock;
    private readonly ContractVersion _supportedContractVersion;

    public MemoryReaderCatalogQueryService(
        IMemoryReaderCatalogSource catalog,
        IMemoryReaderSource reader,
        IAuthorizationScopeValidator authorization,
        IClock clock,
        ContractVersion supportedContractVersion)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        supportedContractVersion.Validate(nameof(supportedContractVersion));
        _supportedContractVersion = supportedContractVersion;
    }

    public async Task<MemoryReaderCatalogPage> BrowseAsync(MemoryReaderCatalogRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request)) return Result(MemoryReaderCatalogState.Unavailable, [], null, request?.ContractVersion);
        var eligibility = await AuthorizeExactAsync(request.Actor, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (eligibility is null) return Result(MemoryReaderCatalogState.NotFound, [], null, request.ContractVersion);

        try
        {
            var documents = new List<MemoryReaderCatalogDocument>(MemoryReaderLimits.DocumentsPerPage);
            var cursor = request.Cursor;
            MemoryReaderCatalogCursor? next = null;
            for (var scan = 0; scan < MemoryReaderLimits.MaximumCatalogLeafScansPerPage && documents.Count < MemoryReaderLimits.DocumentsPerPage; scan++)
            {
                var leaves = await _catalog.ReadReadyLeafPageAsync(eligibility, cursor, cancellationToken).ConfigureAwait(false);
                if (leaves is null)
                    return request.Cursor is null
                        ? Result(MemoryReaderCatalogState.CatalogNotReady, [], null, request.ContractVersion)
                        : Result(MemoryReaderCatalogState.Changed, [], null, request.ContractVersion);

                foreach (var leaf in leaves.Entries)
                {
                    var record = await _reader.ReadByIdAsync(eligibility, leaf.MemoryId, cancellationToken).ConfigureAwait(false);
                    if (record is null || record.Version != leaf.Version || !IsEligible(record, eligibility)) continue;
                    var route = await _reader.GetOrCreateRouteAsync(record.MemoryId, cancellationToken).ConfigureAwait(false);
                    documents.Add(new($"/memory-reader/{Uri.EscapeDataString(route.RouteKey)}", record.Type, Preview(record.CanonicalText), record.UpdatedAt));
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
            return Result(MemoryReaderCatalogState.Available, documents, next, request.ContractVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(MemoryReaderCatalogState.Unavailable, [], null, request.ContractVersion);
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
        if (request is null || request.ContractVersion != _supportedContractVersion ||
            request.Cursor is { NextLeafPosition: < 0 } || request.Cursor is { GenerationKey.Length: > 128 }) return false;
        try
        {
            request.Actor.Validate(nameof(request.Actor));
            request.RequestedScope.Validate();
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private DateTimeOffset UtcNow() => _clock.UtcNow.Offset == TimeSpan.Zero ? _clock.UtcNow : _clock.UtcNow.ToUniversalTime();

    private static bool IsEligible(MemoryReaderSourceRecord record, MemorySearchEligibility eligibility) =>
        eligibility.AuthorizedScopes.Contains(record.Scope) &&
        record.Status == MemoryLifecycleStatus.Active &&
        (record.ExpiresAt is null || record.ExpiresAt > eligibility.AsOfUtc);

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
        MemoryReaderCatalogCursor? next,
        ContractVersion? version) => new(state, documents, next, version ?? _supportedContractVersion);
}
