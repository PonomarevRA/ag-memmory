using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests.Knowledge;

public sealed class KnowledgeServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Capacity warns before exhaustion without deleting replay receipts or changing the source state.</summary>
    [Fact]
    public void CapacityExposesReplayHeadroomAndKeepsReceipts()
    {
        var state = new KnowledgeState { Version = 7 };
        for (var index = 0; index < 4095; index++)
            state.Requests.Add("request-" + index, new("request-" + index, "fingerprint", "fixture", "result", 1, Now));
        var capacity = KnowledgeService.GetCapacity(state);
        var requests = Assert.Single(capacity.Collections, item => item.Collection == "requests");
        Assert.Equal(4095, requests.Used);
        Assert.Equal(1, requests.Remaining);
        Assert.True(requests.NearLimit);
        Assert.True(capacity.DomainLimitsOnly);
        Assert.Equal("retained_no_automatic_compaction", capacity.ReplayRetention);
        Assert.Equal(4095, state.Requests.Count);
        Assert.Equal(7, state.Version);
        var metadata = Assert.Single(capacity.Collections, item => item.Collection == "metadata");
        Assert.Equal(1024, metadata.Remaining);
        Assert.False(metadata.NearLimit);
    }

    /// <summary>Transport retries ignore server timestamps; changed semantic payloads are conflicts and history stays immutable.</summary>
    [Fact]
    public async Task ProcedureReplayPreservesRevisionAndHistoricalApplications()
    {
        var service = Create();
        var first = Procedure();
        Assert.Equal(1, (await service.UpsertProcedureAsync(first, 0, "save-1")).Revision);
        var replay = await service.UpsertProcedureAsync(first with { UpdatedAtUtc = Now.AddHours(1) }, 0, "save-1");
        Assert.Equal("replayed", replay.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpsertProcedureAsync(first with { Goal = "changed" }, 0, "save-1"));
        await service.UpsertProcedureAsync(first with { Goal = "new goal" }, 1, "save-2");
        await service.RecordApplicationAsync(new("attempt", first.Key, 1, "linux", ProcedureApplicationOutcome.ReportedSuccess, "artifact verified by caller", Now), "apply");
        var state = await service.GetAsync();
        Assert.Equal(2, state.ProcedureHistory.Count);
        Assert.Equal(1, state.Applications["attempt"].ProcedureRevision);
    }

    /// <summary>Family resemblance does not collapse independent occurrences or multiply a repeated occurrence identity.</summary>
    [Fact]
    public async Task OccurrencesAndApplicationsHaveIndependentIdentity()
    {
        var service = Create();
        var occurrence = new KnowledgeOccurrence("incident-a", "timeout", "request timed out", "linux", ProblemOccurrenceStatus.Open, Now);
        await service.RecordOccurrenceAsync(occurrence, "create-a");
        await service.RecordOccurrenceAsync(occurrence with { OccurredAtUtc = Now.AddMinutes(1) }, "same-event");
        await service.RecordOccurrenceAsync(occurrence with { Key = "incident-b" }, "create-b");
        Assert.Equal(2, (await service.GetAsync()).Occurrences.Count);
        await service.UpsertProcedureAsync(Procedure(), 0, "procedure");
        var application = new KnowledgeApplication("attempt-a", "logs", 1, "linux", ProcedureApplicationOutcome.Unknown, "", Now, "missing");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordApplicationAsync(application, "invalid-link"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordApplicationAsync(application with { Outcome = ProcedureApplicationOutcome.VerifiedSuccess }, "fake-verification"));
    }

    /// <summary>One self-reported task casts one versioned vote even after multiple exposures, independently of confidence.</summary>
    [Fact]
    public async Task FeedbackUsesPresentationAndVoteCas()
    {
        var service = Create();
        var a = await service.PresentAsync("record", 1, "task", "present-a");
        var b = await service.PresentAsync("record", 1, "task", "present-b");
        await service.FeedbackAsync(a.Receipt, KnowledgeFeedbackKind.Helpful, 0, "vote-a");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FeedbackAsync(b.Receipt, KnowledgeFeedbackKind.Harmful, 0, "racing-vote"));
        await service.FeedbackAsync(b.Receipt, KnowledgeFeedbackKind.Harmful, 1, "vote-b");
        var utility = KnowledgeService.GetUtility(await service.GetAsync(), "record", 1);
        Assert.Equal(2, utility.Presented);
        Assert.Equal(1, utility.Harmful);
        Assert.Equal(0, utility.Helpful);
        Assert.Equal(1, utility.DistinctTasks);
        Assert.Null(KnowledgeService.GetUtility(await service.GetAsync(), "record", 2).Utility);
    }

    /// <summary>Metadata revisions serialize same-canonical-revision edits; callers cannot grant foundation authority.</summary>
    [Fact]
    public async Task MetadataHasIndependentCasAndProtectedFoundation()
    {
        var service = Create();
        var metadata = new KnowledgeMetadata("record", 9, KnowledgeClass.Knowledge, FoundationStatus.None, "linux", null);
        await service.SetMetadataAsync(metadata, 0, "metadata-a");
        await service.SetMetadataAsync(metadata with { Class = KnowledgeClass.Experience }, 1, "metadata-b");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetMetadataAsync(metadata, 1, "stale-metadata"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetMetadataAsync(metadata with { Foundation = FoundationStatus.Approved }, 2, "foundation"));
        Assert.Equal(2, (await service.GetAsync()).Metadata["record"].MetadataRevision);
    }

    /// <summary>Shared working-set CAS prevents silent loss, while expiry remains bounded to the configured pilot interval.</summary>
    [Fact]
    public async Task WorkingSetUsesCasAndBoundedTtl()
    {
        var service = Create();
        var working = new KnowledgeWorkingSet(0, "goal", ["fact"], [], [], "next", Now.AddHours(1));
        await service.SetWorkingSetAsync(working, 0, "working-a");
        Assert.Equal("replayed", (await service.SetWorkingSetAsync(working with { ExpiresAtUtc = Now.AddHours(2) }, 0, "working-a")).Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetWorkingSetAsync(working, 0, "working-b"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetWorkingSetAsync(working with { ExpiresAtUtc = Now.AddDays(2) }, 1, "working-ttl"));
    }

    /// <summary>Application reporting and usefulness occupy independent dimensions, and UTC windows use the observation clock.</summary>
    [Fact]
    public async Task AppliedSignalSurvivesUtilityVoteAndCountsWindows()
    {
        var clock = Now;
        var service = new KnowledgeService(new MemoryStore(), "authority", () => clock);
        var presentation = await service.PresentAsync("record", 1, "task", "present");
        await service.FeedbackAsync(presentation.Receipt, KnowledgeFeedbackKind.Applied, 0, "applied");
        await service.FeedbackAsync(presentation.Receipt, KnowledgeFeedbackKind.Helpful, 0, "helpful");
        var state = await service.GetAsync();
        var current = KnowledgeService.GetUtility(state, "record", 1, Now);
        Assert.Equal(1, current.Applied);
        Assert.Equal(1, current.Helpful);
        Assert.Equal(Now, current.LastAppliedAtUtc);
        Assert.Equal(1, current.Applied1Day);
        Assert.Equal(1, current.Presented1Day);
        var later = KnowledgeService.GetUtility(state, "record", 1, Now.AddDays(8));
        Assert.Equal(0, later.Applied7Days);
        Assert.Equal(1, later.Applied30Days);
        Assert.Equal(0, later.Presented7Days);
        Assert.Equal(1, later.Presented30Days);
        Assert.Equal(0, KnowledgeService.GetUtility(state, "record", 1, Now.AddDays(31)).Applied30Days);
    }

    /// <summary>Status CAS changes a reported incident without incrementing occurrence counts or asserting a verified fix.</summary>
    [Fact]
    public async Task OccurrenceStatusTransitionsUseIndependentRevision()
    {
        var service = Create();
        await service.RecordOccurrenceAsync(new("incident", "timeout", "timeout", "linux", ProblemOccurrenceStatus.Open, Now), "create");
        await service.UpdateOccurrenceAsync("incident", ProblemOccurrenceStatus.Mitigated, 1, "mitigate");
        Assert.Equal("replayed", (await service.UpdateOccurrenceAsync("incident", ProblemOccurrenceStatus.Mitigated, 1, "mitigate")).Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateOccurrenceAsync("incident", ProblemOccurrenceStatus.Resolved, 1, "stale"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateOccurrenceAsync("incident", ProblemOccurrenceStatus.Open, 2, "invalid"));
        await service.UpdateOccurrenceAsync("incident", ProblemOccurrenceStatus.Closed, 2, "close");
        await service.UpdateOccurrenceAsync("incident", ProblemOccurrenceStatus.Diagnosing, 3, "reopen");
        var state = await service.GetAsync();
        Assert.Single(state.Occurrences);
        Assert.Equal(4, state.Occurrences["incident"].Revision);
        Assert.Equal(ProblemOccurrenceStatus.Diagnosing, state.Occurrences["incident"].Status);
    }

    private static KnowledgeService Create() => new(new MemoryStore(), "authority", () => Now);
    private static KnowledgeProcedure Procedure() => new("logs", 0, "download logs", ["скачать логи"], "linux", "read permission", "export bounded interval", "check artifact", null, Now);

    private sealed class MemoryStore : IKnowledgeStore
    {
        private KnowledgeState _state = new();
        public Task<KnowledgeState> ReadAsync(string profileKey, CancellationToken cancellationToken) => Task.FromResult(_state);
        public Task<bool> CompareExchangeAsync(string profileKey, long expectedVersion, KnowledgeState replacement, CancellationToken cancellationToken)
        {
            if (_state.Version != expectedVersion) return Task.FromResult(false);
            _state = replacement;
            return Task.FromResult(true);
        }
    }
}
