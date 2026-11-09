using AgMemory.Contracts;

namespace AgMemory.McpServer;

/// <summary>Allowlisted v2 response; arbitrary exceptions and storage diagnostics never cross this boundary.</summary>
public sealed record AgentResponse<T>(string SchemaVersion, T? Data, AgentError? Error)
{
    public static AgentResponse<T> Success(T data) => new("agmemory-agent-v2", data, null);
    public static AgentResponse<T> Failure(string code, bool retryable = false, string action = "correct_request") =>
        new("agmemory-agent-v2", default, new(code, retryable, action));
}
public sealed record AgentError(string Code, bool Retryable, string Action);
public sealed record AgentCapabilities(string ProfileRef, string RetrievalMode, int MaxCandidates, int MaxBudget,
    int ReferenceLifetimeSeconds, string[] Operations, string Authority, string ContentTrust)
{
    public AgMemory.AgentSetup.SkillDiscovery Skill { get; } = AgMemory.AgentSetup.AgentSkillPackage.Discovery;
    /// <summary>Null disables packaged lexical expansion; a version identifies a finite reviewed dictionary, not semantic inference.</summary>
    public string? QueryAliasesVersion { get; init; }
    public int MaximumQueryChannels { get; init; } = 1;
    public string RecallIndex { get; init; } = "disabled";
}
public sealed record AgentMemory(string Type, string Content, string CitationRef, long Revision, DateTimeOffset UpdatedAt);
public sealed record AgentContext(IReadOnlyList<AgentMemory> Items, int OmittedCandidateCount, int EstimatedTokenCost, string EstimationMethod);
public sealed record AgentWriteReceipt(string Outcome, AgentMemory? Memory);

/// <summary>Caller-supplied evidence is a compact label, never authority or a filesystem locator.</summary>
public sealed record AgentEvidence(string Label, string Claim);
/// <summary>Reusable handoff separates evidence-backed statements from unverified assumptions. It is data, not executable instructions.</summary>
public sealed record AgentHandoff(string[] Facts, string[] Assumptions, string[] OpenQuestions, string NextAction, AgentEvidence[]? Evidence = null);
/// <summary>Public decision trace, excluding private reasoning and transcripts.</summary>
public sealed record AgentDecision(string Problem, string Context, string[] Options, string Decision, string Reason, string[] Consequences, string? Outcome = null);

internal sealed class AgentRequestException(string code, bool retryable = false) : Exception
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
