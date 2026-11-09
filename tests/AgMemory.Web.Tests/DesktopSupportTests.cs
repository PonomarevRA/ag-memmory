using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgMemory.ActivityJournal;
using AgMemory.Contracts;
using AgMemory.DesktopSupport;
using AgMemory.Web.Features.AgentActivity;
using AgMemory.Web.Features.Desktop;
using AgMemory.Web.Features.MemoryGraph;
using AgMemory.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgMemory.Web.Tests;

/// <summary>Exercises installer validation and widget snapshots using only temporary paths and owned loopback listeners.</summary>
public sealed class DesktopSupportTests
{
    [Fact]
    public void LocatorAbsentMalformedOversizedAndSymlinkFailClosed()
    {
        using var paths = new Paths(); var file = Path.Combine(paths.Root, "installation.json");
        Assert.Null(DesktopInstallation.Read(file));
        Directory.CreateDirectory(file); Assert.Throws<InvalidDataException>(() => DesktopInstallation.Read(file)); Directory.Delete(file);
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(file, file + ".missing");
            Assert.Throws<InvalidDataException>(() => DesktopInstallation.Read(file)); File.Delete(file);
        }
        File.WriteAllText(file, "{}"); Assert.Throws<InvalidDataException>(() => DesktopInstallation.Read(file));
        File.WriteAllText(file, new string(' ', 65_537)); Assert.Throws<InvalidDataException>(() => DesktopInstallation.Read(file));
        File.WriteAllText(file, JsonSerializer.Serialize(paths.Valid, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(DesktopInstallation.Canonical(paths.Valid.DataDirectory), DesktopInstallation.Read(file)!.DataDirectory);
        if (!OperatingSystem.IsWindows())
        {
            var link = file + ".link"; File.CreateSymbolicLink(link, file);
            Assert.Throws<InvalidDataException>(() => DesktopInstallation.Read(link));
        }
    }

    [Fact]
    public void RootsPortsAndRepositoryStorageAreValidated()
    {
        using var paths = new Paths(); var valid = paths.Valid;
        Assert.True(valid.Validate().CloseToTray); Assert.False(valid.LaunchAtLogin);
        foreach (var invalid in new[] { valid with { DataDirectory = "." }, valid with { DataDirectory = Path.GetPathRoot(paths.Root)! },
            valid with { BackupDirectory = valid.DataDirectory }, valid with { BackupDirectory = Path.Combine(valid.DataDirectory, "backups") },
            valid with { ApplicationPath = Path.Combine(valid.BackupDirectory, "app") }, valid with { PreferredPort = 0 }, valid with { PreferredPort = 65536 },
            valid with { AppId = "other" }, valid with { SchemaVersion = "v0" } })
            Assert.Throws<InvalidDataException>(() => invalid.Validate());
        Directory.CreateDirectory(Path.Combine(paths.Root, "repo", ".git"));
        Assert.Throws<InvalidDataException>(() => (valid with { DataDirectory = Path.Combine(paths.Root, "repo", "data") }).Validate());
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(valid.DataDirectory);
            var alias = Path.Combine(paths.Root, "alias"); Directory.CreateSymbolicLink(alias, valid.DataDirectory);
            Assert.Throws<InvalidDataException>(() => (valid with { BackupDirectory = alias }).Validate());
        }
        Assert.False(DesktopInstallation.Overlap(Path.Combine(paths.Root, "data"), Path.Combine(paths.Root, "data2")));
        Assert.Equal(Path.GetFullPath("explicit"), LocalApplicationPaths.ResolveDataDirectory("explicit"));
    }

    [Fact]
    public void PreferredPortFallbackNeverAdoptsAnOccupiedListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try { Assert.Equal(0, MacDesktopHost.SelectPort(((IPEndPoint)listener.LocalEndpoint).Port)); }
        finally { listener.Stop(); }
        Assert.Equal(0, MacDesktopHost.SelectPort(0));
        Assert.Null(MacDesktopHost.NativeReadyLine("http://attacker.invalid:8891"));
        Assert.Equal("AGMEMORY_DESKTOP_URL=http://127.0.0.1:8891/", MacDesktopHost.NativeReadyLine("http://127.0.0.1:8891"));
    }

    [Fact]
    public async Task WidgetUsesAuthorizedMetadataBoundsAndDoesNotOpenMemoryStorage()
    {
        await using var fixture = await Fixture.StartAsync();
        for (var index = 0; index < 20; index++) fixture.Append(index, "password=secret-value " + new string('x', 700));
        fixture.Append(100, "private event", partition: new('b', 64));
        using var response = await fixture.Http.GetAsync("/api/desktop/v1/widget");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.InRange((await response.Content.ReadAsByteArrayAsync()).Length, 1, 65_536);
        var text = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(text); var root = json.RootElement;
        Assert.Equal("agmemory", root.GetProperty("appId").GetString()); Assert.Equal("ag-desktop/widget-v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(fixture.Http.BaseAddress!.GetLeftPart(UriPartial.Authority), root.GetProperty("origin").GetString());
        var rows = root.GetProperty("requests").EnumerateArray().ToArray(); Assert.Equal(8, rows.Length);
        Assert.All(rows, row => { Assert.True(row.GetProperty("requestSummary").GetString()!.Length <= 512); Assert.Equal("succeeded", row.GetProperty("status").GetString()); });
        Assert.DoesNotContain("secret-value", text); Assert.DoesNotContain("private event", text); Assert.DoesNotContain("private-actor", text);
        Assert.False(Directory.Exists(fixture.StorePath));
        var before = fixture.Journal.Status(fixture.Partition).LatestSequence;
        using var again = await fixture.Http.GetAsync("/api/desktop/v1/widget");
        Assert.Equal(before, fixture.Journal.Status(fixture.Partition).LatestSequence);
    }

    [Fact]
    public async Task WidgetRejectsRemoteOriginsHostsQueriesAndReportsEmptyCoverage()
    {
        await using var fixture = await Fixture.StartAsync();
        using (var response = await fixture.Http.GetAsync("/api/desktop/v1/widget"))
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Empty(json.RootElement.GetProperty("requests").EnumerateArray());
        }
        fixture.Http.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
        using (var denied = await fixture.Http.GetAsync("/api/desktop/v1/widget")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        fixture.Http.DefaultRequestHeaders.Remove("Origin"); fixture.Http.DefaultRequestHeaders.Host = "attacker.invalid";
        using (var denied = await fixture.Http.GetAsync("/api/desktop/v1/widget")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        fixture.Http.DefaultRequestHeaders.Host = null;
        using var invalid = await fixture.Http.GetAsync("/api/desktop/v1/widget?dataDirectory=private"); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task UnconfiguredWidgetIsUnavailableRatherThanInventingAnEmptyHealthyStore()
    {
        await using var fixture = await Fixture.StartAsync(enabled: false);
        using var response = await fixture.Http.GetAsync("/api/desktop/v1/widget");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unavailable", json.RootElement.GetProperty("status").GetString());
        Assert.Empty(json.RootElement.GetProperty("requests").EnumerateArray());
        Assert.False(Directory.Exists(fixture.StorePath));
    }

    [Fact]
    public async Task EmbeddedOwnerPipeRequestsStopOnlyWhileLifetimeIsActive()
    {
        var stops = 0;
        await DesktopOwnerLifetime.WaitForOwnerAsync(new StringReader(""), () => stops++, CancellationToken.None);
        Assert.Equal(1, stops);
        await DesktopOwnerLifetime.WaitForOwnerAsync(new StringReader("unexpected"), () => stops++, CancellationToken.None);
        Assert.Equal(2, stops);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await DesktopOwnerLifetime.WaitForOwnerAsync(new StringReader(""), () => stops++, cancelled.Token);
        Assert.Equal(2, stops);
    }

    [Fact]
    public void WorstCaseUnicodeWidgetReportsOmissionsWithinTransportHeadroom()
    {
        var text = new string('界', 512);
        var rows = Enumerable.Range(0, 8).Select(_ => new DesktopWidgetRequest(DateTimeOffset.UtcNow, text, text, text, null, text, text)).ToArray();
        var snapshot = new DesktopWidgetResponse("ag-desktop/widget-v1", "agmemory", "1.5.1", DateTimeOffset.UtcNow, "ok", "http://127.0.0.1:8891", rows, "Authorized metadata snapshot.");
        var bounded = DesktopWidget.FitBudget(snapshot);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(bounded, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length, 1, 60 * 1024);
        Assert.InRange(bounded.Requests.Count, 1, 7);
        Assert.Contains($"Omitted {8 - bounded.Requests.Count}", bounded.Source);
        Assert.Equal(8, snapshot.Requests.Count);
    }

    private sealed class Paths : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "agm-desktop-" + Guid.NewGuid().ToString("N"));
        public DesktopInstallation Valid => new("ag-desktop/install-v1", "agmemory", Path.Combine(Root, "data"), Path.Combine(Root, "backups"), Path.Combine(Root, "AgMemory.app"));
        public Paths() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Paths _paths; private readonly WebApplication _app; private readonly LocalMemoryGraphFeature _graph; private readonly LocalAgentActivityFeature _activity;
        public SegmentJournal Journal { get; } public string Partition { get; } public HttpClient Http { get; } public string StorePath { get; }
        private Fixture(Paths paths, WebApplication app, LocalMemoryGraphFeature graph, LocalAgentActivityFeature activity, SegmentJournal journal, string partition, string store)
        { _paths = paths; _app = app; _graph = graph; _activity = activity; Journal = journal; Partition = partition; StorePath = store; Http = new() { BaseAddress = new(app.Urls.Single()) }; }
        public static async Task<Fixture> StartAsync(bool enabled = true)
        {
            var paths = new Paths(); var store = Path.Combine(paths.Root, "memory.lancedb");
            var options = new MemoryGraphHostOptions { Enabled = enabled, StoragePath = store, ActorId = "private-actor", Scope = new() { TenantId = "private-tenant" } };
            var graph = new LocalMemoryGraphFeature(options, paths.Root, new EphemeralDataProtectionProvider());
            var feature = new LocalAgentActivityFeature(options, paths.Root, graph);
            var journal = new SegmentJournal(Path.Combine(paths.Root, "agent-activity"), new());
            var partition = ActivityIdentity.Partition(new ActorId("private-actor"), new MemoryScope(new("private-tenant")), store);
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(feature); builder.Services.AddSingleton<IDesktopWidget, DesktopWidget>();
            var app = builder.Build(); DesktopWidgetEndpoint.Map(app); await app.StartAsync();
            return new(paths, app, graph, feature, journal, partition, store);
        }
        public void Append(int id, string summary, string? partition = null) => Journal.Append([new(partition ?? Partition, new() { EventId = id.ToString(), OperationId = "op-" + id,
            Operation = "memory_recall", SessionId = "session", ClientKind = "codex", Phase = "finished", Outcome = "succeeded", Request = new("memory_recall", summary), Response = new("3 items") })]);
        public async ValueTask DisposeAsync()
        { Http.Dispose(); await _app.StopAsync(); await _app.DisposeAsync(); _activity.Dispose(); await _graph.DisposeAsync(); Journal.Dispose(); _paths.Dispose(); }
    }
}
