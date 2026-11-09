using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AgMemory.McpServer;

/// <summary>Additive evidence tools retain all legacy memory/knowledge contracts and fixed configured authority.</summary>
[McpServerToolType]
public sealed class ClaimMemoryTools(LocalAgMemoryMcpRuntime memory)
{
    [McpServerTool(Name = "memory_claim", Idempotent = true, OpenWorld = false)]
    [Description("Append a caller-reported claim attached to a current citation. sourceKind=UserStatement|ToolObservation|AgentInference; none proves truth. subject/predicate are symbolic keys. Optional UTC validity is [from,to); missing dates stay unknown. Stable requestKey required. supersedesRef must identify the same tuple. No paths/secrets in value or locator.")]
    public async Task<string> ClaimAsync(string citationRef, string subject, string predicate, string value,
        string sourceKind, string requestKey, DateTimeOffset? observedAtUtc = null, DateTimeOffset? validFromUtc = null,
        DateTimeOffset? validToUtc = null, string? locator = null, string? supersedesRef = null,
        CancellationToken cancellationToken = default) => JsonSerializer.Serialize(await memory.KnowledgeClaimAsync(citationRef,
            subject, predicate, value, sourceKind, requestKey, observedAtUtc, validFromUtc, validToUtc, locator, supersedesRef, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_claims", ReadOnly = true, OpenWorld = false)]
    [Description("Read bounded explicit subject/predicate claims with asOfUtc validity and knownAtUtc transaction-time filters. Conflicting values remain visible; unknown validity stays marked. Historical unavailable sources have no current citation. Claims are caller reports, not fact verification.")]
    public async Task<string> ClaimsAsync(string subject, string predicate, DateTimeOffset? asOfUtc = null,
        DateTimeOffset? knownAtUtc = null, int limit = 10, bool includeHistorical = false, CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.KnowledgeClaimsAsync(subject, predicate, asOfUtc, knownAtUtc, limit, includeHistorical, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_evidence", ReadOnly = true, OpenWorld = false)]
    [Description("Recheck a current citation and read its structured caller claims. Legacy records return LegacyUnknown; no observed/valid dates are fabricated. Citation validity establishes current source revision eligibility, never factual truth or permission.")]
    public async Task<string> EvidenceAsync(string citationRef, CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.KnowledgeEvidenceAsync(citationRef, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_procedure_facets", Idempotent = true, OpenWorld = false)]
    [Description("Attach immutable declared environment-family/capability/precondition facets to one exact procedure revision. Different facets require a new procedure revision. Caller labels and requestKey never verify compatibility, success or permission.")]
    public async Task<string> ProcedureFacetsAsync(string procedureRef, string environmentFamily, string[] requiredCapabilities,
        string[] preconditions, string[] transferWarnings, string requestKey, CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.KnowledgeProcedureFacetsAsync(procedureRef, environmentFamily, requiredCapabilities, preconditions,
                transferWarnings, requestKey, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_procedure_discovery", ReadOnly = true, OpenWorld = false)]
    [Description("Discover lexical RU/EN procedure candidates using exact environment or explicit environmentFamily facets. Missing declared capabilities exclude a card. Cross-environment results always require review; preconditions and transfer are never verified automatically. Legacy memory_procedures stays unchanged.")]
    public async Task<string> ProcedureDiscoveryAsync(string query, string environment, string[] capabilities,
        string? environmentFamily = null, int limit = 5, CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.KnowledgeProcedureDiscoveryAsync(query, environment, capabilities, environmentFamily, limit, cancellationToken).ConfigureAwait(false));
}
