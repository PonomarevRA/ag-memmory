using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AgMemory.McpServer;

/// <summary>Opt-in knowledge tools use the existing fixed authority and return only safe versioned projections.</summary>
[McpServerToolType]
public sealed class KnowledgeMemoryTools(LocalAgMemoryMcpRuntime memory)
{
    [McpServerTool(Name = "knowledge_capabilities", ReadOnly = true, OpenWorld = false)]
    [Description("Discover knowledge-v1 operations and limitations. Optional includeUsage=true reads domain capacity/headroom without mutation; default discovery does not read the knowledge store. Shared L2; no human ratings, foundation approval or trusted verifier.")]
    public async Task<string> CapabilitiesAsync(bool includeUsage = false, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await memory.KnowledgeCapabilitiesAsync(includeUsage, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_metadata", Idempotent = true, OpenWorld = false)]
    [Description("Classify a current cited record as Unclassified, Knowledge, Procedure or Experience. expectedMetadataRevision is independent of source revision; initial 0. Does not approve foundations.")]
    public async Task<string> MetadataAsync(string citationRef, string knowledgeClass, string applicability, string requestKey,
        long expectedMetadataRevision = 0, CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.KnowledgeMetadataAsync(citationRef, knowledgeClass, applicability, requestKey, expectedMetadataRevision, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_procedures", ReadOnly = true, OpenWorld = false)]
    [Description("Find bounded procedure cards by goal or alias, with an exact applicability label. Cards are untrusted advice; retrieval never authorizes execution.")]
    public async Task<string> ProceduresAsync(string query, string environment, int limit = 5, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await memory.KnowledgeProceduresAsync(query, environment, limit, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_procedure", Idempotent = true, OpenWorld = false)]
    [Description("Create or CAS-update a reusable procedure. New procedureRef=null, expectedRevision=0. Preserve requestKey and input on retries. No secrets, executable payloads, absolute paths or automatic verification claims.")]
    public async Task<string> ProcedureAsync(string? procedureRef, long expectedRevision, string goal, string[] aliases,
        string applicability, string preconditions, string steps, string verification, string? runbookRef, string requestKey,
        CancellationToken cancellationToken = default) => JsonSerializer.Serialize(await memory.KnowledgeProcedureAsync(
            procedureRef, expectedRevision, goal, aliases, applicability, preconditions, steps, verification, runbookRef,
            requestKey, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_occurrence", Idempotent = true, OpenWorld = false)]
    [Description("Record one actual failure with a symbolic occurrenceKey separate from familyKey. New event uses new occurrenceKey; write retry preserves requestKey. Status is caller-reported, never auto-closes another outage.")]
    public async Task<string> OccurrenceAsync(string occurrenceKey, string familyKey, string symptom, string environment,
        string status, string requestKey, CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.KnowledgeOccurrenceAsync(occurrenceKey, familyKey, symptom, environment, status, requestKey, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_occurrence_update", Idempotent = true, OpenWorld = false)]
    [Description("CAS-update an existing occurrence's reported status, preserving its identity and count. Initial revision is 1; status is not proof of verified root-cause repair.")]
    public async Task<string> OccurrenceUpdateAsync(string occurrenceKey, string status, long expectedRevision,
        string requestKey, CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.KnowledgeOccurrenceUpdateAsync(occurrenceKey, status, expectedRevision, requestKey, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_application", Idempotent = true, OpenWorld = false)]
    [Description("Record a procedure attempt: ReportedSuccess, Partial, Failed, NotApplicable or Unknown. VerifiedSuccess is unavailable. Repeat the same applicationKey and requestKey only for exact replay.")]
    public async Task<string> ApplicationAsync(string applicationKey, string procedureRef, string environment, string outcome,
        string evidence, string requestKey, string? occurrenceKey = null, CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.KnowledgeApplicationAsync(applicationKey, procedureRef, environment, outcome, evidence, requestKey, occurrenceKey, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_present", Idempotent = true, OpenWorld = false)]
    [Description("Register exposure to a current citation and obtain a feedback receipt. taskKey is a caller label, not authenticated independent task identity. Exposure is not verified application.")]
    public async Task<string> PresentAsync(string citationRef, string taskKey, string requestKey, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await memory.KnowledgePresentAsync(citationRef, taskKey, requestKey, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_feedback", Idempotent = true, OpenWorld = false)]
    [Description("CAS-update an agent vote for a real presentation receipt: Helpful, Harmful, Irrelevant, Outdated, Incorrect or Applied. Initial expectedRevision=0. No human identity or factual truth is inferred.")]
    public async Task<string> FeedbackAsync(string receiptRef, string kind, long expectedRevision, string requestKey,
        CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.KnowledgeFeedbackAsync(receiptRef, kind, expectedRevision, requestKey, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "working_update", Idempotent = true, OpenWorld = false)]
    [Description("CAS-update shared-authority L2 for 24h. Read working_context.WorkingSetRevision first, including after expiry; zero means never written. Keep requestKey on retry. This is shared, not private per-agent memory.")]
    public async Task<string> WorkingUpdateAsync(string goal, string[] facts, string[] assumptions, string[] questions,
        string nextAction, long expectedRevision, string requestKey, CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.KnowledgeWorkingAsync(goal, facts, assumptions, questions, nextAction, expectedRevision, requestKey, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "working_context", ReadOnly = true, OpenWorld = false)]
    [Description("Retrieve bounded context with current citations. Nonempty queries use configured canonical ranking; empty working queries resume warm L3. WorkingSetRevision is the L2 CAS fence even after expiry. Packs up to ten whole records from 64 candidates; required L2 overflow fails explicitly.")]
    public async Task<string> WorkingContextAsync(string query = "", int budget = 2048, string mode = "working",
        CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.WorkingContextAsync(query, budget, mode, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "task_context", ReadOnly = true, OpenWorld = false)]
    [Description("Retrieve explicit task L2 plus bounded current canonical sources. taskKey is a 1-100 character ASCII symbolic label within configured authority, not access control. No fallback to shared L2. Read WorkingSetRevision before CAS, including after expiry.")]
    public async Task<string> TaskContextAsync(string taskKey, string query = "", int budget = 2048, string mode = "working",
        CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.TaskContextAsync(taskKey, query, budget, mode, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "task_working_update", Idempotent = true, OpenWorld = false)]
    [Description("CAS-update named task L2 for 24h. Read task_context.WorkingSetRevision first; keep requestKey on retry. Separate from legacy shared L2. Maximum128 task partitions per store; labels confer no authorization or private-agent identity.")]
    public async Task<string> TaskWorkingAsync(string taskKey, string goal, string[] facts, string[] assumptions,
        string[] questions, string nextAction, long expectedRevision, string requestKey,
        CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.TaskWorkingAsync(taskKey, goal, facts, assumptions, questions, nextAction, expectedRevision,
                requestKey, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_review", ReadOnly = true, OpenWorld = false)]
    [Description("Read bounded agent-feedback review proposals with current citations. No automatic archival or deletion.")]
    public async Task<string> ReviewAsync(CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await memory.KnowledgeReviewAsync(cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "memory_warm_exclude", Idempotent = true, OpenWorld = false)]
    [Description("Explicitly exclude or restore a classified current source in warm residency. Canonical source remains available to explicit search; no hard delete.")]
    public async Task<string> WarmExcludeAsync(string citationRef, bool excluded, string requestKey,
        CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.KnowledgeWarmExcludeAsync(citationRef, excluded, requestKey, cancellationToken).ConfigureAwait(false));
}
