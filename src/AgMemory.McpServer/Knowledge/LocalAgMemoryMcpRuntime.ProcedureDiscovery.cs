using AgMemory.Contracts;
using AgMemory.Core;

namespace AgMemory.McpServer;

public sealed record KnowledgeProcedureDiscoveryView(KnowledgeProcedureView Procedure, string ApplicabilityStatus,
    string? EnvironmentFamily, string[] RequiredCapabilities, string[] Preconditions, string[] TransferWarnings,
    string EvidenceTrust = "caller_reported_unverified");

public sealed partial class LocalAgMemoryMcpRuntime
{
    /// <summary>Attaches immutable declared facets to an existing exact procedure revision; it cannot qualify a newer revision implicitly.</summary>
    public Task<AgentResponse<KnowledgeProcedureDiscoveryView>> KnowledgeProcedureFacetsAsync(string procedureRef,
        string environmentFamily, string[] requiredCapabilities, string[] preconditions, string[] transferWarnings,
        string requestKey, CancellationToken cancellationToken = default) => SafeKnowledgeAsync(async () =>
    {
        ValidateRequestKey(requestKey); ValidateKnowledgeKey(environmentFamily);
        ValidateAgentLines(requiredCapabilities); ValidateAgentLines(preconditions); ValidateAgentLines(transferWarnings);
        foreach (var capability in requiredCapabilities) ValidateKnowledgeKey(capability);
        var reference = ReadKnowledgeReference(procedureRef, ProcedurePurpose);
        var key = reference.Id[ProcedurePurpose.Length..];
        var state = await Knowledge.GetAsync(cancellationToken).ConfigureAwait(false);
        KnowledgeProcedure? procedure = null;
        foreach (var candidate in state.ProcedureHistory.Values)
            if (candidate.Key == key && candidate.Revision == reference.Revision) { procedure = candidate; break; }
        if (procedure is null) throw new AgentRequestException("reference_unavailable");
        var fingerprint = KnowledgeClaimService.Fingerprint(new { key, reference.Revision, environmentFamily, requiredCapabilities, preconditions, transferWarnings });
        var facets = await Claims.RecordFacetsAsync(new(key, reference.Revision, environmentFamily, requiredCapabilities,
            preconditions, transferWarnings, fingerprint), cancellationToken).ConfigureAwait(false);
        return ToProcedureDiscovery(procedure, facets, "ExactRevisionNeedsPreconditionCheck");
    });

    /// <summary>Uses lexical RU/EN discovery with explicit environment/facet filters; every cross-environment candidate remains unverified advice.</summary>
    public Task<AgentResponse<IReadOnlyList<KnowledgeProcedureDiscoveryView>>> KnowledgeProcedureDiscoveryAsync(string query,
        string environment, string[] capabilities, string? environmentFamily = null, int limit = 5,
        CancellationToken cancellationToken = default) => SafeKnowledgeAsync(async () =>
    {
        ValidateAgentText(query, 1000); ValidateAgentText(environment, 500); ValidateAgentLines(capabilities);
        if (environmentFamily is not null) ValidateKnowledgeKey(environmentFamily);
        foreach (var capability in capabilities) ValidateKnowledgeKey(capability);
        if (limit is < 1 or > 10) throw new ArgumentException("Invalid discovery limit.");
        var knowledge = await Knowledge.GetAsync(cancellationToken).ConfigureAwait(false);
        var annotations = await Claims.GetAsync(cancellationToken).ConfigureAwait(false);
        var selected = KnowledgeProcedureDiscovery.Find(knowledge, annotations, query, environment, environmentFamily, capabilities, limit);
        var results = new List<KnowledgeProcedureDiscoveryView>(selected.Count);
        foreach (var candidate in selected) results.Add(ToProcedureDiscovery(candidate.Procedure, candidate.Facets, candidate.ApplicabilityStatus));
        return (IReadOnlyList<KnowledgeProcedureDiscoveryView>)results;
    });

    private KnowledgeProcedureDiscoveryView ToProcedureDiscovery(KnowledgeProcedure procedure, KnowledgeProcedureFacets? facets,
        string status) => new(ToKnowledgeProcedure(procedure), status, facets?.EnvironmentFamily,
            facets?.RequiredCapabilities ?? [], facets?.Preconditions ?? [], facets?.TransferWarnings ?? []);
}
