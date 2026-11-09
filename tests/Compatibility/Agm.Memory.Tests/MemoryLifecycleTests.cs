using Agm.Memory.Abstractions;
using Xunit;

namespace Agm.Memory.Tests;

public sealed class MemoryLifecycleTests
{
    private static readonly MemoryScope Scope = new(Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("20000000-0000-0000-0000-000000000001"));

    [Fact]
    public async Task Lifecycle_RejectsStaleVersionAndCrossTenantWithoutMutation()
    {
        var service = new MemoryIngestionService(TimeProvider.System);
        var memory = await CreateAsync(service, Scope, "Use bounded retries");
        var stale = await service.ApplyAsync(Request(
            Scope, memory.Id, MemoryLifecycleAction.Invalidate, 2), default);
        var otherScope = Scope with { TenantId = Guid.NewGuid() };
        var crossTenant = await service.ApplyAsync(Request(
            otherScope, memory.Id, MemoryLifecycleAction.Invalidate, 1), default);
        var crossProject = await service.ApplyAsync(Request(
            Scope with { ProjectId = Guid.NewGuid() }, memory.Id,
            MemoryLifecycleAction.Invalidate, 1), default);

        Assert.Equal(MemoryLifecycleOutcome.StaleVersion, stale.Outcome);
        Assert.Equal(MemoryLifecycleOutcome.NotFound, crossTenant.Outcome);
        Assert.Equal(MemoryLifecycleOutcome.NotFound, crossProject.Outcome);
        Assert.Equal(CanonicalMemoryStatus.Active,
            (await service.GetAsync(Scope, memory.Id, default))!.Status);
    }

    [Fact]
    public async Task SupersedeChain_LeavesOnlyFinalActiveInCurrentViewAndKeepsHistory()
    {
        var service = new MemoryIngestionService(TimeProvider.System);
        var first = await CreateAsync(service, Scope, "Retry twice");
        var second = await CreateAsync(service, Scope, "Retry three times");
        var third = await CreateAsync(service, Scope, "Retry with exponential backoff");

        var firstResult = await service.ApplyAsync(Request(
            Scope, first.Id, MemoryLifecycleAction.Supersede, 1, second.Id), default);
        var secondResult = await service.ApplyAsync(Request(
            Scope, second.Id, MemoryLifecycleAction.Supersede, 1, third.Id), default);
        var current = await service.ListCurrentAsync(Scope, default);
        var all = await service.ListAsync(Scope, default);

        Assert.Equal(MemoryLifecycleOutcome.Applied, firstResult.Outcome);
        Assert.Equal(MemoryRelationType.Supersedes, firstResult.Relation!.Type);
        Assert.Equal(MemoryLifecycleOutcome.Applied, secondResult.Outcome);
        Assert.Equal([third.Id], current.Select(item => item.Id));
        Assert.Equal(2, all.Count(item => item.Status == CanonicalMemoryStatus.Superseded));
        Assert.Equal(second.Id, all.Single(item => item.Id == first.Id).SupersededById);
        Assert.Equal(third.Id, all.Single(item => item.Id == second.Id).SupersededById);
    }

    [Fact]
    public async Task Conflict_PreservesBothMemoriesUntilResolutionAndUndoRestoresConflict()
    {
        var service = new MemoryIngestionService(TimeProvider.System);
        var loser = await CreateAsync(service, Scope, "Deploy on Friday");
        var winner = await CreateAsync(service, Scope, "Never deploy on Friday");

        var conflict = await service.ApplyAsync(Request(
            Scope, loser.Id, MemoryLifecycleAction.Contradict, 1, winner.Id), default);
        Assert.Equal(2, (await service.ListCurrentAsync(Scope, default)).Count);
        Assert.Equal(MemoryRelationType.Contradicts, conflict.Relation!.Type);

        var resolved = await service.ApplyAsync(Request(
            Scope, loser.Id, MemoryLifecycleAction.ResolveConflict, 2, winner.Id,
            reason: "policy owner selected the newer rule"), default);
        var resolvedRelations = await service.GetRelationsAsync(Scope, loser.Id, default);
        Assert.Equal([winner.Id], (await service.ListCurrentAsync(Scope, default)).Select(item => item.Id));
        Assert.Equal(CanonicalMemoryStatus.Superseded, resolved.Memory!.Status);
        Assert.Contains(resolvedRelations, item => item.Type == MemoryRelationType.Contradicts && !item.IsActive);
        Assert.Contains(resolvedRelations, item => item.Type == MemoryRelationType.Supersedes && item.IsActive);

        var undone = await service.ApplyAsync(Request(
            Scope, loser.Id, MemoryLifecycleAction.UndoSupersede, 3,
            reason: "resolution was based on incomplete evidence"), default);
        var restoredRelations = await service.GetRelationsAsync(Scope, loser.Id, default);
        Assert.Equal(CanonicalMemoryStatus.Active, undone.Memory!.Status);
        Assert.Equal(2, (await service.ListCurrentAsync(Scope, default)).Count);
        Assert.Contains(restoredRelations, item => item.Type == MemoryRelationType.Contradicts && item.IsActive);
        Assert.Contains(restoredRelations, item => item.Type == MemoryRelationType.Supersedes && !item.IsActive);
    }

    [Fact]
    public async Task ConfirmInvalidateAndUndo_AreVersionedRelatedAndAudited()
    {
        var service = new MemoryIngestionService(TimeProvider.System);
        var draft = await CreateAsync(service, Scope, "Tentative retention rule", confidence: .4);
        var evidence = await CreateAsync(service, Scope, "Retention owner approved rule");
        var confirmCorrelation = Guid.NewGuid();

        var confirmed = await service.ApplyAsync(new(
            Scope, draft.Id, MemoryLifecycleAction.Confirm, 1, "reviewer",
            "validated against policy", confirmCorrelation, evidence.Id), default);
        var invalidated = await service.ApplyAsync(Request(
            Scope, draft.Id, MemoryLifecycleAction.Invalidate, 2,
            reason: "policy was withdrawn"), default);
        var history = await service.GetHistoryAsync(Scope, draft.Id, default);
        var relations = await service.GetRelationsAsync(Scope, draft.Id, default);
        var audit = await service.GetAuditAsync(Scope, default);

        Assert.Equal(CanonicalMemoryStatus.Active, confirmed.Memory!.Status);
        Assert.Equal(MemoryRelationType.ConfirmedBy, confirmed.Relation!.Type);
        Assert.Equal(CanonicalMemoryStatus.Invalid, invalidated.Memory!.Status);
        Assert.Equal([1L, 2L, 3L], history.Select(item => item.Version));
        Assert.Contains(relations, item => item.Type == MemoryRelationType.ConfirmedBy && item.IsActive);
        Assert.Contains(audit, item => item.MemoryId == draft.Id && item.Action == "confirm" &&
            item.Actor == "reviewer" && item.Reason == "validated against policy" &&
            item.CorrelationId == confirmCorrelation);
        Assert.Contains(audit, item => item.MemoryId == draft.Id && item.Action == "invalidate");
    }

    [Fact]
    public async Task OrdinarySupersede_CanBeUndoneWithRelationHistoryPreserved()
    {
        var service = new MemoryIngestionService(TimeProvider.System);
        var oldMemory = await CreateAsync(service, Scope, "Use API v1");
        var replacement = await CreateAsync(service, Scope, "Use API v2");
        await service.ApplyAsync(Request(
            Scope, oldMemory.Id, MemoryLifecycleAction.Supersede, 1, replacement.Id), default);

        var undone = await service.ApplyAsync(Request(
            Scope, oldMemory.Id, MemoryLifecycleAction.UndoSupersede, 2), default);
        var relation = Assert.Single(await service.GetRelationsAsync(Scope, oldMemory.Id, default));

        Assert.Equal(MemoryLifecycleOutcome.Applied, undone.Outcome);
        Assert.Equal(CanonicalMemoryStatus.Active, undone.Memory!.Status);
        Assert.False(relation.IsActive);
        Assert.NotNull(relation.ReversedAt);
        Assert.NotNull(relation.ReversedByCorrelationId);
    }

    private static MemoryLifecycleRequest Request(
        MemoryScope scope,
        Guid memoryId,
        MemoryLifecycleAction action,
        long expectedVersion,
        Guid? relatedMemoryId = null,
        string reason = "lifecycle test") =>
        new(scope, memoryId, action, expectedVersion, "test-actor", reason, Guid.NewGuid(), relatedMemoryId);

    private static async Task<CanonicalMemoryRecord> CreateAsync(
        MemoryIngestionService service,
        MemoryScope scope,
        string statement,
        double confidence = .95)
    {
        var result = await service.IngestAsync(new(
            scope,
            new(MemorySourceType.Manual, $"test:{Guid.NewGuid():N}"),
            [new(statement)],
            MemoryIngestionMode.Manual,
            Guid.NewGuid().ToString("N"),
            "test-seed",
            Guid.NewGuid(),
            [new(CanonicalMemoryType.Decision, "deployment", statement, null,
                confidence, .5, [], [0])]), default);
        return Assert.Single(result.Items).Memory!;
    }
}
