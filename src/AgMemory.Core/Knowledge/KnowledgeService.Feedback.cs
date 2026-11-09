using AgMemory.Contracts;

namespace AgMemory.Core;

public sealed partial class KnowledgeService
{
    /// <summary>Issues a durable presentation receipt for a canonical source already validated by the host. A receipt means exposure, not use.</summary>
    public async Task<KnowledgePresentation> PresentAsync(string sourceId, long revision, string taskKey, string requestKey,
        CancellationToken cancellationToken = default)
    {
        Text(sourceId, 200); Text(taskKey, 160);
        if (revision <= 0) throw new ArgumentException("invalid_argument");
        var result = await MutateAsync("presentation", requestKey, new { sourceId, revision, taskKey }, (state, now) =>
        {
            var receipt = Guid.NewGuid().ToString("N");
            state.Presentations.Add(receipt, new(receipt, sourceId, revision, taskKey, now, now.AddHours(24)));
            if (!state.Metadata.TryGetValue(sourceId, out var metadata) || !metadata.WarmExcluded)
            {
                if (state.Warm.Count >= MaximumWarmEntries && !state.Warm.ContainsKey(sourceId))
                {
                    var oldest = state.Warm.Values.MinBy(item => item.ExpiresAtUtc)!;
                    state.Warm.Remove(oldest.SourceId);
                }
                state.Warm[sourceId] = new(sourceId, revision, now.AddDays(7));
            }
            return new("presented", receipt, revision);
        }, cancellationToken).ConfigureAwait(false);
        var committed = await GetAsync(cancellationToken).ConfigureAwait(false);
        return committed.Presentations[result.Key];
    }

    /// <summary>Updates one vote per source revision and self-reported task with CAS. Caller cannot impersonate human feedback.</summary>
    public Task<KnowledgeMutationResult> FeedbackAsync(string receipt, KnowledgeFeedbackKind kind, long expectedRevision,
        string requestKey, CancellationToken cancellationToken = default)
    {
        Text(receipt, 200);
        if (!Enum.IsDefined(kind)) throw new ArgumentException("invalid_argument");
        return MutateAsync("feedback", requestKey, new { receipt, kind, expectedRevision }, (state, now) =>
        {
            if (!state.Presentations.TryGetValue(receipt, out var presented) || presented.ExpiresAtUtc <= now)
                throw new InvalidOperationException("presentation_unavailable");
            var dimension = kind switch
            {
                KnowledgeFeedbackKind.Applied => "applied",
                KnowledgeFeedbackKind.Helpful or KnowledgeFeedbackKind.Harmful => "utility",
                KnowledgeFeedbackKind.Irrelevant => "relevance",
                _ => "content_issue"
            };
            var voteKey = System.Text.Json.JsonSerializer.Serialize(new object[] { presented.SourceId, presented.SourceRevision, presented.TaskKey, dimension });
            Revision(state.Feedback.TryGetValue(voteKey, out var prior) ? prior.Revision : 0, expectedRevision);
            var vote = new KnowledgeFeedback(receipt, presented.TaskKey, kind, expectedRevision + 1, now);
            state.Feedback[voteKey] = vote;
            return new("updated", voteKey, vote.Revision);
        }, cancellationToken);
    }

    /// <summary>Computes independent self-reported utility without changing canonical importance, confidence or verification.</summary>
    public static KnowledgeUtility GetUtility(KnowledgeState state, string sourceId, long revision, DateTimeOffset? asOfUtc = null)
    {
        var now = asOfUtc ?? DateTimeOffset.UtcNow;
        var presented1 = 0; var presented7 = 0; var presented30 = 0;
        var applied1 = 0; var applied7 = 0; var applied30 = 0;
        DateTimeOffset? lastApplied = null;
        var presented = 0; var applied = 0; var helpful = 0; var harmful = 0; var irrelevant = 0; var outdated = 0; var incorrect = 0;
        DateTimeOffset? last = null;
        var tasks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exposure in state.Presentations.Values)
        {
            if (exposure.SourceId != sourceId || exposure.SourceRevision != revision) continue;
            presented++;
            CountWindows(exposure.PresentedAtUtc, now, ref presented1, ref presented7, ref presented30);
            if (last is null || exposure.PresentedAtUtc > last) last = exposure.PresentedAtUtc;
        }
        foreach (var feedback in state.Feedback.Values)
        {
            if (!state.Presentations.TryGetValue(feedback.Receipt, out var exposure) || exposure.SourceId != sourceId || exposure.SourceRevision != revision) continue;
            tasks.Add(feedback.TaskKey);
            switch (feedback.Kind)
            {
                case KnowledgeFeedbackKind.Helpful: helpful++; break;
                case KnowledgeFeedbackKind.Harmful: harmful++; break;
                case KnowledgeFeedbackKind.Irrelevant: irrelevant++; break;
                case KnowledgeFeedbackKind.Outdated: outdated++; break;
                case KnowledgeFeedbackKind.Incorrect: incorrect++; break;
                case KnowledgeFeedbackKind.Applied:
                    applied++;
                    CountWindows(feedback.UpdatedAtUtc, now, ref applied1, ref applied7, ref applied30);
                    if (lastApplied is null || feedback.UpdatedAtUtc > lastApplied) lastApplied = feedback.UpdatedAtUtc;
                    break;
            }
        }
        double? utility = helpful + harmful == 0 ? null : (helpful + 1d) / (helpful + harmful + 2d);
        return new(presented, applied, helpful, harmful, irrelevant, outdated, incorrect, tasks.Count, utility, last, lastApplied, presented1, presented7, presented30, applied1, applied7, applied30);
    }

    /// <summary>Counts nested trailing UTC windows, excluding future timestamps from the observed sample.</summary>
    private static void CountWindows(DateTimeOffset occurredAtUtc, DateTimeOffset now, ref int oneDay, ref int sevenDays, ref int thirtyDays)
    {
        if (occurredAtUtc > now) return;
        if (occurredAtUtc >= now.AddDays(-1)) oneDay++;
        if (occurredAtUtc >= now.AddDays(-7)) sevenDays++;
        if (occurredAtUtc >= now.AddDays(-30)) thirtyDays++;
    }

    /// <summary>Proposes human review from negative evidence. Suggestions do not archive or delete records automatically.</summary>
    public static IReadOnlyList<KnowledgeReviewItem> Review(KnowledgeState state)
    {
        var results = new List<KnowledgeReviewItem>();
        foreach (var metadata in state.Metadata.Values)
        {
            var utility = GetUtility(state, metadata.SourceId, metadata.SourceRevision);
            if (utility.Incorrect > 0 || utility.Outdated > 0)
                results.Add(new(metadata.SourceId, metadata.SourceRevision, "reported_content_issue", metadata.WarmExcluded));
            else if (utility.DistinctTasks >= 5 && utility.Harmful > utility.Helpful)
                results.Add(new(metadata.SourceId, metadata.SourceRevision, "negative_agent_feedback_review_only", metadata.WarmExcluded));
        }
        return results;
    }
}
