using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Append-only caller claims and exact-revision procedure facets. Neither metadata nor a citation grants factual authority.</summary>
public sealed class KnowledgeClaimService(IKnowledgeAnnotationStore store, string profile)
{
    public Task<KnowledgeAnnotationState> GetAsync(CancellationToken token = default) => store.ReadAsync(profile, token);

    /// <summary>Records one stable request; a repeated key with different content conflicts. Supersession must stay within the same tuple.</summary>
    public async Task<KnowledgeClaim> RecordAsync(KnowledgeClaim claim, CancellationToken token = default)
    {
        ValidateClaim(claim);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var state = await store.ReadAsync(profile, token).ConfigureAwait(false);
            if (state.Claims.TryGetValue(claim.Key, out var existing))
            {
                if (existing.RequestFingerprint != claim.RequestFingerprint) throw new InvalidOperationException("request_conflict");
                return existing;
            }
            if (state.Claims.Count >= 1024) throw new InvalidOperationException("knowledge_capacity_exceeded");
            if (claim.SupersedesKey is not null && (!state.Claims.TryGetValue(claim.SupersedesKey, out var previous) ||
                previous.Subject != claim.Subject || previous.Predicate != claim.Predicate)) throw new ArgumentException("Invalid claim supersession.");
            var claims = new Dictionary<string, KnowledgeClaim>(state.Claims, StringComparer.Ordinal) { [claim.Key] = claim };
            if (await store.CompareExchangeAsync(profile, state.Version, state with { Version = state.Version + 1, Claims = claims }, token).ConfigureAwait(false)) return claim;
        }
        throw new InvalidOperationException("knowledge_conflict");
    }

    /// <summary>Attaches immutable facets to one procedure revision. A changed qualification requires a new procedure revision.</summary>
    public async Task<KnowledgeProcedureFacets> RecordFacetsAsync(KnowledgeProcedureFacets facets, CancellationToken token = default)
    {
        if (facets.ProcedureRevision <= 0 || string.IsNullOrWhiteSpace(facets.EnvironmentFamily) || facets.EnvironmentFamily.Length > 100 ||
            facets.RequiredCapabilities is null || facets.Preconditions is null || facets.TransferWarnings is null ||
            facets.RequiredCapabilities.Length > 24 || facets.Preconditions.Length > 24 || facets.TransferWarnings.Length > 24)
            throw new ArgumentException("Invalid procedure facets.");
        foreach (var lines in new[] { facets.RequiredCapabilities, facets.Preconditions, facets.TransferWarnings })
            foreach (var line in lines)
                if (string.IsNullOrWhiteSpace(line) || line.Length > 1000) throw new ArgumentException("Invalid procedure facet text.");
        var key = facets.ProcedureKey + ":" + facets.ProcedureRevision;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var state = await store.ReadAsync(profile, token).ConfigureAwait(false);
            if (state.ProcedureFacets.TryGetValue(key, out var existing))
            {
                if (existing.Fingerprint != facets.Fingerprint) throw new InvalidOperationException("request_conflict");
                return existing;
            }
            if (state.ProcedureFacets.Count >= 1024) throw new InvalidOperationException("knowledge_capacity_exceeded");
            var next = new Dictionary<string, KnowledgeProcedureFacets>(state.ProcedureFacets, StringComparer.Ordinal) { [key] = facets };
            if (await store.CompareExchangeAsync(profile, state.Version, state with { Version = state.Version + 1, ProcedureFacets = next }, token).ConfigureAwait(false)) return facets;
        }
        throw new InvalidOperationException("knowledge_conflict");
    }

    /// <summary>Filters valid time and known-at transaction time separately; unknown intervals remain visible and explicitly uncertain.</summary>
    public static IReadOnlyList<KnowledgeClaim> Select(KnowledgeAnnotationState state, string subject, string predicate,
        DateTimeOffset asOf, DateTimeOffset knownAt, bool includeSuperseded = false)
    {
        var selected = new List<KnowledgeClaim>();
        foreach (var claim in state.Claims.Values)
        {
            if (claim.Subject != subject || claim.Predicate != predicate || claim.RecordedAtUtc > knownAt ||
                claim.ValidFromUtc > asOf || claim.ValidToUtc <= asOf) continue;
            selected.Add(claim);
        }
        selected.Sort((left, right) =>
        {
            var order = right.RecordedAtUtc.CompareTo(left.RecordedAtUtc);
            return order != 0 ? order : string.CompareOrdinal(left.Key, right.Key);
        });
        return includeSuperseded ? selected : RemoveSuperseded(selected);
    }

    /// <summary>Applies supersession only among eligible claims, so an unavailable replacement cannot hide a current predecessor.</summary>
    public static IReadOnlyList<KnowledgeClaim> RemoveSuperseded(IReadOnlyList<KnowledgeClaim> eligible)
    {
        var superseded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in eligible) if (claim.SupersedesKey is not null) superseded.Add(claim.SupersedesKey);
        var selected = new List<KnowledgeClaim>();
        foreach (var claim in eligible) if (!superseded.Contains(claim.Key)) selected.Add(claim);
        return selected;
    }

    /// <summary>Fingerprints public structured inputs only; server receipt time is deliberately excluded from replay identity.</summary>
    public static string Fingerprint(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Protects direct application-service callers as well as MCP adapters; missing observation/validity dates remain unknown.</summary>
    private static void ValidateClaim(KnowledgeClaim claim)
    {
        foreach (var text in new[] { claim.Key, claim.RequestFingerprint, claim.SourceId, claim.Subject, claim.Predicate, claim.Value })
            if (string.IsNullOrWhiteSpace(text) || text.Length > 1000) throw new ArgumentException("Invalid claim text.");
        if (claim.SourceRevision <= 0 || !Enum.IsDefined(claim.SourceKind) || claim.SourceKind == KnowledgeSourceKind.LegacyUnknown ||
            claim.Locator?.Length > 180 || claim.ValidFromUtc >= claim.ValidToUtc || claim.ObservedAtUtc > claim.RecordedAtUtc)
            throw new ArgumentException("Invalid claim metadata.");
        foreach (var time in new[] { claim.ObservedAtUtc, claim.ValidFromUtc, claim.ValidToUtc, claim.RecordedAtUtc })
            if (time is { Offset: var offset } && offset != TimeSpan.Zero) throw new ArgumentException("Claim times must be UTC.");
    }
}
