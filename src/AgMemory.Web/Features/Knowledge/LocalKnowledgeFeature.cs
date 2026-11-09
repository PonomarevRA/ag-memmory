using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;
using AgMemory.Web.Features.MemoryGraph;

namespace AgMemory.Web.Features.Knowledge;

/// <summary>Reads a fixed server-authority knowledge profile only after endpoint access checks have succeeded.</summary>
public sealed class LocalKnowledgeFeature
{
    private readonly MemoryGraphHostConfiguration? _configuration;
    public bool IsConfigured => _configuration is not null;

    /// <summary>Validates fixed server options without opening a store or creating its signing key.</summary>
    public LocalKnowledgeFeature(MemoryGraphHostOptions options, string dataDirectory) => _configuration = options.TryCreate(dataDirectory);

    /// <summary>Projects only procedure content and aggregate observations; persistent keys, actors and scope never enter the DTO.</summary>
    public async Task<KnowledgeApiResponse> ReadAsync(CancellationToken cancellationToken)
    {
        var config = _configuration ?? throw new InvalidOperationException("Knowledge is unavailable.");
        var profile = KnowledgeProfileIdentity.GetKnowledge(config.StoragePath, config.Actor, config.Scope, config.KnowledgeTeamId);
        var service = new KnowledgeService(new KnowledgeFileStore(config.StoragePath), profile);
        var state = await service.GetAsync(cancellationToken).ConfigureAwait(false);
        var sources = new List<KnowledgeSourceView>();
        var eligibleSourceIds = new HashSet<string>(StringComparer.Ordinal);
        if (state.Metadata.Count > 0)
        {
            await using var canonical = new LanceDbMemoryStore(new(config.StoragePath));
            var scopes = new AuthorizedScopeSet([new ScopeSelector(config.Scope)]);
            sources = await ReadSourcesAsync(state, config.Scope,
                id => canonical.GetAsync(scopes, new(id), cancellationToken), DateTimeOffset.UtcNow, eligibleSourceIds).ConfigureAwait(false);
        }
        return Project(state, eligibleSourceIds) with { Sources = sources };
    }

    /// <summary>Hydrates at most 50 sources and rejects stale metadata, expired content and foreign scopes before exposing observations.</summary>
    internal static async Task<List<KnowledgeSourceView>> ReadSourcesAsync(KnowledgeState state, MemoryScope scope,
        Func<string, Task<MemoryRecord?>> readSource, DateTimeOffset now, ISet<string>? eligibleSourceIds = null)
    {
        var sources = new List<KnowledgeSourceView>();
        var examined = 0;
        foreach (var metadata in state.Metadata.Values)
        {
            if (++examined > 50) break;
            var source = await readSource(metadata.SourceId).ConfigureAwait(false);
            if (source is null || source.Id.Value != metadata.SourceId || source.Scope != scope || source.Version != metadata.SourceRevision ||
                source.Status != MemoryLifecycleStatus.Active || source.ExpiresAt <= now) continue;
            eligibleSourceIds?.Add(metadata.SourceId);
            var tier = metadata.Class switch { KnowledgeClass.Knowledge => "L11", KnowledgeClass.Procedure => "L12", KnowledgeClass.Experience => "L13", _ => "Unclassified" };
            var preview = source.CanonicalText.Length > 400 ? source.CanonicalText[..400] : source.CanonicalText;
            var windows = new List<KnowledgeUtilityWindow>();
            foreach (var days in new[] { 1, 7, 30 }) windows.Add(Window(state, source.Id.Value, source.Version, now.AddDays(-days), days));
            sources.Add(new(tier, preview, source.Version, metadata.WarmExcluded, windows));
        }
        return sources;
    }

    /// <summary>Counts recent presentations separately from timestamped self-reported feedback; unknown utility remains null.</summary>
    private static KnowledgeUtilityWindow Window(KnowledgeState state, string sourceId, long revision, DateTimeOffset cutoff, int days)
    {
        var presented = 0; var helpful = 0; var harmful = 0; var applied = 0;
        var tasks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in state.Presentations.Values)
            if (item.SourceId == sourceId && item.SourceRevision == revision && item.PresentedAtUtc >= cutoff) presented++;
        foreach (var item in state.Feedback.Values)
        {
            if (item.UpdatedAtUtc < cutoff || !state.Presentations.TryGetValue(item.Receipt, out var exposure) ||
                exposure.SourceId != sourceId || exposure.SourceRevision != revision) continue;
            tasks.Add(item.TaskKey);
            if (item.Kind == KnowledgeFeedbackKind.Helpful) helpful++;
            if (item.Kind == KnowledgeFeedbackKind.Harmful) harmful++;
            if (item.Kind == KnowledgeFeedbackKind.Applied) applied++;
        }
        double? utility = helpful + harmful == 0 ? null : (helpful + 1d) / (helpful + harmful + 2d);
        return new(days, presented, applied, helpful + harmful, tasks.Count, utility);
    }

    /// <summary>Produces a bounded read-only browser projection without source identifiers or runbook URLs.</summary>
    internal static KnowledgeApiResponse Project(KnowledgeState state, IReadOnlySet<string>? eligibleSourceIds = null)
    {
        var procedures = new List<KnowledgeProcedureView>();
        foreach (var procedure in state.Procedures.Values.OrderByDescending(item => item.UpdatedAtUtc).Take(50))
            procedures.Add(new(procedure.Goal, procedure.Revision, procedure.Applicability, procedure.Preconditions, procedure.Steps, procedure.Verification));
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in KnowledgeService.Review(state))
        {
            // Feedback about an older revision is historical evidence, not a proposal to review current content.
            if (eligibleSourceIds?.Contains(item.SourceId) != true) continue;
            reasons[item.Reason] = reasons.GetValueOrDefault(item.Reason) + 1;
        }
        var feedback = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in state.Feedback.Values)
        {
            var name = item.Kind.ToString();
            feedback[name] = feedback.GetValueOrDefault(name) + 1;
        }
        var applications = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in state.Applications.Values)
        {
            var name = item.Outcome.ToString();
            applications[name] = applications.GetValueOrDefault(name) + 1;
        }
        var now = DateTimeOffset.UtcNow;
        var activeWarm = 0;
        foreach (var item in state.Warm.Values) if (item.ExpiresAtUtc > now) activeWarm++;
        return new("available", state.Metadata.Count, state.Procedures.Count, state.Occurrences.Count,
            state.Presentations.Count, activeWarm, state.WorkingSet?.ExpiresAtUtc > now,
            procedures, feedback, applications, reasons);
    }
}

/// <summary>Public content allowlist: no permanent IDs, profile keys, authority or raw configuration.</summary>
public sealed record KnowledgeProcedureView(string Goal, long Revision, string Applicability, string Preconditions, string Steps, string Verification);
public sealed record KnowledgeUtilityWindow(int Days, int Presented, int Applied, int RatingSampleSize, int SelfReportedTasks, double? Utility);
public sealed record KnowledgeSourceView(string Class, string Preview, long SourceRevision, bool WarmExcluded, IReadOnlyList<KnowledgeUtilityWindow> Windows);
public sealed record KnowledgeApiResponse(string Status, int ClassifiedCount, int ProcedureCount, int OccurrenceCount,
    int PresentationCount, int ActiveWarmCount, bool WorkingSetActive, IReadOnlyList<KnowledgeProcedureView> Procedures,
    IReadOnlyDictionary<string, int> Feedback, IReadOnlyDictionary<string, int> Applications, IReadOnlyDictionary<string, int> ReviewReasons)
{
    public IReadOnlyList<KnowledgeSourceView> Sources { get; init; } = [];
    public static KnowledgeApiResponse Unavailable { get; } = new("unavailable", 0, 0, 0, 0, 0, false, [],
        new Dictionary<string, int>(), new Dictionary<string, int>(), new Dictionary<string, int>());
}
