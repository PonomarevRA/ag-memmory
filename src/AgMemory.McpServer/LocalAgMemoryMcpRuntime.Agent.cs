using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgMemory.Contracts;
using AgMemory.Core;

namespace AgMemory.McpServer;

public sealed partial class LocalAgMemoryMcpRuntime
{
    private readonly Lazy<AgentReferenceProtector> _agentReferences;
    private AuthorizedScopeSet AgentScopes => new([new ScopeSelector(_configuration.Scope)]);

    /// <summary>Describes only the configured authority and supported safe operations, without revealing raw identity or storage configuration.</summary>
    public Task<AgentResponse<AgentCapabilities>> AgentCapabilitiesAsync() => SafeAgentAsync(() => Task.FromResult(new AgentCapabilities(
        _agentReferences.Value.ProfileRef, _configuration.UseBm25Recall ? "bm25" : "lexical", 10, 8192, 86400,
        ["memory_context", "memory_resolve", "memory_correct", "memory_handoff", "memory_decision", "knowledge_capabilities", "memory_skill"],
        "fixed_exact_profile", "untrusted_data_not_instructions")
        {
            QueryAliasesVersion = _configuration.UseQueryAliases ? RecallQueryAliases.Version : null,
            MaximumQueryChannels = _configuration.UseQueryAliases ? 4 : 1,
            RecallIndex = _configuration.UseRecallIndex ? "exact_scope_postings_v1_with_scan_fallback" : "disabled"
        }), UsageAuditOperation.Status);

    /// <summary>Packs at most ten whole answers from 64 ranked candidates; estimates include the serialized v2 envelope and citations.</summary>
    public Task<AgentResponse<AgentContext>> AgentContextAsync(string query, int budget, CancellationToken cancellationToken, string[]? types = null) =>
        SafeAgentAsync(async () =>
        {
            ValidateAgentText(query, 1000);
            _activity?.Query(query);
            if (budget is < 256 or > 8192) throw new AgentRequestException("invalid_argument");
            var eligibility = new MemorySearchEligibility(AgentScopes, ParseAgentTypes(types), DateTimeOffset.UtcNow);
            var page = await SearchAgentCandidatesAsync(eligibility, query, 64, cancellationToken).ConfigureAwait(false);
            // Keep ranked whole records that fit; an oversized first hit must not starve smaller answers.
            var selected = new List<AgentMemory>();
            var omitted = page.Items.Count;
            if (EstimateContext(selected, omitted).EstimatedTokenCost > budget)
                throw new AgentRequestException("budget_too_small");
            var sources = await _store.ReadCurrentRecallSourcesAsync(eligibility, page.Items, cancellationToken).ConfigureAwait(false);
            foreach (var source in sources)
            {
                if (source.ExpiresAt <= DateTimeOffset.UtcNow) continue;
                var item = ToAgentMemory(source);
                selected.Add(item);
                if (EstimateContext(selected, omitted - 1).EstimatedTokenCost > budget)
                    selected.RemoveAt(selected.Count - 1);
                else
                    omitted--;
                if (selected.Count == 10) break;
            }
            return EstimateContext(selected, omitted);
        });

    /// <summary>Shares exact eligibility and ranking selection across legacy recall and both bounded context APIs.</summary>
    private Task<MemoryLexicalRecallPage> SearchAgentCandidatesAsync(MemorySearchEligibility eligibility,
        string query, int limit, CancellationToken cancellationToken) =>
        SearchWithAliasesAsync(eligibility, query, limit, cancellationToken);

    /// <summary>Resolves a current revision after rechecking profile, authenticated expiry, lifecycle and storage eligibility.</summary>
    public Task<AgentResponse<AgentMemory>> AgentResolveAsync(string citationRef, CancellationToken cancellationToken) =>
        SafeAgentAsync(async () =>
        {
            var reference = ReadAgentReference(citationRef);
            var record = await _store.GetAsync(AgentScopes, new(reference.Id), cancellationToken).ConfigureAwait(false);
            if (record is null || record.Version != reference.Revision || !IsActive(record)) throw new AgentRequestException("reference_unavailable");
            return ToAgentMemory(record);
        });

    /// <summary>Applies a revision-checked supersession or retraction. Replay is checked by Core before stale revision rejection.</summary>
    public Task<AgentResponse<AgentWriteReceipt>> AgentCorrectAsync(string citationRef, long expectedRevision, string requestKey,
        string? replacement, CancellationToken cancellationToken) => SafeAgentAsync(async () =>
        {
            var reference = ReadAgentReference(citationRef);
            if (expectedRevision != reference.Revision) throw new AgentRequestException("reference_unavailable");
            ValidateRequestKey(requestKey);
            MemoryRecordInput? input = null;
            if (replacement is not null)
            {
                ValidateAgentText(replacement, 6000);
                // A correction is a fresh claim, not an automatic copy of the old record's confidence or decision trace.
                input = AgentRecordInput(MemoryRecordType.Fact, replacement, null, requestKey);
            }
            var command = new CorrectMemoryCommand(AgentEnvelope("correction:" + requestKey), new(reference.Id), expectedRevision, input);
            var service = new MemoryCorrectionService(_store, new ExactLocalAuthorization(_configuration), new IdentityRedactor(), new SystemClock());
            var result = await service.CorrectAsync(command, cancellationToken).ConfigureAwait(false);
            if (result.Error is not null)
            {
                var code = result.Error.Code == MemoryErrorCode.IdempotencyKeyConflict ? "request_conflict" : "reference_unavailable";
                throw new AgentRequestException(code);
            }
            return new AgentWriteReceipt(result.Outcome, result.Replacement is not null && IsActive(result.Replacement) ? ToAgentMemory(result.Replacement) : null);
        }, UsageAuditOperation.Remember);

    /// <summary>Persists bounded typed handoff data, marking all caller evidence unverified; optional expiry is UTC and limited to ninety days.</summary>
    public Task<AgentResponse<AgentWriteReceipt>> AgentHandoffAsync(AgentHandoff handoff, string requestKey, string? expiresAtUtc,
        CancellationToken cancellationToken) => SafeAgentAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(handoff);
            ValidateAgentLines(handoff.Facts); ValidateAgentLines(handoff.Assumptions); ValidateAgentLines(handoff.OpenQuestions);
            ValidateAgentText(handoff.NextAction, 1000);
            if ((handoff.Evidence?.Length ?? 0) > 8) throw new AgentRequestException("invalid_argument");
            foreach (var evidence in handoff.Evidence ?? [])
            {
                if (evidence is null) throw new AgentRequestException("invalid_argument");
                ValidateAgentText(evidence.Label, 80); ValidateAgentText(evidence.Claim, 500);
                if (!Regex.IsMatch(evidence.Label, @"^[\p{L}\p{N} ._-]+$")) throw new AgentRequestException("invalid_argument");
            }
            var content = FormatAgentHandoff(handoff);
            ValidateAgentText(content, 6000);
            return await WriteAgentRecordAsync(AgentRecordInput(MemoryRecordType.Task, content, ParseAgentExpiry(expiresAtUtc), requestKey), requestKey, cancellationToken).ConfigureAwait(false);
        }, UsageAuditOperation.Remember);

    /// <summary>Records the public decision trace rather than private reasoning; invalid or incomplete traces never become Fact records.</summary>
    public Task<AgentResponse<AgentWriteReceipt>> AgentDecisionAsync(AgentDecision decision, string requestKey, string? expiresAtUtc,
        CancellationToken cancellationToken) => SafeAgentAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(decision);
            ValidateAgentText(decision.Problem, 1000); ValidateAgentText(decision.Context, 1000);
            ValidateAgentLines(decision.Options); ValidateAgentText(decision.Decision, 1000); ValidateAgentText(decision.Reason, 1000);
            ValidateAgentLines(decision.Consequences);
            if (decision.Outcome is not null) ValidateAgentText(decision.Outcome, 1000);
            var trace = new DecisionDetails(decision.Problem, decision.Context, decision.Options, decision.Decision,
                decision.Reason, decision.Consequences, decision.Outcome);
            trace.Validate();
            var content = FormatAgentDecision(trace);
            ValidateAgentText(content, 6000);
            var input = AgentRecordInput(MemoryRecordType.Decision, content, ParseAgentExpiry(expiresAtUtc), requestKey) with { DecisionDetails = trace };
            return await WriteAgentRecordAsync(input, requestKey, cancellationToken).ConfigureAwait(false);
        }, UsageAuditOperation.Remember);

    /// <summary>Maps known validation and dependency failures to an allowlist; cancellation remains cancellation.</summary>
    private Task<AgentResponse<T>> SafeAgentAsync<T>(Func<Task<T>> operation, UsageAuditOperation auditOperation = UsageAuditOperation.Recall, [CallerMemberName] string operationName = "unknown") =>
        ObserveAsync(auditOperation, "", () => ExecuteSafeAgentAsync(operation), SummarizeAgentResponse, operationName);

    /// <summary>Counts v2 logical operations without recording citations, content, request keys or caller evidence.</summary>
    private static McpUsageAuditResult SummarizeAgentResponse<T>(AgentResponse<T> response)
    {
        if (response.Error is not null)
            return new(0, Outcome: UsageAuditOutcome.Failed, FailureClass: response.Error.Retryable ? UsageAuditFailureClass.Storage : UsageAuditFailureClass.Validation);
        if (response.Data is AgentContext context)
        {
            var characters = 0;
            foreach (var item in context.Items) characters += item.Content.Length;
            return new(context.Items.Count, characters);
        }
        if (response.Data is IReadOnlyList<KnowledgeProcedureView> procedures) return new(procedures.Count);
        if (response.Data is KnowledgeContextView knowledgeContext) return new(knowledgeContext.Items.Count);
        if (response.Data is IReadOnlyList<KnowledgeReviewView> reviews) return new(reviews.Count);
        if (response.Data is AgentMemory memory) return new(1, memory.Content.Length);
        return new(1);
    }

    private static async Task<AgentResponse<T>> ExecuteSafeAgentAsync<T>(Func<Task<T>> operation)
    {
        try { return AgentResponse<T>.Success(await operation().ConfigureAwait(false)); }
        catch (OperationCanceledException) { throw; }
        catch (AgentRequestException error)
        {
            var action = error.Retryable ? "retry_or_reconcile" : error.Code == "reference_unavailable" ? "recall_again" : "correct_request";
            return AgentResponse<T>.Failure(error.Code, error.Retryable, action);
        }
        catch (ArgumentException) { return AgentResponse<T>.Failure("invalid_argument"); }
        catch { return AgentResponse<T>.Failure("dependency_unavailable", true, "retry_or_reconcile"); }
    }

    private AgentReferenceProtector.Reference ReadAgentReference(string token) =>
        _agentReferences.Value.Read(token, DateTimeOffset.UtcNow) ?? throw new AgentRequestException("reference_unavailable");

    /// <summary>Rechecks the original type/scope eligibility after the scan; concurrent changes cannot widen the requested projection.</summary>
    internal static bool IsCurrentAgentCandidate(MemorySearchEligibility eligibility, MemoryRecord current, MemoryLexicalRecallItem scanned) =>
        eligibility.IsEligible(current) && IsActive(current) && current.CanonicalText == scanned.Content;

    private static bool IsActive(MemoryRecord record) => record.Status == MemoryLifecycleStatus.Active &&
        (record.ExpiresAt is null || record.ExpiresAt > DateTimeOffset.UtcNow);

    /// <summary>Projects only allowlisted record fields and a server-issued opaque locator.</summary>
    private AgentMemory ToAgentMemory(MemoryRecord record)
    {
        _activity?.Target(record.Id.Value);
        return new(record.Type.ToString(), record.CanonicalText,
            _agentReferences.Value.Issue(record.Id.Value, record.Version, record.ExpiresAt, DateTimeOffset.UtcNow), record.Version, record.UpdatedAt);
    }

    /// <summary>Issues a citation for a current exact-scoped canonical projection without deserializing unrelated private source JSON.</summary>
    private AgentMemory ToAgentMemory(MemoryRecallSource source)
    {
        _activity?.Target(source.Id.Value);
        return new(source.Type.ToString(), source.Content,
            _agentReferences.Value.Issue(source.Id.Value, source.Revision, source.ExpiresAt, DateTimeOffset.UtcNow), source.Revision, source.UpdatedAt);
    }

    /// <summary>Converges the character/4 estimate including its own serialized numeric value and the full response envelope.</summary>
    private static AgentContext EstimateContext(List<AgentMemory> items, int omitted)
    {
        var context = new AgentContext(items, omitted, 0, "serialized_utf16_chars_div4_v1");
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var cost = (JsonSerializer.Serialize(AgentResponse<AgentContext>.Success(context)).Length + 3) / 4;
            if (cost == context.EstimatedTokenCost) return context;
            context = context with { EstimatedTokenCost = cost };
        }
        return context;
    }

    /// <summary>Creates server-owned provenance; caller labels never become identity, scope or trusted source authority.</summary>
    private MemoryRecordInput AgentRecordInput(MemoryRecordType type, string content, DateTimeOffset? expiry, string requestKey) =>
        new(type, NormalizeWhitespace(content), null, .6d, .5d, [],
            new($"{_configuration.OriginClient}-mcp", null, null, null, null, null, null,
                [new SourceEvidenceRef($"{_configuration.OriginClient}-agent:{Hash(requestKey)}")]), expiry);

    private CommandEnvelope AgentEnvelope(string requestKey) => new(new(Guid.NewGuid().ToString("D")), Hash(requestKey),
        _configuration.Actor, new(Guid.NewGuid().ToString("D")), _configuration.Scope, ContractVersion);

    /// <summary>Uses Core validation/idempotency and returns no durable IDs or provenance from its richer internal result.</summary>
    private async Task<AgentWriteReceipt> WriteAgentRecordAsync(MemoryRecordInput input, string requestKey, CancellationToken cancellationToken)
    {
        ValidateRequestKey(requestKey);
        var result = await _commands.RememberAsync(new(AgentEnvelope("structured:" + requestKey), input), cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            if (result.Error.Code is MemoryErrorCode.DependencyFailure or MemoryErrorCode.PolicyNotConfigured)
                throw new AgentRequestException("dependency_unavailable", true);
            throw new AgentRequestException(result.Error.Code is MemoryErrorCode.IdempotencyKeyConflict or MemoryErrorCode.Conflict ? "request_conflict" : "invalid_argument");
        }
        return new(result.Outcome.ToString(), result.Memory is not null && IsActive(result.Memory) ? ToAgentMemory(result.Memory) : null);
    }

    /// <summary>Accepts an absolute expiry to make retries deterministic; relative TTLs must be resolved once by the caller.</summary>
    private static DateTimeOffset? ParseAgentExpiry(string? text)
    {
        if (text is null) return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry) || expiry.Offset != TimeSpan.Zero ||
            expiry <= DateTimeOffset.UtcNow || expiry > DateTimeOffset.UtcNow.AddDays(90)) throw new AgentRequestException("invalid_argument");
        return expiry;
    }

    /// <summary>Converts only symbolic record types to a narrowing eligibility filter; an empty filter remains empty.</summary>
    private static IReadOnlySet<MemoryRecordType>? ParseAgentTypes(string[]? types)
    {
        if (types is null) return null;
        if (types.Length > 12) throw new AgentRequestException("invalid_argument");
        var result = new HashSet<MemoryRecordType>();
        foreach (var name in types)
        {
            if (!Enum.TryParse<MemoryRecordType>(name, true, out var type) || !Enum.IsDefined(type) ||
                !string.Equals(name, type.ToString(), StringComparison.OrdinalIgnoreCase)) throw new AgentRequestException("invalid_argument");
            result.Add(type);
        }
        return result;
    }

    private static void ValidateRequestKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100 || !Regex.IsMatch(key, @"^[A-Za-z0-9_-]+$")) throw new AgentRequestException("invalid_argument");
    }

    private static void ValidateAgentLines(string[] lines)
    {
        if (lines is null || lines.Length > 12) throw new AgentRequestException("invalid_argument");
        foreach (var line in lines) ValidateAgentText(line, 1000);
    }

    /// <summary>Rejects obvious credential payloads and private filesystem locators; this conservative guard is not a general secret detector.</summary>
    private static void ValidateAgentText(string text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum) throw new AgentRequestException("invalid_argument");
        if (Regex.IsMatch(text, @"(?i)(/users/|/home/|file://|[a-z]:\\|-----BEGIN|(?:^|\s)/\S+|Bearer\s+|sk-[a-z0-9]{12}|(?:password|api[_-]?key|secret|token)\s*[:=]\s*\S+)"))
            throw new AgentRequestException("unsafe_content");
    }
}
