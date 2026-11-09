using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgMemory.ActivityJournal;
using AgMemory.Contracts;
using AgMemory.Web.Features.AgentActivity;
using AgMemory.Web.Features.MemoryGraph;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgMemory.Web.Tests;

/// <summary>Checks the portable read-only HTTP contract over isolated real loopback servers.</summary>
public sealed class AgentActivityHttpEndpointTests
{
    [Fact]
    public async Task StatusAndStrictPortableQueriesWorkWithoutBrowserOrigin()
    {
        await using var fixture = await Fixture.StartAsync();
        using var status = await fixture.GetAsync("/status");
        Assert.Equal("agent-activity-http/v1", status.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("agmemory", status.RootElement.GetProperty("application").GetString());
        Assert.Equal("0", status.RootElement.GetProperty("latestSequence").GetString());
        Assert.Equal("1", status.RootElement.GetProperty("oldestSequence").GetString());
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("health").GetProperty("queueItems").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("health").GetProperty("queueBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("health").GetProperty("errorCode").ValueKind);
        Assert.Equal(100, status.RootElement.GetProperty("limits").GetProperty("maxPageEvents").GetInt32());
        Assert.Equal(262_144, status.RootElement.GetProperty("limits").GetProperty("maxPageBytes").GetInt32());
        string[] invalid = ["limit=0", "limit=101", "limit=01", "limit=+2", "limit=2&limit=3", "operation=a&operation=b",
            "afterSequence=0", "journalEpoch=epoch", "afterSequence=0&beforeSequence=1&journalEpoch=epoch",
            "afterSequence=9223372036854775808&journalEpoch=epoch", "afterSequence=00&journalEpoch=epoch", "actorId=private-secret",
            "q=%ED%A0%80", "q=%FF", "q=%E2%82", "Limit=2"];
        foreach (var query in invalid)
            await fixture.AssertErrorAsync("/events?" + query, HttpStatusCode.BadRequest, "invalid_query");
        await fixture.AssertErrorAsync("/status?limit=1", HttpStatusCode.BadRequest, "invalid_query");
        var scalar128 = string.Concat(Enumerable.Repeat("😀", 128));
        using var accepted = await fixture.GetAsync("/events?sessionId=" + Uri.EscapeDataString(scalar128));
        Assert.Empty(accepted.RootElement.GetProperty("events").EnumerateArray());
        await fixture.AssertErrorAsync("/events?sessionId=" + Uri.EscapeDataString(scalar128 + "😀"), HttpStatusCode.BadRequest, "invalid_query");
        Assert.False(ActivityHttpQueryParser.ValidScalars("\ud800", 128));
        Assert.False(ActivityHttpQueryParser.ValidScalars("\udc00", 128));
    }

    [Fact]
    public async Task ForwardFiltersAreLiteralAndContinuationDoesNotSkipVisibleMatches()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Append("1", preview: "needle"); fixture.Append("2", preview: "Needle");
        fixture.Append("3", request: "request needle"); fixture.Append("4", client: "needle");
        fixture.Append("5", response: "response needle"); fixture.Append("private-other-scope", partition: new('b', 64));
        var epoch = fixture.Journal.Status(fixture.Partition).JournalEpoch;
        using var first = await fixture.GetAsync($"/events?afterSequence=0&journalEpoch={epoch}&q=needle&limit=2");
        Assert.Equal(new[] { "1", "3" }, Ids(first));
        Assert.Equal("3", Cursor(first)); Assert.Equal("6", first.RootElement.GetProperty("highWatermark").GetProperty("sequence").GetString());
        Assert.True(first.RootElement.GetProperty("hasMore").GetBoolean()); Assert.False(first.RootElement.GetProperty("truncated").GetBoolean());
        fixture.Append("7", request: "new needle");
        using var second = await fixture.GetAsync($"/events?afterSequence=3&journalEpoch={epoch}&q=needle&limit=2");
        Assert.Equal(new[] { "5", "7" }, Ids(second)); Assert.Equal("7", Cursor(second)); Assert.False(second.RootElement.GetProperty("hasMore").GetBoolean());
        using var unmatched = await fixture.GetAsync($"/events?afterSequence=0&journalEpoch={epoch}&projectId=any-code-project");
        Assert.Empty(Ids(unmatched)); Assert.Equal("7", Cursor(unmatched));
        using var regex = await fixture.GetAsync($"/events?afterSequence=0&journalEpoch={epoch}&q=" + Uri.EscapeDataString(".*"));
        Assert.Empty(Ids(regex)); Assert.Equal("7", Cursor(regex));
        using var upper = await fixture.GetAsync("/events?q=NEEDLE"); Assert.Empty(Ids(upper));
        using var exact = await fixture.GetAsync("/events?sessionId=session&operation=search"); Assert.Equal(6, Ids(exact).Length);
        using var wrongCase = await fixture.GetAsync("/events?operation=Search"); Assert.Empty(Ids(wrongCase));
        var raw = exact.RootElement.GetRawText(); Assert.DoesNotContain("private-other-scope", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("targetToken", raw, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("private-actor", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackwardPagesHaveExclusiveBoundariesAndByteTruncationHasNoHoles()
    {
        await using var fixture = await Fixture.StartAsync();
        for (var number = 1; number <= 100; number++) fixture.Append(number.ToString(), preview: new string('я', 256), request: new string('я', 128), response: new string('я', 128));
        var epoch = fixture.Journal.Status(fixture.Partition).JournalEpoch;
        var seen = new HashSet<string>(); string suffix = "?limit=100"; var sawTruncation = false;
        while (true)
        {
            using var response = await fixture.Http.GetAsync(AgentActivityHttpEndpoint.Prefix + "/events" + suffix);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            var bytes = await response.Content.ReadAsByteArrayAsync(); Assert.InRange(bytes.Length, 1, 262_144);
            using var page = JsonDocument.Parse(bytes); var rows = page.RootElement.GetProperty("events").EnumerateArray().ToArray();
            var numbers = rows.Select(value => long.Parse(value.GetProperty("sequence").GetString()!)).ToArray(); Assert.Equal(numbers.Order(), numbers);
            foreach (var id in Ids(page)) Assert.True(seen.Add(id));
            sawTruncation |= page.RootElement.GetProperty("truncated").GetBoolean();
            if (!page.RootElement.GetProperty("hasMore").GetBoolean()) { Assert.Equal("0", Cursor(page)); break; }
            suffix = $"?limit=100&journalEpoch={epoch}&beforeSequence={Cursor(page)}";
        }
        Assert.True(sawTruncation); Assert.Equal(100, seen.Count);
        seen.Clear(); suffix = $"?limit=100&journalEpoch={epoch}&afterSequence=0";
        while (true)
        {
            using var page = await fixture.GetAsync("/events" + suffix);
            foreach (var id in Ids(page)) Assert.True(seen.Add(id));
            if (!page.RootElement.GetProperty("hasMore").GetBoolean()) { Assert.Equal("100", Cursor(page)); break; }
            suffix = $"?limit=100&journalEpoch={epoch}&afterSequence={Cursor(page)}";
        }
        Assert.Equal(100, seen.Count);
    }

    [Fact]
    public async Task RotationClearAndFutureCursorsReturnTheCapturedResetHead()
    {
        await using var fixture = await Fixture.StartAsync(maximum: 65_536);
        for (var number = 1; number <= 100; number++) fixture.Append(number.ToString(), preview: new string('я', 256));
        var status = fixture.Journal.Status(fixture.Partition); Assert.True(long.Parse(status.OldestSequence) > 1);
        await fixture.AssertErrorAsync($"/events?journalEpoch={status.JournalEpoch}&afterSequence=0", HttpStatusCode.Conflict, "cursor_expired");
        await fixture.AssertErrorAsync($"/events?journalEpoch={status.JournalEpoch}&afterSequence=9223372036854775807", HttpStatusCode.Conflict, "cursor_expired");
        var cleared = fixture.Journal.ClearPartition(fixture.Partition);
        using var reset = await fixture.AssertErrorAsync($"/events?journalEpoch={status.JournalEpoch}&afterSequence={status.LatestSequence}", HttpStatusCode.Conflict, "cursor_expired");
        Assert.Equal(cleared.JournalEpoch, reset.RootElement.GetProperty("resetCursor").GetProperty("journalEpoch").GetString());
        using var empty = await fixture.GetAsync($"/events?journalEpoch={cleared.JournalEpoch}&afterSequence={cleared.LatestSequence}");
        Assert.Empty(Ids(empty)); Assert.Equal(cleared.LatestSequence, Cursor(empty));
    }

    [Fact]
    public async Task IdleForwardAndInvalidCursorDoNotReadEventFilesOrOpenGraphStorage()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Append("target", targetToken: "server-only-target");
        var status = fixture.Journal.Status(fixture.Partition);
        Assert.False(Directory.Exists(fixture.StorePath));
        using var invalid = await fixture.AssertErrorAsync("/events?journalEpoch=obsolete&afterSequence=0", HttpStatusCode.Conflict, "cursor_expired");
        Assert.False(Directory.Exists(fixture.StorePath));
        // A real exclusive file handle makes any attempted event scan fail, while metadata stays readable.
        using var held = new FileStream(fixture.SegmentPath, FileMode.Open, FileAccess.Read, FileShare.None);
        using var idle = await fixture.GetAsync($"/events?journalEpoch={status.JournalEpoch}&afterSequence={status.LatestSequence}");
        Assert.Empty(Ids(idle)); Assert.Equal(status.LatestSequence, Cursor(idle));
        Assert.False(Directory.Exists(fixture.StorePath));
        await fixture.AssertErrorAsync($"/events?journalEpoch={status.JournalEpoch}&afterSequence=0", HttpStatusCode.ServiceUnavailable, "journal_unavailable");
    }

    [Fact]
    public async Task FixedAuthorityOriginAndUnavailableErrorsKeepOneSafeEnvelope()
    {
        await using (var fixture = await Fixture.StartAsync())
        {
            fixture.Http.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
            await fixture.AssertErrorAsync("/status", HttpStatusCode.Forbidden, "forbidden");
            fixture.Http.DefaultRequestHeaders.Remove("Origin"); fixture.Http.DefaultRequestHeaders.Host = "attacker.invalid";
            await fixture.AssertErrorAsync("/events", HttpStatusCode.Forbidden, "forbidden");
        }
        await using (var production = await Fixture.StartAsync(development: false))
            await production.AssertErrorAsync("/status", HttpStatusCode.Forbidden, "forbidden");
        await using (var unavailable = await Fixture.StartAsync(invalidConfiguration: true))
        {
            using var error = await unavailable.AssertErrorAsync("/status", HttpStatusCode.ServiceUnavailable, "journal_unavailable");
            Assert.True(error.RootElement.GetProperty("error").GetProperty("retryable").GetBoolean());
            await unavailable.AssertErrorAsync("/events", HttpStatusCode.ServiceUnavailable, "journal_unavailable");
        }
    }

    [Fact]
    public void SharedGoldenEnvelopesAndQueryCasesUseTheSameContract()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "activity-http-contract", "examples.json")));
        foreach (var query in fixtures.RootElement.GetProperty("validation").GetProperty("invalidQueries").EnumerateArray())
        {
            var context = new DefaultHttpContext(); context.Request.QueryString = new("?" + query.GetString());
            Assert.False(ActivityHttpQueryParser.TryParse(context.Request, out _));
        }
        foreach (var query in fixtures.RootElement.GetProperty("validation").GetProperty("validQueries").EnumerateArray())
        {
            var context = new DefaultHttpContext(); context.Request.QueryString = new("?" + query.GetString());
            Assert.True(ActivityHttpQueryParser.TryParse(context.Request, out _));
        }
        foreach (var example in fixtures.RootElement.GetProperty("cases").EnumerateArray())
        {
            var status = example.GetProperty("status");
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(status.GetRawText()), JsonSerializer.SerializeToNode(status.Deserialize<ActivityHttpStatus>(ActivityJson.Options), ActivityJson.Options)));
            foreach (var item in example.GetProperty("errors").EnumerateArray())
            {
                var expected = item.GetProperty("response");
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), JsonSerializer.SerializeToNode(expected.Deserialize<ActivityHttpError>(ActivityJson.Options), ActivityJson.Options)));
            }
            foreach (var item in example.GetProperty("pages").EnumerateArray())
            {
                var expected = JsonNode.Parse(item.GetProperty("response").GetRawText())!;
                var page = item.GetProperty("response").Deserialize<ActivityHttpPage>(ActivityJson.Options)!;
                Assert.Equal(item.GetProperty("response").GetProperty("events").EnumerateArray().Select(value => value.GetProperty("eventId").GetString()), page.Events.Select(value => value.EventId));
                // Optional producer-specific event fields are not normalized by this envelope roundtrip test.
                expected["events"] = new JsonArray();
                var actual = JsonSerializer.SerializeToNode(page with { Events = [] }, ActivityJson.Options);
                Assert.True(JsonNode.DeepEquals(expected, actual));
            }
        }
    }

    private static string[] Ids(JsonDocument page) => page.RootElement.GetProperty("events").EnumerateArray().Select(value => value.GetProperty("eventId").GetString()!).ToArray();
    private static string Cursor(JsonDocument page) => page.RootElement.GetProperty("nextCursor").GetProperty("sequence").GetString()!;

    /// <summary>Owns only temporary storage, a private journal and an ephemeral real Kestrel listener.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly LocalMemoryGraphFeature _graph;
        private readonly LocalAgentActivityFeature _feature;
        private readonly WebApplication _app;
        public SegmentJournal Journal { get; }
        public string Partition { get; }
        public HttpClient Http { get; }
        public string StorePath => Path.Combine(_root, "memory.lancedb");
        public string SegmentPath => Directory.GetFiles(Path.Combine(_root, "agent-activity"), "segment-*.jsonl").Single();
        private Fixture(string root, SegmentJournal journal, string partition, LocalMemoryGraphFeature graph, LocalAgentActivityFeature feature, WebApplication app)
        { _root = root; Journal = journal; Partition = partition; _graph = graph; _feature = feature; _app = app; Http = new() { BaseAddress = new(app.Urls.Single()) }; }

        public static async Task<Fixture> StartAsync(long maximum = 50_000_000, bool development = true, bool invalidConfiguration = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "agm-http-v1-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "appsettings.local.json"), JsonSerializer.Serialize(new { ActivityJournal = new { MaxSizeBytes = invalidConfiguration ? 0 : maximum } }));
            var storePath = Path.Combine(root, "memory.lancedb");
            var scope = new MemoryScope(new("private-tenant")); var actor = new ActorId("private-actor");
            var partition = ActivityIdentity.Partition(actor, scope, storePath);
            var journal = new SegmentJournal(Path.Combine(root, "agent-activity"), new() { MaxSizeBytes = maximum });
            var graph = new LocalMemoryGraphFeature(new() { Enabled = true, StoragePath = storePath, ActorId = actor.Value, Scope = new() { TenantId = "private-tenant" } }, root, new EphemeralDataProtectionProvider());
            var feature = new LocalAgentActivityFeature(new() { Enabled = true, StoragePath = storePath, ActorId = actor.Value, Scope = new() { TenantId = "private-tenant" } }, root, graph);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = development ? Environments.Development : Environments.Production });
            builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(feature); builder.Services.AddAntiforgery();
            var app = builder.Build(); app.UseWebSockets(); AgentActivityEndpoint.Map(app); await app.StartAsync();
            return new(root, journal, partition, graph, feature, app);
        }

        public void Append(string id, string? preview = null, string? request = null, string? response = null, string client = "codex", string? partition = null, string? targetToken = null) => Journal.Append([new(partition ?? Partition, new() {
            EventId = id, SessionId = "session", OperationId = "operation-" + id, Operation = "search", Phase = "finished", Outcome = "succeeded", ClientKind = client,
            Query = preview is null ? new("omitted") : new("preview", Preview: preview), Request = request is null ? null : new("memory_recall", request),
            Response = response is null ? null : new(response) }, targetToken)]);

        public async Task<JsonDocument> GetAsync(string suffix)
        {
            using var response = await Http.GetAsync(AgentActivityHttpEndpoint.Prefix + suffix); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.StartsWith("application/json", response.Content.Headers.ContentType!.ToString());
            return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        }
        public async Task<JsonDocument> AssertErrorAsync(string suffix, HttpStatusCode code, string expected)
        {
            using var response = await Http.GetAsync(AgentActivityHttpEndpoint.Prefix + suffix); Assert.Equal(code, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(); var json = JsonDocument.Parse(text);
            Assert.Equal("agent-activity-http/v1", json.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal(expected, json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(code == HttpStatusCode.Conflict ? JsonValueKind.Object : JsonValueKind.Null, json.RootElement.GetProperty("resetCursor").ValueKind);
            Assert.DoesNotContain("private-", text, StringComparison.Ordinal); Assert.DoesNotContain(_root, text, StringComparison.Ordinal);
            return json;
        }
        public async ValueTask DisposeAsync()
        { Http.Dispose(); await _app.StopAsync(); await _app.DisposeAsync(); _feature.Dispose(); await _graph.DisposeAsync(); Journal.Dispose(); Directory.Delete(_root, true); }
    }
}
