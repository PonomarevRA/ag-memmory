using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Real SQLite migration and replay tests use disposable roots; no installed memory is opened.</summary>
public sealed class KnowledgeJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "agmemory-knowledge-journal-" + Guid.NewGuid().ToString("N"));
    private const string Profile = "isolated-journal-profile";

    /// <summary>More than4096 mutations survive restart; late identical and conflicting requests preserve exactly-once history.</summary>
    [Fact]
    public async Task MigratedLedgerRetainsLateReplayBeyondLegacyLimit()
    {
        var legacy = new KnowledgeService(new KnowledgeFileStore(_path), Profile);
        await legacy.RecordOccurrenceAsync(new("event", "family", "Fixture symptom", "test", ProblemOccurrenceStatus.Open, DateTimeOffset.UtcNow), "original_event");
        var journal = new KnowledgeJournalStore(_path);
        await journal.MigrateAsync(Profile);
        var service = new KnowledgeService(journal, Profile);
        for (var revision = 0; revision < 4100; revision++)
        {
            var result = await service.SetWorkingSetAsync(new(revision + 1, "Goal " + revision, [], [], [], "Continue", DateTimeOffset.UtcNow.AddHours(1)), revision, "working_" + revision);
            Assert.Equal(revision + 1, result.Revision);
        }
        var reopenedStore = new KnowledgeJournalStore(_path);
        var reopened = new KnowledgeService(reopenedStore, Profile);
        var replay = await reopened.SetWorkingSetAsync(new(1, "Goal 0", [], [], [], "Continue", DateTimeOffset.UtcNow.AddHours(1)), 0, "working_0");
        Assert.Equal("replayed", replay.Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SetWorkingSetAsync(new(1, "Changed goal", [], [], [], "Continue", DateTimeOffset.UtcNow.AddHours(1)), 0, "working_0"));
        var state = await reopened.GetAsync();
        Assert.Equal(4100, state.WorkingSet!.Revision);
        Assert.Single(state.Occurrences);
        Assert.Empty(state.Requests);
        Assert.Equal(4101, await reopenedStore.CountRequestsAsync(Profile, default));
        Assert.NotNull(await reopenedStore.ReadRequestAsync(Profile, "original_event", default));
        // An old process reading the same profile must fail, rather than erase the journal or reapply a late key.
        await Assert.ThrowsAsync<InvalidDataException>(() => legacy.GetAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RollbackBeforeWritesAsync(Profile));
    }

    /// <summary>Exact legacy bytes and receipts can be restored before the first new write; repeat migration preserves subsequent legacy work.</summary>
    [Fact]
    public async Task RollbackAndRepeatMigrationPreserveLegacyGeneration()
    {
        var legacyStore = new KnowledgeFileStore(_path);
        var legacy = new KnowledgeService(legacyStore, Profile);
        await legacy.SetWorkingSetAsync(new(1, "Legacy goal", [], [], [], "Continue", DateTimeOffset.UtcNow.AddHours(1)), 0, "first");
        var before = await legacy.GetAsync();
        var journal = new KnowledgeJournalStore(_path);
        await journal.MigrateAsync(Profile);
        await journal.MigrateAsync(Profile);
        Assert.Equal(1, await journal.CountRequestsAsync(Profile, default));
        await journal.RollbackBeforeWritesAsync(Profile);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(await legacy.GetAsync()));
        await legacy.SetWorkingSetAsync(new(2, "Updated legacy", [], [], [], "Continue", DateTimeOffset.UtcNow.AddHours(1)), 1, "second");
        await journal.MigrateAsync(Profile);
        await journal.RollbackBeforeWritesAsync(Profile);
        Assert.Equal(2, (await legacy.GetAsync()).WorkingSet!.Revision);
        Assert.Equal(2, (await legacy.GetAsync()).Requests.Count);
    }

    /// <summary>Separate instances serialize CAS and duplicate receipts through the existing cross-process namespace lease.</summary>
    [Fact]
    public async Task ConcurrentWritersAndCancelledMutationDoNotLoseOrDuplicateState()
    {
        var journal = new KnowledgeJournalStore(_path);
        await journal.MigrateAsync(Profile);
        var first = new KnowledgeService(journal, Profile);
        var second = new KnowledgeService(new KnowledgeJournalStore(_path), Profile);
        var when = DateTimeOffset.UtcNow;
        var attempts = await Task.WhenAll(first.RecordOccurrenceAsync(new("one", "family", "One symptom", "test", ProblemOccurrenceStatus.Open, when), "write_one"),
            second.RecordOccurrenceAsync(new("two", "family", "Two symptom", "test", ProblemOccurrenceStatus.Open, when), "write_two"));
        Assert.Equal(2, attempts.Length);
        Assert.Equal(2, (await first.GetAsync()).Occurrences.Count);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.RecordOccurrenceAsync(new("cancelled", "family", "Cancelled symptom", "test", ProblemOccurrenceStatus.Open, when), "cancelled", cancelled.Token));
        Assert.Equal(2, await journal.CountRequestsAsync(Profile, default));
    }

    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }

    /// <summary>Legacy documents with ambiguous receipt lookup keys fail migration before publishing a fence or renaming history.</summary>
    [Fact]
    public async Task MismatchedLegacyReceiptKeyCannotBeSilentlyRenamed()
    {
        var legacy = new KnowledgeFileStore(_path);
        var state = new KnowledgeState { Version = 1 };
        state.Requests.Add("original", new("different", "fingerprint", "fixture", "result", 1, DateTimeOffset.UtcNow));
        Assert.True(await legacy.CompareExchangeAsync(Profile, 0, state, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => new KnowledgeJournalStore(_path).MigrateAsync(Profile));
        Assert.Contains("original", (await legacy.ReadAsync(Profile, default)).Requests.Keys);
        Assert.DoesNotContain("different", (await legacy.ReadAsync(Profile, default)).Requests.Keys);
    }

    /// <summary>Namespace headroom uses every profile; profile counts remain separately available for diagnostics.</summary>
    [Fact]
    public async Task NamespaceCapacityIncludesOtherMigratedProfiles()
    {
        var store = new KnowledgeJournalStore(_path);
        foreach (var profile in new[] { Profile, "other-profile" })
        {
            await store.MigrateAsync(profile);
            var service = new KnowledgeService(store, profile);
            await service.SetWorkingSetAsync(new(1, "Goal", [], [], [], "Continue", DateTimeOffset.UtcNow.AddHours(1)), 0, "request");
        }
        Assert.Equal(1, await store.CountRequestsAsync(Profile, default));
        Assert.Equal(2, await store.CountNamespaceRequestsAsync(Profile, default));
    }

    /// <summary>Import obeys the same global quota as mutation; a rejected import keeps both ledger and authoritative legacy state unchanged.</summary>
    [Fact]
    public async Task MigrationCannotExceedNamespaceQuotaOrPublishPartialFence()
    {
        var journal = new KnowledgeJournalStore(_path, false, null, maximumRequests: 3);
        await journal.MigrateAsync("other");
        var other = new KnowledgeService(journal, "other");
        for (var revision = 0; revision < 2; revision++)
            await other.SetWorkingSetAsync(new(revision + 1, "Other", [], [], [], "Next", DateTimeOffset.UtcNow.AddHours(1)), revision, "other_" + revision);
        var legacy = new KnowledgeService(new KnowledgeFileStore(_path), Profile);
        for (var revision = 0; revision < 2; revision++)
            await legacy.SetWorkingSetAsync(new(revision + 1, "Legacy", [], [], [], "Next", DateTimeOffset.UtcNow.AddHours(1)), revision, "legacy_" + revision);
        var before = System.Text.Json.JsonSerializer.Serialize(await legacy.GetAsync());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => journal.MigrateAsync(Profile));
        Assert.Equal("knowledge_capacity_exceeded", error.Message);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(await legacy.GetAsync()));
        Assert.Equal(2, await journal.CountNamespaceRequestsAsync("other", default));
        Assert.Equal(2, (await other.GetAsync()).WorkingSet!.Revision);
    }
}
