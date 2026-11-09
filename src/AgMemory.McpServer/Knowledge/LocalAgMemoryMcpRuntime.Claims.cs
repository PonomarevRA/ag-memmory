using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;

namespace AgMemory.McpServer;

public sealed partial class LocalAgMemoryMcpRuntime
{
    private KnowledgeClaimService Claims => new(new KnowledgeAnnotationStore(_configuration.StoragePath), KnowledgeProfileRef);
    private string ClaimPurpose => KnowledgePurpose("knowledge-claim:");

    /// <summary>Adds an attributed caller claim without rewriting canonical text or promoting inference to observation.</summary>
    public Task<AgentResponse<KnowledgeClaimView>> KnowledgeClaimAsync(string citationRef, string subject, string predicate,
        string value, string sourceKind, string requestKey, DateTimeOffset? observedAtUtc = null,
        DateTimeOffset? validFromUtc = null, DateTimeOffset? validToUtc = null, string? locator = null,
        string? supersedesRef = null, CancellationToken cancellationToken = default) => SafeKnowledgeAsync(async () =>
    {
        ValidateRequestKey(requestKey); ValidateKnowledgeKey(subject); ValidateKnowledgeKey(predicate); ValidateAgentText(value, 1000);
        var kind = ParseKnowledgeEnum<KnowledgeSourceKind>(sourceKind);
        if (kind == KnowledgeSourceKind.LegacyUnknown) throw new ArgumentException("LegacyUnknown is a read projection only.");
        if (locator is not null) ValidateAgentText(locator, 180);
        ValidateClaimTime(observedAtUtc); ValidateClaimTime(validFromUtc); ValidateClaimTime(validToUtc);
        var now = DateTimeOffset.UtcNow;
        if (observedAtUtc > now || validFromUtc >= validToUtc) throw new ArgumentException("Invalid claim interval.");
        var source = await CurrentKnowledgeSourceAsync(citationRef, cancellationToken).ConfigureAwait(false);
        var supersedes = supersedesRef is null ? null : ReadKnowledgeReference(supersedesRef, ClaimPurpose).Id[ClaimPurpose.Length..];
        var fingerprint = KnowledgeClaimService.Fingerprint(new { SourceId = source.Id.Value, SourceRevision = source.Version,
            subject, predicate, value, kind, observedAtUtc, validFromUtc, validToUtc, locator, supersedes });
        var claim = new KnowledgeClaim("claim-" + Hash(requestKey)[..24], fingerprint, source.Id.Value, source.Version,
            subject, predicate, value, kind, locator, observedAtUtc, validFromUtc, validToUtc, now, supersedes);
        var saved = await Claims.RecordAsync(claim, cancellationToken).ConfigureAwait(false);
        return await ToClaimViewAsync(saved, null, cancellationToken).ConfigureAwait(false);
    });

    /// <summary>Queries explicit tuples with separate valid-time/known-at filters; contradictions remain visible rather than choosing a winner.</summary>
    public Task<AgentResponse<KnowledgeClaimsView>> KnowledgeClaimsAsync(string subject, string predicate,
        DateTimeOffset? asOfUtc = null, DateTimeOffset? knownAtUtc = null, int limit = 10,
        bool includeHistorical = false, CancellationToken cancellationToken = default) => SafeKnowledgeAsync(async () =>
    {
        ValidateKnowledgeKey(subject); ValidateKnowledgeKey(predicate); ValidateClaimTime(asOfUtc); ValidateClaimTime(knownAtUtc);
        if (limit is < 1 or > 16 || knownAtUtc > DateTimeOffset.UtcNow) throw new ArgumentException("Invalid claim query.");
        var asOf = asOfUtc ?? DateTimeOffset.UtcNow;
        var knownAt = knownAtUtc ?? DateTimeOffset.UtcNow;
        var state = await Claims.GetAsync(cancellationToken).ConfigureAwait(false);
        var eligible = new List<KnowledgeClaim>();
        var sources = new Dictionary<string, MemoryRecord?>(StringComparer.Ordinal);
        foreach (var claim in KnowledgeClaimService.Select(state, subject, predicate, asOf, knownAt, includeSuperseded: true))
        {
            if (!sources.TryGetValue(claim.SourceId, out var source))
            {
                source = await _store.GetAsync(AgentScopes, new(claim.SourceId), cancellationToken).ConfigureAwait(false);
                sources.Add(claim.SourceId, source);
            }
            // A scope move/deletion revokes even historical projection. History is explicit and never carries a current citation.
            if (source is null || !includeHistorical && (!IsActive(source) || source.Version != claim.SourceRevision)) continue;
            eligible.Add(claim);
        }
        var selected = KnowledgeClaimService.RemoveSuperseded(eligible);
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in selected) values.Add(claim.Value);
        var conflict = values.Count > 1;
        var items = new List<KnowledgeClaimView>(Math.Min(limit, selected.Count));
        foreach (var claim in selected)
        {
            items.Add(await ToClaimViewAsync(claim, conflict, cancellationToken).ConfigureAwait(false));
            if (items.Count == limit) break;
        }
        return new KnowledgeClaimsView(asOf, knownAt, items, selected.Count - items.Count, conflict);
    });

    /// <summary>Resolves the canonical citation first and reports unknown provenance for records with no structured claims; no dates are inferred from prose.</summary>
    public Task<AgentResponse<KnowledgeEvidenceView>> KnowledgeEvidenceAsync(string citationRef,
        CancellationToken cancellationToken = default) => SafeKnowledgeAsync(async () =>
    {
        var source = await CurrentKnowledgeSourceAsync(citationRef, cancellationToken).ConfigureAwait(false);
        var state = await Claims.GetAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<KnowledgeClaimView>();
        var matching = 0;
        foreach (var claim in state.Claims.Values)
        {
            if (claim.SourceId != source.Id.Value || claim.SourceRevision != source.Version) continue;
            matching++;
            // Raw evidence spans dates/tuples. A false flag would incorrectly claim that temporal conflict analysis ran.
            if (items.Count < 16) items.Add(await ToClaimViewAsync(claim, null, cancellationToken).ConfigureAwait(false));
        }
        return new KnowledgeEvidenceView(ToAgentMemory(source), matching == 0 ? "LegacyUnknown" : "StructuredCallerClaims",
            "current_revision_eligibility_not_factual_truth", items, matching - items.Count);
    });

    /// <summary>Rechecks source lifecycle and revision at output time; unavailable historical sources receive no fresh canonical citation.</summary>
    private async Task<KnowledgeClaimView> ToClaimViewAsync(KnowledgeClaim claim, bool? conflict, CancellationToken token)
    {
        var source = await _store.GetAsync(AgentScopes, new(claim.SourceId), token).ConfigureAwait(false);
        var current = source is not null && IsActive(source) && source.Version == claim.SourceRevision;
        var reference = _agentReferences.Value.Issue(ClaimPurpose + claim.Key, 1, null, DateTimeOffset.UtcNow);
        return new(reference, claim.Subject, claim.Predicate, claim.Value, claim.SourceKind.ToString(), claim.Locator,
            claim.ObservedAtUtc, claim.ValidFromUtc, claim.ValidToUtc, claim.RecordedAtUtc,
            current ? ToAgentMemory(source!).CitationRef : null, current,
            claim.ValidFromUtc is null && claim.ValidToUtc is null, conflict);
    }

    private static void ValidateClaimTime(DateTimeOffset? value)
    {
        if (value is { Offset: var offset } && offset != TimeSpan.Zero) throw new ArgumentException("Claim times must be UTC.");
    }
}
