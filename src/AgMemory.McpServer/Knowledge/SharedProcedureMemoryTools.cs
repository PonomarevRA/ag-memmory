using System.ComponentModel;
using System.Text.Json;
using AgMemory.Contracts;
using ModelContextProtocol.Server;

namespace AgMemory.McpServer;

/// <summary>Discovery and explicit publication in the fixed tenant-local procedure library.</summary>
[McpServerToolType]
public sealed class SharedProcedureMemoryTools(LocalAgMemoryMcpRuntime memory)
{
    /// <summary>Reuses exact-scope procedure discovery with environment and capability checks.</summary>
    [McpServerTool(Name = "memory_shared_procedure_discovery", ReadOnly = true, OpenWorld = false)]
    [Description("Find reusable procedures in the local shared library. Check applicability, preconditions and current authorization before use.")]
    public async Task<string> DiscoverAsync(string query, string environment, string[] capabilities,
        string? environmentFamily = null, int limit = 5, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await memory.SharedProcedureDiscoveryAsync(query, environment, capabilities,
            environmentFamily, limit, cancellationToken).ConfigureAwait(false));

    /// <summary>Creates a shared card from explicitly supplied, project-independent procedure text.</summary>
    [McpServerTool(Name = "memory_shared_procedure_publish", Idempotent = true, OpenWorld = false)]
    [Description("Publish a new reusable procedure to all projects in the same local store and tenant. Supply generalized steps and verification, and an exact applicability label up to 500 characters. Omit project details, private links and credentials. Reuse requestKey for retries. Existing cards cannot be edited here.")]
    public async Task<string> PublishAsync(string goal, string[] aliases, string applicability, string preconditions,
        string steps, string verification, string requestKey, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await memory.SharedProcedurePublishAsync(goal, aliases, applicability, preconditions,
            steps, verification, requestKey, cancellationToken).ConfigureAwait(false));
}

public sealed partial class LocalAgMemoryMcpRuntime
{
    internal const string SharedProcedurePartition = "agmemory-shared-procedures";

    /// <summary>Targets the same local store and tenant with a reserved project-level procedure partition.</summary>
    private static LocalAgMemoryMcpConfiguration SharedProcedureConfiguration(LocalAgMemoryMcpConfiguration configuration) =>
        configuration with
        {
            Scope = new MemoryScope(configuration.Scope.TenantId, new ScopeId(SharedProcedurePartition)),
            KnowledgeTeamId = SharedProcedurePartition,
            UseKnowledgeJournal = false,
            ActivityDataDirectory = null,
            UsageAudit = LocalMcpUsageAuditConfiguration.Disabled
        };

    /// <summary>Uses the shared library authority only for this read, preserving the calling actor's reference identity.</summary>
    internal Task<AgentResponse<IReadOnlyList<KnowledgeProcedureDiscoveryView>>> SharedProcedureDiscoveryAsync(
        string query, string environment, string[] capabilities, string? environmentFamily, int limit,
        CancellationToken cancellationToken) =>
        _sharedProcedureLibrary.Value.KnowledgeProcedureDiscoveryAsync(query, environment, capabilities,
            environmentFamily, limit, cancellationToken);

    /// <summary>Creates revision one in the fixed library, with retries isolated by the caller's configured profile.</summary>
    internal Task<AgentResponse<KnowledgeProcedureView>> SharedProcedurePublishAsync(string goal, string[] aliases,
        string applicability, string preconditions, string steps, string verification, string requestKey,
        CancellationToken cancellationToken) => SafeKnowledgeAsync(() =>
    {
        ValidateRequestKey(requestKey);
        ValidateAgentText(applicability, 500);
        var scopedKey = "shared-" + Hash(_agentReferences.Value.ProfileRef + ":" + requestKey);
        return _sharedProcedureLibrary.Value.SaveProcedureAsync(null, 0, goal, aliases, applicability, preconditions,
            steps, verification, null, scopedKey, cancellationToken);
    }, UsageAuditOperation.Remember);
}
