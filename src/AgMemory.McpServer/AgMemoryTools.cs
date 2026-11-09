using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AgMemory.McpServer;

/// <summary>Tools intentionally carry no actor, scope, storage path, provenance or durable IDs.</summary>
[McpServerToolType]
public sealed class AgMemoryTools(LocalAgMemoryMcpRuntime memory)
{
    [McpServerTool(Name = "memory_recall", Title = "Recall AgMemory", ReadOnly = true, OpenWorld = false)]
    [Description("Search the fixed local AgMemory scope for active memories relevant to a query. Use before answering questions that may depend on saved project context.")]
    public async Task<string> RecallAsync(
        [Description("Terms or a question to search for in saved memory.")] string query,
        [Description("Maximum matching memories to return, from 1 through 10.")] int limit = 5,
        CancellationToken cancellationToken = default)
    {
        var hits = await memory.RecallAsync(query, limit, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(hits);
    }

    [McpServerTool(Name = "memory_remember", Title = "Remember in AgMemory", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Persist a concise reusable fact, constraint, preference, task, procedure, observation, incident, lesson, outcome, summary, or event in the fixed local AgMemory scope. Do not store secrets, raw conversation logs, private reasoning, or Decision records; Decisions require a structured trace unavailable through this tool.")]
    public async Task<string> RememberAsync(
        [Description("Concise reusable memory, up to 6000 characters.")] string content,
        [Description("AgMemory record type, for example Fact, Constraint, Preference, Task, or Outcome. Decision is unavailable through this tool.")] string memoryType = "Fact",
        [Description("Importance from 0 through 1.")] double importance = 0.6d,
        [Description("Confidence from 0 through 1.")] double confidence = 0.8d,
        [Description("Optional compact entity labels used for local graph grouping.")] string[]? entities = null,
        CancellationToken cancellationToken = default)
    {
        var receipt = await memory.RememberAsync(content, memoryType, importance, confidence, entities, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(receipt);
    }

    [McpServerTool(Name = "memory_status", Title = "AgMemory status", ReadOnly = true, OpenWorld = false)]
    [Description("Check whether the configured local AgMemory store is available and count active memories in its fixed scope.")]
    public async Task<string> StatusAsync(CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await memory.GetStatusAsync(cancellationToken).ConfigureAwait(false));
}
