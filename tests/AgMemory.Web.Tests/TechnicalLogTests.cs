using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AgMemory.Web.Features.TechnicalLogs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgMemory.Web.Tests;

/// <summary>Exercises the independent native technical sink and its read contract over isolated journals and real Kestrel.</summary>
public sealed class TechnicalLogTests
{
    [Fact]
    public async Task NativeLoggingCapturesRedactedExceptionsScopesAndExplicitNulls()
    {
        await using var fixture = await Fixture.StartAsync();
        var logger = fixture.Provider.CreateLogger("Tests.Component");
        using var activity = new Activity("technical-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        using (logger.BeginScope(new Dictionary<string, object?> { ["OperationId"] = "operation", ["SessionId"] = "session", ["TaskId"] = "task", ["ActorId"] = "private-actor" }))
        {
            try { ThrowFailure(); }
            catch (Exception error) { logger.LogError(new EventId(42, "DiagnosticFailure"), error, "Failed {Count}; password={Password}", 3, "private-password"); }
        }
        await fixture.FlushAsync();
        using var page = await fixture.GetAsync("/events");
        var item = page.RootElement.GetProperty("events").EnumerateArray().Single(value => value.GetProperty("category").GetString() == "Tests.Component");
        Assert.Equal("ERROR", item.GetProperty("level").GetString()); Assert.Equal("operation", item.GetProperty("operationId").GetString());
        Assert.Equal("session", item.GetProperty("sessionId").GetString()); Assert.Equal("task", item.GetProperty("taskId").GetString());
        Assert.Equal(activity.TraceId.ToString(), item.GetProperty("traceId").GetString());
        Assert.Contains(nameof(ThrowFailure), item.GetProperty("exception").GetProperty("stackTrace").GetString());
        var raw = item.GetRawText(); Assert.DoesNotContain("private-password", raw); Assert.DoesNotContain("private-actor", raw); Assert.DoesNotContain("secret-value", raw);
        Assert.Contains("redacted", raw); Assert.Equal(3, item.GetProperty("properties").GetProperty("Count").GetInt32());
        logger.LogInformation("Plain event"); await fixture.FlushAsync();
        using var after = await fixture.GetAsync("/events");
        var plain = after.RootElement.GetProperty("events").EnumerateArray().Single(value => value.GetProperty("message").GetString() == "Plain event");
        foreach (var key in new[] { "exception", "operationId", "sessionId", "taskId", "eventName" }) Assert.Equal(JsonValueKind.Null, plain.GetProperty(key).ValueKind);
    }

    private static void ThrowFailure() => throw new InvalidOperationException("token=secret-value");

    [Fact]
    public async Task ProtectedGatewayAndReadAccessDoNotCreateFeedback()
    {
        await using var fixture = await Fixture.StartAsync();
        var before = fixture.Provider.Status().LatestSequence;
        fixture.Provider.CreateLogger("AgMemory.Web.Gateway.Adapter").LogCritical("private-provider-key");
        fixture.Provider.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").LogInformation("GET /api/technical-logs/v1/events?secret=private");
        fixture.Provider.CreateLogger("AgMemory.Web.Features.TechnicalLogs.Writer").LogError("internal-failure");
        for (var count = 0; count < 12; count++) { using var status = await fixture.GetAsync("/status"); using var page = await fixture.GetAsync("/events"); }
        await Task.Delay(50);
        Assert.Equal(before, fixture.Provider.Status().LatestSequence);
    }

    [Fact]
    public async Task SourceApiHasStrictQueriesAuthorityAndExclusiveNoHoleBytePages()
    {
        await using var fixture = await Fixture.StartAsync();
        var initialCount = long.Parse(fixture.Provider.Status().LatestSequence);
        for (var number = 0; number < 50; number++) fixture.Provider.Journal!.Append([new() { EventId = number.ToString(), Message = new string('я', 2048), Category = "synthetic" }]);
        var status = fixture.Provider.Status(); var seen = new HashSet<string>(); var cursor = "0"; var truncated = false;
        while (true)
        {
            using var response = await fixture.Http.GetAsync(TechnicalLogEndpoint.Prefix + $"/events?journalEpoch={status.JournalEpoch}&afterSequence={cursor}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            var bytes = await response.Content.ReadAsByteArrayAsync(); Assert.True(bytes.Length <= 262144);
            using var page = JsonDocument.Parse(bytes); var items = page.RootElement.GetProperty("events").EnumerateArray().ToArray();
            Assert.Equal(items.Select(item => long.Parse(item.GetProperty("sequence").GetString()!)).Order(), items.Select(item => long.Parse(item.GetProperty("sequence").GetString()!)));
            foreach (var item in items) Assert.True(seen.Add(item.GetProperty("eventId").GetString()!));
            truncated |= page.RootElement.GetProperty("truncated").GetBoolean(); cursor = page.RootElement.GetProperty("nextCursor").GetProperty("sequence").GetString()!;
            if (!page.RootElement.GetProperty("hasMore").GetBoolean()) break;
        }
        Assert.True(truncated); Assert.Equal(50 + initialCount, seen.Count); Assert.Equal(status.LatestSequence, cursor);
        foreach (var query in new[] { "q=x", "operation=x", "projectId=x", "limit=01", "limit=1&%6cimit=2", "q=%80", "limit=%GG", "afterSequence=0", "journalEpoch=e", "journalEpoch=e&afterSequence=9223372036854775808" })
            await fixture.ErrorAsync("/events?" + query, HttpStatusCode.BadRequest, "invalid_query");
        await fixture.ErrorAsync("/status?limit=1", HttpStatusCode.BadRequest, "invalid_query");
        await fixture.ErrorAsync("/events?journalEpoch=obsolete&afterSequence=0", HttpStatusCode.Conflict, "cursor_expired");
        fixture.Http.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid"); await fixture.ErrorAsync("/events", HttpStatusCode.Forbidden, "forbidden");
        fixture.Http.DefaultRequestHeaders.Remove("Origin"); fixture.Http.DefaultRequestHeaders.Host = "attacker.invalid"; await fixture.ErrorAsync("/status", HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task QuotaRotationAndIdlePollingUseCommittedMetadataOnly()
    {
        await using var fixture = await Fixture.StartAsync(new() { MaxSizeBytes = 131072 });
        for (var number = 0; number < 100; number++) fixture.Provider.Journal!.Append([new() { EventId = number.ToString(), Message = new string('я', 500) }]);
        var status = fixture.Provider.Status(); Assert.True(status.UsedBytes <= status.MaxSizeBytes); Assert.True(long.Parse(status.OldestSequence) > 1);
        await fixture.ErrorAsync($"/events?journalEpoch={status.JournalEpoch}&afterSequence=0", HttpStatusCode.Conflict, "cursor_expired");
        var segment = Directory.GetFiles(Path.Combine(fixture.Root, "technical-logs"), "segment-*.jsonl")[0];
        using var held = new FileStream(segment, FileMode.Open, FileAccess.Read, FileShare.None);
        using var idle = await fixture.GetAsync($"/events?journalEpoch={status.JournalEpoch}&afterSequence={status.LatestSequence}");
        Assert.Empty(idle.RootElement.GetProperty("events").EnumerateArray());
        await fixture.ErrorAsync("/events", HttpStatusCode.ServiceUnavailable, "journal_unavailable");
    }

    [Fact]
    public async Task LoggingQueueNeverWaitsOnDiskAndReportsDrops()
    {
        await using var fixture = await Fixture.StartAsync(new() { BatchDelayMs = 1, QueueMaxBytes = 131072 });
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.Combine(fixture.Root, "technical-logs"))));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { using var mutex = new Mutex(false, "AgMemory.TechnicalLogs." + identity); mutex.WaitOne(); entered.Set(); release.Wait(); mutex.ReleaseMutex(); });
        Assert.True(entered.Wait(2000));
        try
        {
            var logger = fixture.Provider.CreateLogger("Tests.Queue");
            var producer = Task.Run(() => { for (var index = 0; index < 500; index++) logger.LogInformation("Count {Count}", index); });
            await producer.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally { release.Set(); await holder; }
        await fixture.FlushAsync();
        fixture.Provider.CreateLogger("Tests.Queue").LogInformation("After drop"); await fixture.FlushAsync();
        Assert.True(fixture.Provider.Status().Health.DroppedTotal > 0);
    }

    [Fact]
    public async Task LargeDiagnosticsFitTheEventBudgetAndBackwardTraversalRemainsExclusive()
    {
        await using var fixture = await Fixture.StartAsync();
        var logger = fixture.Provider.CreateLogger("Tests.Bounded");
        logger.LogError(new InvalidOperationException(new string('я', 7000)), "Huge {Diagnostic}", new string('я', 10000));
        await fixture.FlushAsync();
        var status = fixture.Provider.Status(); var seen = new HashSet<string>(); string suffix = "?limit=1";
        while (true)
        {
            using var page = await fixture.GetAsync("/events" + suffix);
            foreach (var item in page.RootElement.GetProperty("events").EnumerateArray())
            {
                Assert.True(seen.Add(item.GetProperty("eventId").GetString()!));
                Assert.True(Encoding.UTF8.GetByteCount(item.GetRawText()) <= 16384);
                Assert.True(item.GetProperty("message").GetString()!.Length <= 2048);
            }
            if (!page.RootElement.GetProperty("hasMore").GetBoolean()) { Assert.Equal("0", page.RootElement.GetProperty("nextCursor").GetProperty("sequence").GetString()); break; }
            suffix = $"?limit=1&journalEpoch={status.JournalEpoch}&beforeSequence={page.RootElement.GetProperty("nextCursor").GetProperty("sequence").GetString()}";
        }
        Assert.Equal(long.Parse(status.LatestSequence), seen.Count);
    }

    [Fact]
    public void SharedQuotaCannotBeReactivatedByAnOldIdleInstanceAndMissingSegmentsFailClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "agm-technical-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var old = new TechnicalLogJournal(root, new());
            using var current = new TechnicalLogJournal(root, new() { MaxSizeBytes = 42_000_000 });
            Assert.Throws<InvalidOperationException>(() => old.Append([new() { Message = "old" }]));
            current.Append([new() { Message = "current" }]); Assert.Equal(42_000_000, current.Status().MaxSizeBytes);
            File.Delete(Directory.GetFiles(root, "segment-*.jsonl").Single());
            Assert.Throws<IOException>(() => current.Read(new(100, null, null, null)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MalformedStateAndStorageFailureDoNotEscapeApplicationLogging()
    {
        await using var fixture = await Fixture.StartAsync(); var logger = fixture.Provider.CreateLogger("Tests.Failures");
        var droppedBefore = fixture.Provider.Status().Health.DroppedTotal;
        logger.Log(LogLevel.Error, default, "state", null, (_, _) => throw new InvalidOperationException("formatter"));
        Assert.Equal(droppedBefore + 1, fixture.Provider.Status().Health.DroppedTotal);
        var opaque = new ThrowingObject();
        var formatterCalled = false;
        logger.Log(LogLevel.Information, default, new Dictionary<string, object?> { ["opaque"] = opaque }, null, (_, _) => { formatterCalled = true; return opaque.ToString(); });
        logger.Log(LogLevel.Information, default, new Dictionary<string, object?> { ["ActorId"] = opaque }, null, (_, _) => { formatterCalled = true; return opaque.ToString(); });
        var tooMany = Enumerable.Range(0, 33).ToDictionary(index => "value" + index, index => (object?)index);
        logger.Log(LogLevel.Information, default, tooMany, null, (_, _) => { formatterCalled = true; return "uninspected values"; });
        Assert.False(formatterCalled);
        await fixture.FlushAsync();
        var active = Directory.GetFiles(Path.Combine(fixture.Root, "technical-logs"), "segment-*.jsonl").Single(); File.Delete(active);
        logger.LogError("business work continues"); await Task.Delay(80);
        await fixture.ErrorAsync("/events", HttpStatusCode.ServiceUnavailable, "journal_unavailable");
        await using var unavailable = await Fixture.StartAsync(new() { MaxSizeBytes = 0 });
        unavailable.Provider.CreateLogger("Tests").LogError("Still returns");
        await unavailable.ErrorAsync("/status", HttpStatusCode.ServiceUnavailable, "journal_unavailable");
    }

    [Fact]
    public async Task ProcessMetricsAreLocalCachedAndExplicitAboutDiskCoverage()
    {
        await using var fixture = await Fixture.StartAsync();
        File.WriteAllBytes(Path.Combine(fixture.Root, "outside-journal.bin"), new byte[100000]);
        var owned = Directory.GetFiles(Path.Combine(fixture.Root, "technical-logs")).Sum(path => new FileInfo(path).Length);
        var head = fixture.Provider.Status().LatestSequence;
        using var response = await fixture.Http.GetAsync(DiagnosticsEndpoint.Route); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var first = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("service-diagnostics/v1", first.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(Environment.ProcessId, first.RootElement.GetProperty("process").GetProperty("processId").GetInt32());
        Assert.True(first.RootElement.GetProperty("process").GetProperty("residentBytes").GetInt64() > 0);
        var peak = first.RootElement.GetProperty("process").GetProperty("peakResidentBytes");
        Assert.True(peak.ValueKind == JsonValueKind.Null || peak.GetInt64() > 0);
        if (peak.ValueKind == JsonValueKind.Null) Assert.Equal("degraded", first.RootElement.GetProperty("health").GetProperty("status").GetString());
        Assert.Equal(owned, first.RootElement.GetProperty("storage").GetProperty("journalBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("storage").GetProperty("dataBytes").ValueKind);
        Assert.Equal("journals-only", first.RootElement.GetProperty("storage").GetProperty("measurement").GetString());
        Assert.DoesNotContain(fixture.Root, first.RootElement.GetRawText());
        File.WriteAllBytes(Path.Combine(fixture.Root, "technical-logs", "metrics-test.bin"), new byte[321]);
        using var secondResponse = await fixture.Http.GetAsync(DiagnosticsEndpoint.Route);
        using var second = JsonDocument.Parse(await secondResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal(owned, second.RootElement.GetProperty("storage").GetProperty("journalBytes").GetInt64());
        Assert.True(second.RootElement.GetProperty("process").GetProperty("uptimeSeconds").GetDouble() >= first.RootElement.GetProperty("process").GetProperty("uptimeSeconds").GetDouble());
        await Task.Delay(50); Assert.Equal(head, fixture.Provider.Status().LatestSequence);
        fixture.Http.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
        using var denied = await fixture.Http.GetAsync(DiagnosticsEndpoint.Route); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task EqualLengthSequenceAndEpochCorruptionCannotBeReplayed()
    {
        await using var fixture = await Fixture.StartAsync();
        var path = Directory.GetFiles(Path.Combine(fixture.Root, "technical-logs"), "segment-*.jsonl").Single();
        var original = File.ReadAllText(path); var length = new FileInfo(path).Length;
        File.WriteAllText(path, original.Replace("\"sequence\":\"1\"", "\"sequence\":\"2\"", StringComparison.Ordinal));
        Assert.Equal(length, new FileInfo(path).Length);
        await fixture.ErrorAsync("/events", HttpStatusCode.ServiceUnavailable, "journal_unavailable");
        File.WriteAllText(path, original.Replace(fixture.Provider.Status().JournalEpoch, new string('f', 32), StringComparison.Ordinal));
        Assert.Equal(length, new FileInfo(path).Length);
        await fixture.ErrorAsync("/events", HttpStatusCode.ServiceUnavailable, "journal_unavailable");
        File.WriteAllText(path, original);
    }

    [Fact]
    public void OwnedDataMetricsAreBoundedCachedAndNeverOpenTheDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "agm-owned-metrics-" + Guid.NewGuid().ToString("N"));
        var graph = Path.Combine(root, "memory.lancedb"); Directory.CreateDirectory(Path.Combine(graph, "nested"));
        try
        {
            File.WriteAllBytes(Path.Combine(graph, "nested", "sample.lance"), new byte[321]);
            var reader = new LocalServiceDiagnostics(root, graph); var first = reader.Read();
            Assert.Equal(321, first.Storage.DataBytes); Assert.Equal("owned-data", first.Storage.Measurement);
            File.WriteAllBytes(Path.Combine(graph, "nested", "later.lance"), new byte[123]);
            Assert.Equal(321, reader.Read().Storage.DataBytes);
            Assert.DoesNotContain(root, JsonSerializer.Serialize(first));
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(graph, "outside-link"), Path.Combine(graph, "nested", "sample.lance"));
                var invalid = new LocalServiceDiagnostics(root, graph).Read();
                Assert.Null(invalid.Storage.DataBytes); Assert.Equal("data_measurement_failed", invalid.Health.ErrorCode);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OwnedJournalSymlinksFailClosedWithoutFollowingExternalFiles()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "agm-log-symlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "technical-logs"));
        var outside = Path.Combine(root, "outside.txt"); File.WriteAllText(outside, "preserve synthetic file");
        try
        {
            File.CreateSymbolicLink(Path.Combine(root, "technical-logs", "manifest.json"), outside);
            Assert.Throws<IOException>(() => new TechnicalLogJournal(Path.Combine(root, "technical-logs"), new()));
            var metrics = new LocalServiceDiagnostics(root).Read(); Assert.Null(metrics.Storage.JournalBytes);
            Assert.Equal("preserve synthetic file", File.ReadAllText(outside));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MetricsBoundedWalkReturnsUnknownInsteadOfAFalseZero()
    {
        var root = Path.Combine(Path.GetTempPath(), "agm-metrics-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "technical-logs"));
        try
        {
            for (var index = 0; index < 4097; index++) File.WriteAllBytes(Path.Combine(root, "technical-logs", index.ToString()), []);
            var metrics = new LocalServiceDiagnostics(root).Read();
            Assert.Null(metrics.Storage.JournalBytes); Assert.Equal("degraded", metrics.Health.Status); Assert.Equal("journal_measurement_failed", metrics.Health.ErrorCode);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ThrowingObject { public override string ToString() => throw new InvalidOperationException("must never stringify state"); }

    /// <summary>Owns only temporary diagnostics and an ephemeral loopback host; no working memory database is opened.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "agm-technical-" + Guid.NewGuid().ToString("N"));
        public TechnicalLogProvider Provider { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        private WebApplication _app = null!;
        public static async Task<Fixture> StartAsync(TechnicalLogOptions? options = null)
        {
            var fixture = new Fixture(); Directory.CreateDirectory(fixture.Root);
            fixture.Provider = new(fixture.Root, options ?? new() { BatchDelayMs = 10 });
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development }); builder.Logging.ClearProviders();
            builder.Logging.AddProvider(fixture.Provider); builder.Services.AddSingleton(fixture.Provider); builder.Services.AddSingleton(new LocalServiceDiagnostics(fixture.Root)); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            fixture._app = builder.Build(); fixture._app.UseTechnicalLogs(); await fixture._app.StartAsync();
            fixture.Http = new() { BaseAddress = new(fixture._app.Urls.Single()) }; await fixture.FlushAsync();
            return fixture;
        }
        public async Task FlushAsync()
        {
            // Wait for the bounded batch deadline and publication, then require a stable durable head.
            if (Provider.Journal is null) return;
            string? previous = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                await Task.Delay(30);
                var current = Provider.Journal?.Status().LatestSequence;
                if (previous is not null && current == previous) return;
                previous = current;
            }
            throw new TimeoutException("Test journal did not settle.");
        }
        public async Task<JsonDocument> GetAsync(string suffix)
        {
            using var response = await Http.GetAsync(TechnicalLogEndpoint.Prefix + suffix); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        }
        public async Task ErrorAsync(string suffix, HttpStatusCode code, string expected)
        {
            using var response = await Http.GetAsync(TechnicalLogEndpoint.Prefix + suffix); Assert.Equal(code, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(expected, json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(code == HttpStatusCode.Conflict ? JsonValueKind.Object : JsonValueKind.Null, json.RootElement.GetProperty("resetCursor").ValueKind);
            Assert.DoesNotContain(Root, json.RootElement.GetRawText());
        }
        public async ValueTask DisposeAsync()
        { Http.Dispose(); await _app.StopAsync(); await _app.DisposeAsync(); await Provider.DisposeAsync(); Directory.Delete(Root, true); }
    }
}
