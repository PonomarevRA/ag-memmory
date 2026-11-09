using AgMemory.Contracts;
using System.Runtime.CompilerServices;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;

namespace AgMemory.McpServer;

public sealed partial class LocalAgMemoryMcpRuntime
{
    private string KnowledgeProfileRef => KnowledgeProfileIdentity.GetKnowledge(
        _configuration.StoragePath, _configuration.Actor, _configuration.Scope, _configuration.KnowledgeTeamId);
    private IKnowledgeStore KnowledgeStore => _configuration.UseKnowledgeJournal
        ? new KnowledgeJournalStore(_configuration.StoragePath) : new KnowledgeFileStore(_configuration.StoragePath);
    private KnowledgeService Knowledge => new(KnowledgeStore, KnowledgeProfileRef);
    private KnowledgeService TaskKnowledge(string taskKey) => new(KnowledgeFileStore.ForTaskWorking(_configuration.StoragePath),
        KnowledgeProfileIdentity.GetTaskWorking(_configuration.StoragePath, _configuration.Actor,
            _configuration.Scope, _configuration.KnowledgeTeamId, taskKey));
    private string ProcedurePurpose => KnowledgePurpose("knowledge-procedure:");
    private string PresentationPurpose => KnowledgePurpose("knowledge-presentation:");

    /// <summary>Keep legacy actor tokens stable while rejecting team tokens after a server-side partition change.</summary>
    private string KnowledgePurpose(string purpose) => _configuration.KnowledgeTeamId is null ? purpose : purpose + KnowledgeProfileRef + ":";

    /// <summary>Advertises only implemented knowledge operations; task labels never become authenticated participants.</summary>
    public Task<AgentResponse<KnowledgeCapabilitiesView>> KnowledgeCapabilitiesAsync() => KnowledgeCapabilitiesAsync(false);

    /// <summary>Optionally reads capacity of the configured knowledge authority; default discovery does not open its store.</summary>
    public Task<AgentResponse<KnowledgeCapabilitiesView>> KnowledgeCapabilitiesAsync(bool includeUsage,
        CancellationToken cancellationToken = default) => SafeAgentAsync(async () =>
    {
        var capacity = includeUsage ? KnowledgeService.GetCapacity(await Knowledge.GetAsync(cancellationToken).ConfigureAwait(false)) : null;
        if (capacity is not null && KnowledgeStore is IKnowledgeReplayStore ledger)
        {
            var profileUsed = await ledger.CountRequestsAsync(KnowledgeProfileRef, cancellationToken).ConfigureAwait(false);
            var used = await ledger.CountNamespaceRequestsAsync(KnowledgeProfileRef, cancellationToken).ConfigureAwait(false);
            var collections = capacity.Collections.ToList();
            collections[0] = new("requests", used, ledger.MaximumRequests, Math.Max(0, ledger.MaximumRequests - used), used >= ledger.MaximumRequests * .8);
            capacity = capacity with { Collections = collections, ReplayRetention = "durable_journal_no_eviction_namespace_limits_apply",
                RequestCountScope = "namespace", ProfileRequestCount = profileUsed };
        }
        return new KnowledgeCapabilitiesView("agmemory-knowledge-v1", _agentReferences.Value.ProfileRef,
            ["memory_metadata", "memory_procedures", "memory_procedure", "memory_occurrence", "memory_application",
             "memory_present", "memory_feedback", "memory_occurrence_update", "working_update", "working_context",
             "task_working_update", "task_context", "memory_claim", "memory_claims", "memory_evidence",
             "memory_procedure_facets", "memory_procedure_discovery", "memory_review", "memory_warm_exclude"],
            "shared_configured_authority", "caller_labels_unverified", false, false, false, 8192, "explicit_reversible_warm_exclusion")
        { KnowledgeProfileRef = KnowledgeProfileRef, KnowledgeSharing = _configuration.KnowledgeTeamId is null ? "actor" : "local_team", Capacity = capacity,
          KnowledgeStorage = _configuration.UseKnowledgeJournal ? "journal_v2_explicit_migration" : "legacy_v1",
          JournalMaximumDatabaseBytes = _configuration.UseKnowledgeJournal ? KnowledgeJournalStore.MaximumDatabaseBytes : null };
    }, UsageAuditOperation.Status);

    /// <summary>Classifies one current canonical revision without granting caller-controlled foundation approval.</summary>
    public Task<AgentResponse<KnowledgeMutationResult>> KnowledgeMetadataAsync(string citationRef, string knowledgeClass,
        string applicability, string requestKey, long expectedMetadataRevision = 0, CancellationToken cancellationToken = default) => SafeKnowledgeAsync(async () =>
    {
        ValidateRequestKey(requestKey);
        var source = await CurrentKnowledgeSourceAsync(citationRef, cancellationToken).ConfigureAwait(false);
        ValidateAgentText(applicability, 1000);
        var kind = ParseKnowledgeEnum<KnowledgeClass>(knowledgeClass);
        var result = await Knowledge.SetMetadataAsync(new(source.Id.Value, source.Version, kind, FoundationStatus.None,
            applicability, null), expectedMetadataRevision, requestKey, cancellationToken).ConfigureAwait(false);
        return result with { Key = string.Empty };
    });

    /// <summary>Returns applicable procedure revisions as data with actor/profile-bound opaque locators.</summary>
    public Task<AgentResponse<IReadOnlyList<KnowledgeProcedureView>>> KnowledgeProceduresAsync(string query, string environment,
        int limit = 5, CancellationToken cancellationToken = default) => SafeKnowledgeAsync(async () =>
    {
        ValidateAgentText(query, 1000); ValidateAgentText(environment, 500);
        _activity?.Query(query);
        if (limit is < 1 or > 10) throw new ArgumentException("Invalid limit.");
        var service = Knowledge;
        var state = await service.GetAsync(cancellationToken).ConfigureAwait(false);
        var selected = KnowledgeService.FindProcedures(state, query, environment, limit);
        var views = new List<KnowledgeProcedureView>();
        foreach (var procedure in selected) views.Add(ToKnowledgeProcedure(procedure));
        return (IReadOnlyList<KnowledgeProcedureView>)views;
    });

    /// <summary>Creates or replaces one procedure under CAS; the request key is stable and the returned locator is opaque.</summary>
    public Task<AgentResponse<KnowledgeProcedureView>> KnowledgeProcedureAsync(string? procedureRef, long expectedRevision,
        string goal, string[] aliases, string applicability, string preconditions, string steps, string verification,
        string? runbookRef, string requestKey, CancellationToken cancellationToken = default) => SafeKnowledgeAsync(async () =>
    {
        ValidateRequestKey(requestKey);
        ValidateAgentText(goal, 1000); ValidateAgentLines(aliases); ValidateAgentText(applicability, 1000);
        ValidateAgentText(preconditions, 1000); ValidateAgentText(steps, 3000); ValidateAgentText(verification, 1000);
        if (runbookRef is not null) ValidateAgentText(runbookRef, 180);
        var key = "procedure-" + Hash(requestKey)[..24];
        if (procedureRef is not null)
        {
            var reference = ReadKnowledgeReference(procedureRef, ProcedurePurpose);
            if (reference.Revision != expectedRevision) throw new AgentRequestException("reference_unavailable");
            key = reference.Id[ProcedurePurpose.Length..];
        }
        else if (expectedRevision != 0) throw new ArgumentException("New procedure revision must be zero.");
        var procedure = new KnowledgeProcedure(key, expectedRevision + 1, goal, aliases, applicability, preconditions,
            steps, verification, runbookRef, DateTimeOffset.UtcNow);
        var service = Knowledge;
        var result = await service.UpsertProcedureAsync(procedure, expectedRevision, requestKey, cancellationToken).ConfigureAwait(false);
        var state = await service.GetAsync(cancellationToken).ConfigureAwait(false);
        foreach (var saved in state.ProcedureHistory.Values)
            if (saved.Key == result.Key && saved.Revision == result.Revision) return ToKnowledgeProcedure(saved);
        throw new AgentRequestException("reference_unavailable");
    });

    /// <summary>Records an actual occurrence separately from its symptom family; identical write retries do not count again.</summary>
    public Task<AgentResponse<KnowledgeMutationResult>> KnowledgeOccurrenceAsync(string occurrenceKey, string familyKey,
        string symptom, string environment, string status, string requestKey, CancellationToken cancellationToken = default) =>
        SafeKnowledgeAsync(() =>
        {
            ValidateKnowledgeKey(occurrenceKey); ValidateKnowledgeKey(familyKey); ValidateRequestKey(requestKey);
            ValidateAgentText(symptom, 1000); ValidateAgentText(environment, 500);
            return Knowledge.RecordOccurrenceAsync(new(occurrenceKey, familyKey, symptom, environment,
                ParseKnowledgeEnum<ProblemOccurrenceStatus>(status), DateTimeOffset.UtcNow), requestKey, cancellationToken);
        });

    /// <summary>CAS-updates the reported state of an existing incident without inventing a new occurrence or verified cause.</summary>
    public Task<AgentResponse<KnowledgeMutationResult>> KnowledgeOccurrenceUpdateAsync(string occurrenceKey, string status,
        long expectedRevision, string requestKey, CancellationToken cancellationToken = default) => SafeKnowledgeAsync(() =>
    {
        ValidateKnowledgeKey(occurrenceKey); ValidateRequestKey(requestKey);
        return Knowledge.UpdateOccurrenceAsync(occurrenceKey, ParseKnowledgeEnum<ProblemOccurrenceStatus>(status),
            expectedRevision, requestKey, cancellationToken);
    });

    /// <summary>Records a client-reported procedure attempt; no caller can grant itself verified-success authority.</summary>
    public Task<AgentResponse<KnowledgeMutationResult>> KnowledgeApplicationAsync(string applicationKey, string procedureRef,
        string environment, string outcome, string evidence, string requestKey, string? occurrenceKey = null, CancellationToken cancellationToken = default) =>
        SafeKnowledgeAsync(async () =>
        {
            ValidateKnowledgeKey(applicationKey); ValidateRequestKey(requestKey);
            if (occurrenceKey is not null) ValidateKnowledgeKey(occurrenceKey);
            ValidateAgentText(environment, 500); ValidateAgentText(evidence, 1000);
            var reference = ReadKnowledgeReference(procedureRef, ProcedurePurpose);
            var kind = ParseKnowledgeEnum<ProcedureApplicationOutcome>(outcome);
            if (kind == ProcedureApplicationOutcome.VerifiedSuccess) throw new AgentRequestException("authority_unavailable");
            return await Knowledge.RecordApplicationAsync(new(applicationKey, reference.Id[ProcedurePurpose.Length..],
                reference.Revision, environment, kind, evidence, DateTimeOffset.UtcNow, occurrenceKey), requestKey, cancellationToken).ConfigureAwait(false);
        });

    /// <summary>Registers a current cited presentation and returns a purpose-bound receipt for later feedback.</summary>
    public Task<AgentResponse<KnowledgePresentationView>> KnowledgePresentAsync(string citationRef, string taskKey,
        string requestKey, CancellationToken cancellationToken = default) => SafeKnowledgeAsync(async () =>
    {
        ValidateKnowledgeKey(taskKey); ValidateRequestKey(requestKey);
        var source = await CurrentKnowledgeSourceAsync(citationRef, cancellationToken).ConfigureAwait(false);
        var presentation = await Knowledge.PresentAsync(source.Id.Value, source.Version, taskKey, requestKey,
            cancellationToken).ConfigureAwait(false);
        var receipt = _agentReferences.Value.Issue(PresentationPurpose + presentation.Receipt, 1,
            presentation.ExpiresAtUtc, DateTimeOffset.UtcNow);
        return new KnowledgePresentationView(receipt, presentation.ExpiresAtUtc);
    });

    /// <summary>Accepts one revisioned client vote for a real presentation; source freshness is rechecked before changing utility.</summary>
    public Task<AgentResponse<KnowledgeMutationResult>> KnowledgeFeedbackAsync(string receiptRef, string kind,
        long expectedRevision, string requestKey, CancellationToken cancellationToken = default) => SafeKnowledgeAsync(async () =>
    {
        ValidateRequestKey(requestKey);
        var reference = ReadKnowledgeReference(receiptRef, PresentationPurpose);
        var service = Knowledge;
        var state = await service.GetAsync(cancellationToken).ConfigureAwait(false);
        var receipt = reference.Id[PresentationPurpose.Length..];
        if (!state.Presentations.TryGetValue(receipt, out var presentation)) throw new AgentRequestException("reference_unavailable");
        var current = await _store.GetAsync(AgentScopes, new(presentation.SourceId), cancellationToken).ConfigureAwait(false);
        if (current is null || !IsActive(current) || current.Version != presentation.SourceRevision)
            throw new AgentRequestException("reference_unavailable");
        var result = await service.FeedbackAsync(receipt, ParseKnowledgeEnum<KnowledgeFeedbackKind>(kind),
            expectedRevision, requestKey, cancellationToken).ConfigureAwait(false);
        return result with { Key = string.Empty };
    });

    /// <summary>Updates the shared configured-authority L2 summary; the server grants no private participant identity.</summary>
    public Task<AgentResponse<KnowledgeMutationResult>> KnowledgeWorkingAsync(string goal, string[] facts, string[] assumptions,
        string[] questions, string nextAction, long expectedRevision, string requestKey, CancellationToken cancellationToken = default) =>
        SafeKnowledgeAsync(() =>
        {
            ValidateRequestKey(requestKey);
            ValidateAgentText(goal, 1000); ValidateAgentLines(facts); ValidateAgentLines(assumptions);
            ValidateAgentLines(questions); ValidateAgentText(nextAction, 1000);
            return Knowledge.SetWorkingSetAsync(new(expectedRevision + 1, goal, facts, assumptions, questions, nextAction,
                DateTimeOffset.UtcNow.AddHours(24)), expectedRevision, requestKey, cancellationToken);
        });

    /// <summary>CAS-updates the named task's 24h L2 without modifying legacy shared state or other task partitions.</summary>
    public Task<AgentResponse<KnowledgeMutationResult>> TaskWorkingAsync(string taskKey, string goal, string[] facts,
        string[] assumptions, string[] questions, string nextAction, long expectedRevision, string requestKey,
        CancellationToken cancellationToken = default) => SafeKnowledgeAsync(() =>
        {
            KnowledgeProfileIdentity.ValidateTaskKey(taskKey);
            ValidateRequestKey(requestKey);
            ValidateAgentText(goal, 1000); ValidateAgentLines(facts); ValidateAgentLines(assumptions);
            ValidateAgentLines(questions); ValidateAgentText(nextAction, 1000);
            return TaskKnowledge(taskKey).SetWorkingSetAsync(new(expectedRevision + 1, goal, facts, assumptions, questions,
                nextAction, DateTimeOffset.UtcNow.AddHours(24)), expectedRevision, requestKey, cancellationToken);
        });

    /// <summary>Returns source-safe review proposals; it never deletes or mutates canonical content.</summary>
    public Task<AgentResponse<IReadOnlyList<KnowledgeReviewView>>> KnowledgeReviewAsync(CancellationToken cancellationToken = default) =>
        SafeKnowledgeAsync(async () =>
        {
            var service = Knowledge;
            var state = await service.GetAsync(cancellationToken).ConfigureAwait(false);
            var views = new List<KnowledgeReviewView>();
            foreach (var item in KnowledgeService.Review(state))
            {
                var source = await _store.GetAsync(AgentScopes, new(item.SourceId), cancellationToken).ConfigureAwait(false);
                if (source is null || !IsActive(source) || source.Version != item.SourceRevision) continue;
                views.Add(new(ToAgentMemory(source).CitationRef, source.Version, item.Reason, item.WarmExcluded));
                if (views.Count == 32) break;
            }
            return (IReadOnlyList<KnowledgeReviewView>)views;
        });

    /// <summary>Explicitly excludes or restores only warm residency after validating the current source revision.</summary>
    public Task<AgentResponse<KnowledgeMutationResult>> KnowledgeWarmExcludeAsync(string citationRef, bool excluded,
        string requestKey, CancellationToken cancellationToken = default) => SafeKnowledgeAsync(async () =>
    {
        ValidateRequestKey(requestKey);
        var source = await CurrentKnowledgeSourceAsync(citationRef, cancellationToken).ConfigureAwait(false);
        var result = await Knowledge.SetWarmExcludedAsync(source.Id.Value, excluded, requestKey, cancellationToken).ConfigureAwait(false);
        return result with { Key = string.Empty };
    });

    /// <summary>Maps feature validation/conflicts without exposing provider errors, IDs, paths or caller payloads.</summary>
    private Task<AgentResponse<T>> SafeKnowledgeAsync<T>(Func<Task<T>> operation, [CallerMemberName] string operationName = "unknown") => SafeAgentAsync(async () =>
    {
        try { return await operation().ConfigureAwait(false); }
        catch (InvalidOperationException error)
        {
            var code = error.Message switch
            {
                "knowledge_capacity_exceeded" => "capacity_exceeded",
                "knowledge_conflict" or "occurrence_conflict" or "application_conflict" => "request_conflict",
                "presentation_unavailable" or "procedure_unavailable" or "source_unavailable" or "occurrence_unavailable" => "reference_unavailable",
                "foundation_protected" => "authority_unavailable",
                "invalid_transition" => "invalid_argument",
                "journal_migration_required" => "migration_required",
                "request_conflict" or "revision_conflict" => error.Message,
                _ => null
            };
            if (code is not null) throw new AgentRequestException(code);
            throw;
        }
    }, operationName: operationName);

    /// <summary>Validates the exact encrypted reference purpose before using its internal locator.</summary>
    private AgentReferenceProtector.Reference ReadKnowledgeReference(string value, string purpose)
    {
        var reference = ReadAgentReference(value);
        if (!reference.Id.StartsWith(purpose, StringComparison.Ordinal)) throw new AgentRequestException("reference_unavailable");
        return reference;
    }

    /// <summary>Rechecks lifecycle, authority and revision of a canonical citation before a metadata or utility operation.</summary>
    private async Task<MemoryRecord> CurrentKnowledgeSourceAsync(string citationRef, CancellationToken cancellationToken)
    {
        var reference = ReadAgentReference(citationRef);
        var source = await _store.GetAsync(AgentScopes, new(reference.Id), cancellationToken).ConfigureAwait(false);
        if (source is null || !IsActive(source) || source.Version != reference.Revision)
            throw new AgentRequestException("reference_unavailable");
        return source;
    }

    /// <summary>Rejects numeric/undefined enum aliases instead of widening a closed feature vocabulary.</summary>
    private static T ParseKnowledgeEnum<T>(string text) where T : struct, Enum
    {
        if (!Enum.TryParse<T>(text, true, out var value) || !Enum.IsDefined(value) ||
            !string.Equals(text, value.ToString(), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid feature value.");
        return value;
    }

    /// <summary>Restricts operational labels to symbolic keys, avoiding private paths or credential-shaped free text.</summary>
    private static void ValidateKnowledgeKey(string key) => ValidateRequestKey(key);

    private KnowledgeProcedureView ToKnowledgeProcedure(KnowledgeProcedure procedure) => new(
        _agentReferences.Value.Issue(ProcedurePurpose + procedure.Key, procedure.Revision, null, DateTimeOffset.UtcNow),
        procedure.Revision, procedure.Goal, procedure.Aliases, procedure.Applicability, procedure.Preconditions,
        procedure.Steps, procedure.Verification, procedure.RunbookRef, procedure.UpdatedAtUtc);
}
