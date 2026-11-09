using System.Text.Json;
using AgMemory.ActivityJournal;
using AgMemory.McpServer;
using ModelContextProtocol.Client;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Independent contract tests use isolated directories and never access the user's working memory or journal.</summary>
public sealed class AgentActivityJournalTests
{
    private static readonly string Partition = new('a', 64);

    [Fact]
    public void PortableFixturesRoundTripWithDecimalSequencesAndNoAuthority()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "activity-contract", "examples.json");
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var value in fixture.RootElement.GetProperty("events").EnumerateArray())
        {
            var parsed = value.Deserialize<ActivityEvent>(ActivityJson.Options)!;
            using var serialized = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(parsed, ActivityJson.Options));
            Assert.Equal(value.GetProperty("sequence").GetString(), serialized.RootElement.GetProperty("sequence").GetString());
            Assert.Equal("agent-activity/v1", parsed.SchemaVersion);
            Assert.Equal(value.GetProperty("operation").GetString(), parsed.Operation);
            Assert.Equal(JsonValueKind.String, serialized.RootElement.GetProperty("sequence").ValueKind);
            Assert.DoesNotContain("actor", serialized.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CyclicQuotaRetainsRecentEventsAndSequenceAcrossRestartWithoutAgeRetention()
    {
        var path = TemporaryPath();
        try
        {
            var options = new ActivityJournalOptions { MaxSizeBytes = 65_536 };
            string epoch;
            using (var store = new SegmentJournal(path, options))
            {
                for (var batch = 0; batch < 80; batch++)
                {
                    store.Append(Enumerable.Range(0, 8).Select(_ => Item()).ToArray());
                    Assert.InRange(store.Status().UsedBytes, 1, options.MaxSizeBytes);
                }
                var read = store.Read(Partition, 100);
                Assert.NotEmpty(read.Events); Assert.True(ActivityJson.Sequence(read.Events[0].Event.Sequence) > 1);
                Assert.Equal("640", read.Status.LatestSequence); epoch = read.Status.JournalEpoch;
                Assert.All(read.Events, value => Assert.Equal(2001, value.Event.OccurredAt.Year));
            }
            using var reopened = new SegmentJournal(path, options);
            reopened.Append([Item()]);
            Assert.Equal("641", reopened.Status().LatestSequence); Assert.Equal(epoch, reopened.Status().JournalEpoch);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void RecoversUncommittedTailAndClearALeavesPartitionBAndItsCursorIntact()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new SegmentJournal(path, new());
            store.Append([Item(), Item(new('b', 64))]);
            var beforeA = store.Read(Partition).Status;
            var beforeB = store.Read(new('b', 64)).Status;
            var segment = Directory.GetFiles(path, "*.jsonl").Single();
            File.AppendAllText(segment, "{crash");
            Assert.Equal("2", store.Read(Partition).Status.LatestSequence);
            Assert.DoesNotContain("{crash", File.ReadAllText(segment), StringComparison.Ordinal);
            var cleared = store.ClearPartition(Partition);
            Assert.NotEqual(beforeA.JournalEpoch, cleared.JournalEpoch);
            Assert.Empty(store.Read(Partition).Events);
            Assert.Single(store.Read(new('b', 64)).Events);
            Assert.Equal(beforeB.JournalEpoch, store.Read(new('b', 64)).Status.JournalEpoch);
            store.Append([Item()]);
            Assert.Single(store.Read(Partition).Events);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void MissingCommittedActiveOrMiddleSegmentFailsClosedWithoutZeroFilledRecreation()
    {
        foreach (var active in new[] { true, false })
        {
            var path = TemporaryPath();
            try
            {
                using var store = new SegmentJournal(path, new() { MaxSizeBytes = 262_144 });
                for (var batch = 0; batch < 45; batch++) store.Append(Enumerable.Range(0, 8).Select(_ => Item()).ToArray());
                var segments = Directory.GetFiles(path, "*.jsonl").Order(StringComparer.Ordinal).ToArray();
                Assert.True(segments.Length >= 3);
                var removed = active ? segments[^1] : segments[1]; File.Delete(removed);
                Assert.Throws<IOException>(() => store.Status());
                Assert.Throws<IOException>(() => store.Append([Item()])); Assert.False(File.Exists(removed));
            }
            finally { Delete(path); }
        }
    }

    [Fact]
    public void InvalidBatchBudgetIsRejectedAndServiceSettingsControlQuotaAndPreview()
    {
        Assert.Throws<ArgumentException>(() => new ActivityJournalOptions { BatchBytes = 1024 }.Validate());
        var path = TemporaryPath();
        try
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "appsettings.local.json"), "{\"ActivityJournal\":{\"MaxSizeBytes\":1000000,\"CaptureQueryPreview\":false}}");
            var settings = ActivityJournalOptions.Load(path); Assert.Equal(1_000_000, settings.MaxSizeBytes); Assert.False(settings.CaptureQueryPreview);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void OldIdleInstanceCannotReactivateItsQuotaAfterNewServiceStartup()
    {
        var path = TemporaryPath();
        try
        {
            using var oldIdle = new SegmentJournal(path, new() { MaxSizeBytes = 200_000 });
            using var restarted = new SegmentJournal(path, new() { MaxSizeBytes = 100_000 });
            Assert.Throws<InvalidOperationException>(() => oldIdle.Append([Item()]));
            Assert.Equal(100_000, restarted.Status().MaxSizeBytes);
            restarted.Append([Item()]); Assert.Single(restarted.Read(Partition).Events);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task InaccessibleJournalStartupDoesNotBreakPrimaryMcpMemory()
    {
        var path = TemporaryPath(); Directory.CreateDirectory(path);
        try
        {
            var blocked = Path.Combine(path, "not-a-directory"); File.WriteAllText(blocked, "blocked");
            var configuration = LocalAgMemoryMcpConfiguration.Create(Path.Combine(path, "store"), "actor", "tenant") with { ActivityDataDirectory = blocked };
            await using var runtime = new LocalAgMemoryMcpRuntime(configuration);
            Assert.Equal("available", (await runtime.GetStatusAsync(default)).Status);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ConcurrentInstancesAssignUniqueSequencesAndDoNotInterleaveLines()
    {
        var path = TemporaryPath();
        try
        {
            using var first = new SegmentJournal(path, new()); using var second = new SegmentJournal(path, new());
            await Task.WhenAll(Task.Run(() => { for (var i = 0; i < 30; i++) first.Append([Item()]); }),
                Task.Run(() => { for (var i = 0; i < 30; i++) second.Append([Item()]); }));
            var read = first.Read(Partition, 100);
            Assert.Equal(60, read.Events.Count);
            Assert.Equal(60, read.Events.Select(value => value.Event.Sequence).Distinct().Count());
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task NonblockingObserverRetainsExceptionAndDrainsStartedFinishedWithSafeTargets()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new SegmentJournal(path, new());
            await using (var writer = new ActivityWriter(store, new()))
            {
                var observer = new ActivityObserver(writer, Partition, "cursor");
                Assert.Equal(7, await observer.ExecuteAsync("resolve", () => { observer.Target("private-record-id"); return Task.FromResult(7); }));
                var original = new ArgumentException("private-query-never-record");
                Assert.Same(original, await Assert.ThrowsAsync<ArgumentException>(() => observer.ExecuteAsync<int>("recall", () => Task.FromException<int>(original))));
            }
            var read = store.Read(Partition);
            Assert.Equal(4, read.Events.Count);
            Assert.NotNull(read.Events[1].TargetToken);
            Assert.Equal("failed", read.Events[^1].Event.Outcome);
            var disk = string.Join("", Directory.GetFiles(path).Select(File.ReadAllText));
            Assert.DoesNotContain("private-record-id", disk, StringComparison.Ordinal);
            Assert.DoesNotContain("private-query", disk, StringComparison.Ordinal);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task SearchPreviewIsBoundedRedactedAndCannotChangeTheBusinessResult()
    {
        var path = TemporaryPath();
        try
        {
            using var store = new SegmentJournal(path, new());
            await using (var writer = new ActivityWriter(store, new()))
            {
                var observer = new ActivityObserver(writer, Partition, "codex");
                Assert.Equal(11, await observer.ExecuteAsync("RecallAsync", () => Task.FromResult(11), requestQuery: "find coupon token=private-credential\npassword=hidden " + new string('x', 6000)));
            }
            var events = store.Read(Partition).Events;
            Assert.Equal(2, events.Count);
            Assert.All(events, item => { Assert.Equal("preview", item.Event.Query.Mode); Assert.InRange(item.Event.Query.Preview!.Length, 1, 256); });
            var text = JsonSerializer.Serialize(events, ActivityJson.Options);
            Assert.DoesNotContain("private-credential", text, StringComparison.Ordinal); Assert.DoesNotContain("password=hidden", text, StringComparison.Ordinal);
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task RealStdioMcpWritesJournalWithoutWebAndWithoutChangingToolSchemas()
    {
        var path = TemporaryPath();
        try
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "appsettings.local.json"), "{\"ActivityJournal\":{\"CaptureQueryPreview\":false}}");
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["AGMEMORY_STORAGE_PATH"] = Path.Combine(path, "store");
            environment["AGMEMORY_DATA_DIR"] = path;
            environment["AGMEMORY_ACTOR_ID"] = "private-test-actor"; environment["AGMEMORY_TENANT_ID"] = "private-test-tenant";
            environment["AGMEMORY_CLIENT_ID"] = "cursor";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using (var client = await McpClient.CreateAsync(new StdioClientTransport(new() { Name = "activity-journal", Command = "dotnet",
                Arguments = [typeof(LocalAgMemoryMcpRuntime).Assembly.Location], InheritEnvironmentVariables = false, EnvironmentVariables = environment }), cancellationToken: deadline.Token))
            {
                var result = await client.CallToolAsync("memory_status", cancellationToken: deadline.Token);
                Assert.NotEqual(true, result.IsError);
                await client.CallToolAsync("memory_recall", new Dictionary<string, object?> { ["query"] = "never-save-this-query" }, cancellationToken: deadline.Token);
                await Task.Delay(350, deadline.Token);
            }
            using var store = new SegmentJournal(Path.Combine(path, "agent-activity"), new());
            var config = LocalAgMemoryMcpConfiguration.Create(Path.Combine(path, "store"), "private-test-actor", "private-test-tenant");
            var read = store.Read(ActivityIdentity.Partition(config.Actor, config.Scope, config.StoragePath));
            Assert.Equal(4, read.Events.Count);
            Assert.All(read.Events, item => Assert.Equal("omitted", item.Event.Query.Mode));
            var disk = string.Join("", Directory.GetFiles(Path.Combine(path, "agent-activity")).Select(File.ReadAllText));
            Assert.DoesNotContain("never-save-this-query", disk, StringComparison.Ordinal);
            Assert.DoesNotContain("private-test-actor", disk, StringComparison.Ordinal);
        }
        finally { Delete(path); }
    }

    private static StoredActivity Item(string? partition = null) => new(partition ?? Partition, new() { SessionId = Guid.NewGuid().ToString("N"), OperationId = Guid.NewGuid().ToString("N"), Operation = "recall", OccurredAt = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero) });
    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), "agm-activity-" + Guid.NewGuid().ToString("N"));
    private static void Delete(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }
}
