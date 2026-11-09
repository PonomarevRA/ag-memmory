using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Task labels partition working summaries within the same configured authority; they do not authenticate callers.</summary>
public sealed class TaskWorkingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "agmemory-task-working-" + Guid.NewGuid().ToString("N"));
    private LocalAgMemoryMcpConfiguration Configuration => LocalAgMemoryMcpConfiguration.Create(_path, "task-actor", "task-tenant");

    /// <summary>Interleaved task writers have independent CAS fences and replay receipts; old shared writers retain their original state.</summary>
    [Fact]
    public async Task IndependentTasksAndLegacySharedWorkingSurviveRestart()
    {
        await using (var first = new LocalAgMemoryMcpRuntime(Configuration))
        await using (var second = new LocalAgMemoryMcpRuntime(Configuration))
        {
            Assert.Null((await first.KnowledgeWorkingAsync("Legacy goal", [], [], [], "Continue legacy", 0, "legacy_one")).Error);
            Assert.Null((await first.TaskWorkingAsync("MEM-27", "Retrieval goal", ["Alias rules are bounded"], [], [], "Run pilot", 0, "task_one")).Error);
            Assert.Null((await second.TaskWorkingAsync("MEM-28", "Capacity goal", [], [], [], "Measure load", 0, "task_one")).Error);
            Assert.Equal("replayed", (await first.TaskWorkingAsync("MEM-27", "Retrieval goal", ["Alias rules are bounded"], [], [], "Run pilot", 0, "task_one")).Data!.Outcome);
            Assert.Equal("revision_conflict", (await second.TaskWorkingAsync("MEM-27", "Stale write", [], [], [], "Retry", 0, "task_two")).Error!.Code);
            Assert.Null((await second.TaskWorkingAsync("MEM-27", "Updated retrieval", [], [], [], "Check rank", 1, "task_two")).Error);
            Assert.Null((await first.KnowledgeWorkingAsync("Updated legacy", [], [], [], "Legacy next", 1, "legacy_two")).Error);
        }
        await using var reopened = new LocalAgMemoryMcpRuntime(Configuration);
        var retrieval = (await reopened.TaskContextAsync("MEM-27")).Data!;
        var capacity = (await reopened.TaskContextAsync("MEM-28")).Data!;
        Assert.Equal("Updated retrieval", retrieval.WorkingSet!.Goal);
        Assert.Equal(2, retrieval.WorkingSetRevision);
        Assert.Equal("MEM-27", retrieval.TaskKey);
        Assert.Equal("Capacity goal", capacity.WorkingSet!.Goal);
        Assert.Equal(1, capacity.WorkingSetRevision);
        Assert.Equal("Updated legacy", (await reopened.WorkingContextAsync("")).Data!.WorkingSet!.Goal);
        Assert.Null((await reopened.TaskContextAsync("MEM-29")).Data!.WorkingSet);
        Assert.Null((await reopened.TaskContextAsync("MEM-27", "unmatched", mode: "search")).Data!.WorkingSet);
    }

    /// <summary>An expired task hides content but retains its revision, so an old writer cannot recreate revision one.</summary>
    [Fact]
    public async Task ExpiryRetainsFenceAndForeignScopeDoesNotReadTask()
    {
        var config = Configuration;
        var profile = KnowledgeProfileIdentity.GetTaskWorking(_path, config.Actor, config.Scope, null, "MEM-28");
        var service = new KnowledgeService(KnowledgeFileStore.ForTaskWorking(_path), profile,
            () => DateTimeOffset.UtcNow.AddDays(-2));
        await service.SetWorkingSetAsync(new(1, "Expired goal", [], [], [], "Old next", DateTimeOffset.UtcNow.AddDays(-1)), 0, "expired");
        await using var runtime = new LocalAgMemoryMcpRuntime(config);
        var expired = (await runtime.TaskContextAsync("MEM-28")).Data!;
        Assert.Null(expired.WorkingSet);
        Assert.Equal(1, expired.WorkingSetRevision);
        Assert.Equal("revision_conflict", (await runtime.TaskWorkingAsync("MEM-28", "Stale", [], [], [], "Next", 0, "stale")).Error!.Code);
        await using var foreign = new LocalAgMemoryMcpRuntime(config with { Scope = config.Scope with { WorkspaceId = new("foreign") } });
        Assert.Equal(0, (await foreign.TaskContextAsync("MEM-28")).Data!.WorkingSetRevision);
    }

    /// <summary>Invalid labels are rejected before key or state I/O, including a null label passed by a non-MCP caller.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("../other")]
    [InlineData("задача")]
    [InlineData(null)]
    public async Task InvalidTaskKeyCannotSelectLegacyOrCreateStorage(string? taskKey)
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration);
        Assert.Equal("invalid_argument", (await runtime.TaskContextAsync(taskKey!)).Error!.Code);
        Assert.False(Directory.Exists(_path));
    }

    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }
}
