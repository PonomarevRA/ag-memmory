using System.Text;
using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>
/// Builds a bounded graph snapshot from active memory records. Relationships are derived per query from
/// normalized shared entities and are deliberately never persisted as <see cref="MemoryRelation"/> values.
/// </summary>
public sealed class MemoryGraphQueryService : IMemoryGraphQueryService
{
    private readonly IMemoryGraphSource _source;
    private readonly IAuthorizationScopeValidator _authorization;
    private readonly IClock _clock;
    private readonly ContractVersion _supportedContractVersion;

    public MemoryGraphQueryService(
        IMemoryGraphSource source,
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

    public async Task<MemoryGraphSnapshot> ReadAsync(MemoryGraphRequest request, CancellationToken cancellationToken)
    {
        if (!IsValid(request, out var validationError))
            return Failure(request, validationError!);

        ScopeAuthorizationResult authorization;
        try
        {
            authorization = await _authorization.AuthorizeAsync(
                request.Actor, MemoryOperation.GraphRead, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failure(request, DependencyFailure());
        }

        if (!authorization.IsAllowed)
            return Failure(request, authorization.Error ?? new(MemoryErrorCode.Unauthorized, null, authorization.PolicyVersion));

        // A graph is intentionally one exact configured scope even if another caller is authorised for more.
        if (!authorization.AuthorizedScopes!.Contains(request.RequestedScope))
            return Failure(request, new(MemoryErrorCode.Unauthorized, null, authorization.PolicyVersion));

        try
        {
            var sourceLimit = Math.Min(request.SourceLimit, MemoryGraphLimits.MaximumSourceRecords);
            var visibleLimit = Math.Min(Math.Min(request.VisibleNodeLimit, MemoryGraphLimits.MaximumVisibleNodes), sourceLimit);
            var edgeLimit = Math.Min(request.EdgeLimit, MemoryGraphLimits.MaximumEdges);
            var exactScope = new AuthorizedScopeSet([new ScopeSelector(request.RequestedScope)]);
            var eligibility = new MemorySearchEligibility(exactScope, null, UtcNow());
            var sourceRequest = new MemoryGraphSourceRequest(eligibility, sourceLimit);
            var source = await _source.ReadAsync(sourceRequest, cancellationToken).ConfigureAwait(false);
            var visible = source
                .Where(record => record is not null && IsUsable(record) && sourceRequest.IsEligible(record))
                .GroupBy(record => record.MemoryId)
                .Select(group => group.First())
                .OrderByDescending(record => record.Importance)
                .ThenByDescending(record => record.Confidence)
                .ThenBy(record => record.MemoryId.Value, StringComparer.Ordinal)
                .Take(visibleLimit)
                .ToArray();

            var edges = DeriveEdges(visible, edgeLimit);
            var degrees = Degrees(edges);
            var nodes = visible
                .Select(record => new MemoryGraphNode(
                    record.MemoryId,
                    record.Type,
                    Band(record.Importance),
                    Band(record.Confidence),
                    degrees.GetValueOrDefault(record.MemoryId)))
                .ToArray();
            return new(nodes, edges, null, _supportedContractVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failure(request, DependencyFailure());
        }
    }

    private bool IsValid(MemoryGraphRequest? request, out MemoryError? error)
    {
        error = null;
        if (request is null || request.SourceLimit <= 0 || request.VisibleNodeLimit <= 0 || request.EdgeLimit <= 0)
        {
            error = new(MemoryErrorCode.InvalidArgument, nameof(request));
            return false;
        }
        if (request.ContractVersion != _supportedContractVersion)
        {
            error = new(MemoryErrorCode.UnsupportedContractVersion, nameof(MemoryGraphRequest.ContractVersion));
            return false;
        }
        try
        {
            request.Actor.Validate(nameof(request.Actor));
            request.RequestedScope.Validate();
            request.ContractVersion.Validate(nameof(request.ContractVersion));
            return true;
        }
        catch (ArgumentException)
        {
            error = new(MemoryErrorCode.InvalidArgument, nameof(request));
            return false;
        }
    }

    private static bool IsUsable(MemoryGraphSourceRecord record) =>
        Enum.IsDefined(record.Type) &&
        double.IsFinite(record.Importance) && record.Importance is >= 0d and <= 1d &&
        double.IsFinite(record.Confidence) && record.Confidence is >= 0d and <= 1d &&
        record.Entities is not null;

    private static int Band(double value) => Math.Clamp((int)Math.Ceiling(value * 5d), 1, 5);

    private static IReadOnlyList<MemoryGraphEdge> DeriveEdges(
        IReadOnlyList<MemoryGraphSourceRecord> records,
        int edgeLimit)
    {
        var recordsByEntity = new Dictionary<string, List<MemoryId>>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            foreach (var entity in NormalizeEntities(record.Entities))
            {
                if (!recordsByEntity.TryGetValue(entity, out var members))
                {
                    members = [];
                    recordsByEntity.Add(entity, members);
                }

                members.Add(record.MemoryId);
            }
        }

        var weights = new Dictionary<MemoryIdPair, int>();
        foreach (var members in recordsByEntity.Values)
        {
            for (var first = 0; first < members.Count; first++)
            {
                for (var second = first + 1; second < members.Count; second++)
                {
                    var pair = MemoryIdPair.Create(members[first], members[second]);
                    weights[pair] = weights.GetValueOrDefault(pair) + 1;
                }
            }
        }

        return weights
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key.First.Value, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.Second.Value, StringComparer.Ordinal)
            .Take(edgeLimit)
            .Select(entry => new MemoryGraphEdge(
                entry.Key.First,
                entry.Key.Second,
                entry.Value,
                MemoryGraphEdgeKind.SharedEntity))
            .ToArray();
    }

    private static IReadOnlyDictionary<MemoryId, int> Degrees(IReadOnlyList<MemoryGraphEdge> edges)
    {
        var degrees = new Dictionary<MemoryId, int>();
        foreach (var edge in edges)
        {
            degrees[edge.FirstMemoryId] = degrees.GetValueOrDefault(edge.FirstMemoryId) + 1;
            degrees[edge.SecondMemoryId] = degrees.GetValueOrDefault(edge.SecondMemoryId) + 1;
        }

        return degrees;
    }

    private static IEnumerable<string> NormalizeEntities(IReadOnlyList<string> entities) => entities
        .Where(entity => !string.IsNullOrWhiteSpace(entity))
        .Select(NormalizeEntity)
        .Where(entity => entity.Length > 0)
        .Distinct(StringComparer.Ordinal);

    private static string NormalizeEntity(string entity)
    {
        var normalized = entity.Normalize(NormalizationForm.FormKC).Trim();
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString().ToUpperInvariant();
    }

    private DateTimeOffset UtcNow()
    {
        var now = _clock.UtcNow;
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }

    private MemoryGraphSnapshot Failure(MemoryGraphRequest? request, MemoryError error) =>
        new([], [], error, request?.ContractVersion ?? _supportedContractVersion);

    private static MemoryError DependencyFailure() => new(MemoryErrorCode.DependencyFailure, null);

    private readonly record struct MemoryIdPair(MemoryId First, MemoryId Second)
    {
        public static MemoryIdPair Create(MemoryId first, MemoryId second) =>
            string.CompareOrdinal(first.Value, second.Value) <= 0 ? new(first, second) : new(second, first);
    }
}
