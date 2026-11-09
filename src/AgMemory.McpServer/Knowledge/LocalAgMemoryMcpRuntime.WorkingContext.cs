using System.Text.Json;
using System.Runtime.CompilerServices;
using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;

namespace AgMemory.McpServer;

public sealed partial class LocalAgMemoryMcpRuntime
{
    /// <summary>
    /// Builds a bounded shared-authority response. Nonempty queries use the configured canonical ranking;
    /// empty working requests hydrate only current warm entries. Required L2 is never silently truncated.
    /// </summary>
    public Task<AgentResponse<KnowledgeContextView>> WorkingContextAsync(string query, int budget = 2048,
        string mode = "working", CancellationToken cancellationToken = default) =>
        WorkingContextCoreAsync(null, query, budget, mode, cancellationToken);

    /// <summary>Reads only the named task's L2 while canonical search and warm residency retain the configured authority.</summary>
    public Task<AgentResponse<KnowledgeContextView>> TaskContextAsync(string taskKey, string query = "", int budget = 2048,
        string mode = "working", CancellationToken cancellationToken = default) =>
        WorkingContextCoreAsync(taskKey ?? string.Empty, query, budget, mode, cancellationToken);

    /// <summary>Shares source validation and packing; explicit task requests never fall back to unrelated legacy L2.</summary>
    private Task<AgentResponse<KnowledgeContextView>> WorkingContextCoreAsync(string? taskKey, string query, int budget,
        string mode, CancellationToken cancellationToken) => SafeKnowledgeAsync(async () =>
    {
        if (query is null || query.Length > 1000 || budget is < 256 or > 8192 || mode is not ("working" or "search"))
            throw new ArgumentException("Invalid working context request.");
        if (mode == "search" && string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Search requires a query.");
        if (!string.IsNullOrWhiteSpace(query)) ValidateAgentText(query, 1000);
        if (taskKey is not null) KnowledgeProfileIdentity.ValidateTaskKey(taskKey);
        _activity?.Query(query);
        var state = await Knowledge.GetAsync(cancellationToken).ConfigureAwait(false);
        var workingState = taskKey is null ? state : await TaskKnowledge(taskKey).GetAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var working = mode == "working" && workingState.WorkingSet?.ExpiresAtUtc > now ? workingState.WorkingSet : null;
        var residents = new List<MemoryRecord>();
        IReadOnlyList<MemoryLexicalRecallItem>? ranked = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var eligibility = new MemorySearchEligibility(AgentScopes, null, now);
        // An empty request resumes the bounded warm set without scanning the canonical corpus.
        // Query requests must search canonical data: a partial warm hit cannot prove that better answers are absent.
        if (mode == "working" && string.IsNullOrWhiteSpace(query))
        {
            foreach (var resident in state.Warm.Values)
            {
                if (resident.ExpiresAtUtc <= now) continue;
                var source = await _store.GetAsync(AgentScopes, new(resident.SourceId), cancellationToken).ConfigureAwait(false);
                if (source is null || !IsActive(source) || source.Version != resident.SourceRevision) continue;
                if (!HasCurrentWarmResidency(state, source, now)) continue;
                residents.Add(source);
                if (residents.Count == 10) break;
            }
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            var candidates = await SearchAgentCandidatesAsync(eligibility, query, 64,
                cancellationToken).ConfigureAwait(false);
            ranked = candidates.Items;
        }
        var selected = new List<KnowledgeContextItem>();
        var context = new KnowledgeContextView(selected, working, 0, ranked?.Count ?? residents.Count, mode, "knowledge-working-v2")
        {
            WorkingSetRevision = workingState.WorkingSet?.Revision ?? 0,
            TaskKey = taskKey
        };
        if (EstimateKnowledgeContext(context).EstimatedTokenCost > budget)
            throw new AgentRequestException("required_context_exceeds_budget");
        await foreach (var source in ReadContextSourcesAsync(eligibility, ranked, residents, cancellationToken).ConfigureAwait(false))
        {
            if (!seen.Add(source.Id.Value) || source.ExpiresAt <= DateTimeOffset.UtcNow) continue;
            var level = mode == "working" && HasCurrentWarmResidency(state, source.Id.Value, source.Revision, now) ? "L3" : "L10";
            var item = ToKnowledgeContextItem(state, source, level);
            // Try each complete candidate; skipping a large hit leaves room for useful lower-ranked answers.
            selected.Add(item);
            var proposed = context with { OmittedCandidateCount = context.OmittedCandidateCount - 1 };
            if (EstimateKnowledgeContext(proposed).EstimatedTokenCost > budget)
                selected.RemoveAt(selected.Count - 1);
            else
                context = proposed;
            if (selected.Count == 10) break;
        }
        return EstimateKnowledgeContext(context);
    });

    /// <summary>Projects current canonical metadata only; stale classifications and feedback never transfer to a new source revision.</summary>
    private KnowledgeContextItem ToKnowledgeContextItem(KnowledgeState state, MemoryRecallSource source, string fallbackLevel)
    {
        var level = fallbackLevel;
        var review = false;
        if (state.Metadata.TryGetValue(source.Id.Value, out var metadata) && metadata.SourceRevision == source.Revision)
        {
            review = metadata.ReviewAtUtc <= DateTimeOffset.UtcNow;
            if (fallbackLevel != "L3") level = metadata.Class switch
            {
                KnowledgeClass.Knowledge => "L11",
                KnowledgeClass.Procedure => "L12",
                KnowledgeClass.Experience => "L13",
                _ => "L10"
            };
        }
        return new(ToAgentMemory(source), level, KnowledgeService.GetUtility(state, source.Id.Value, source.Revision), review);
    }

    /// <summary>Reads ranked sources in one bounded current projection; empty working requests yield already validated warm residents.</summary>
    private async IAsyncEnumerable<MemoryRecallSource> ReadContextSourcesAsync(MemorySearchEligibility eligibility,
        IReadOnlyList<MemoryLexicalRecallItem>? ranked, IReadOnlyList<MemoryRecord> residents,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (ranked is null)
        {
            foreach (var source in residents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(source.Id, source.Type, source.CanonicalText, source.Version, source.UpdatedAt, source.ExpiresAt);
            }
            yield break;
        }
        var current = await _store.ReadCurrentRecallSourcesAsync(eligibility, ranked, cancellationToken).ConfigureAwait(false);
        foreach (var source in current) yield return source;
    }

    /// <summary>Warmth describes residency only; stale revisions and excluded metadata cannot label a canonical result as warm.</summary>
    private static bool HasCurrentWarmResidency(KnowledgeState state, MemoryRecord source, DateTimeOffset now) =>
        HasCurrentWarmResidency(state, source.Id.Value, source.Version, now);

    private static bool HasCurrentWarmResidency(KnowledgeState state, string sourceId, long revision, DateTimeOffset now)
    {
        if (!state.Warm.TryGetValue(sourceId, out var resident) || resident.ExpiresAtUtc <= now || resident.SourceRevision != revision) return false;
        return !state.Metadata.TryGetValue(sourceId, out var metadata) || metadata.SourceRevision == revision && !metadata.WarmExcluded;
    }

    /// <summary>Converges the same serialized-envelope character estimate used by versioned agent context.</summary>
    private static KnowledgeContextView EstimateKnowledgeContext(KnowledgeContextView context)
    {
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var cost = (JsonSerializer.Serialize(AgentResponse<KnowledgeContextView>.Success(context)).Length + 3) / 4;
            if (cost == context.EstimatedTokenCost) return context;
            context = context with { EstimatedTokenCost = cost };
        }
        return context;
    }
}
