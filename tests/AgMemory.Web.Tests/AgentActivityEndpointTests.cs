using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgMemory.ActivityJournal;
using AgMemory.McpServer;
using AgMemory.Web.Features.AgentActivity;
using AgMemory.Web.Features.MemoryGraph;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using Xunit;

namespace AgMemory.Web.Tests;

/// <summary>Exercises real loopback HTTP/WS against an isolated journal and actual stdio MCP producer.</summary>
public sealed class AgentActivityEndpointTests
{
    [Fact]
    public async Task ActualMcpToHistoryAndAckedWebSocketProjectsOnlyAuthorizedCurrentNodes()
    {
        var root = Path.Combine(Path.GetTempPath(), "agm-activity-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storePath = Path.Combine(root, "memory.lancedb");
            var options = new MemoryGraphHostOptions { Enabled = true, StoragePath = storePath, ActorId = "test-actor",
                Scope = new() { TenantId = "test-tenant" } };
            await using var graph = new LocalMemoryGraphFeature(options, root, new EphemeralDataProtectionProvider());
            using var activity = new LocalAgentActivityFeature(options, root, graph);
            await using var app = await StartAsync(activity);
            var address = app.Urls.Single();
            using var http = new HttpClient { BaseAddress = new(address) };
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["AGMEMORY_STORAGE_PATH"] = storePath; environment["AGMEMORY_DATA_DIR"] = root;
            environment["AGMEMORY_ACTOR_ID"] = "test-actor"; environment["AGMEMORY_TENANT_ID"] = "test-tenant";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new() { Name = "web-activity", Command = "dotnet",
                Arguments = [McpAssemblyPath()], InheritEnvironmentVariables = false, EnvironmentVariables = environment }), cancellationToken: deadline.Token);
            await client.CallToolAsync("memory_remember", new Dictionary<string, object?> { ["content"] = "Synthetic isolated activity target.", ["memoryType"] = "Fact" }, cancellationToken: deadline.Token);
            await Task.Delay(350, deadline.Token);
            var history = await http.GetStringAsync(AgentActivityEndpoint.Prefix + "/history", deadline.Token);
            using var page = JsonDocument.Parse(history);
            Assert.Equal(2, page.RootElement.GetProperty("events").GetArrayLength());
            var finished = page.RootElement.GetProperty("events")[1];
            Assert.Equal("available", finished.GetProperty("target").GetProperty("status").GetString());
            var nodeKey = finished.GetProperty("target").GetProperty("nodeKey").GetString();
            Assert.Matches("^[a-f0-9]{32}$", nodeKey!);
            Assert.DoesNotContain("test-actor", history, StringComparison.Ordinal);
            Assert.DoesNotContain("Synthetic isolated activity target", history, StringComparison.Ordinal);
            Assert.DoesNotContain("targetToken", history, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync(AgentActivityEndpoint.Prefix + "/history?limit=101", deadline.Token)).StatusCode);

            using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", address);
            await socket.ConnectAsync(new Uri(address.Replace("http:", "ws:") + AgentActivityEndpoint.Prefix + "/stream"), deadline.Token);
            await SendAsync(socket, new { type = "subscribe" }, deadline.Token);
            using var snapshot = await ReceiveAsync(socket, deadline.Token);
            Assert.Equal("snapshot", snapshot.RootElement.GetProperty("type").GetString());
            var cursor = snapshot.RootElement.GetProperty("cursor").Clone();
            await SendAsync(socket, new { type = "ack", messageId = snapshot.RootElement.GetProperty("messageId").GetString(), cursor }, deadline.Token);
            await client.CallToolAsync("memory_recall", new Dictionary<string, object?> { ["query"] = "Synthetic", ["limit"] = 5 }, cancellationToken: deadline.Token);
            using var live = await ReceiveAsync(socket, deadline.Token);
            Assert.Equal("events", live.RootElement.GetProperty("type").GetString());
            Assert.Contains("Synthetic", live.RootElement.GetRawText(), StringComparison.Ordinal);
            var requestSummary = live.RootElement.GetProperty("events")[0].GetProperty("request").GetProperty("summary").GetString();
            Assert.Contains("максимум 5", requestSummary!, StringComparison.Ordinal);
            await SendAsync(socket, new { type = "ack", messageId = live.RootElement.GetProperty("messageId").GetString(), cursor = live.RootElement.GetProperty("cursor").Clone() }, deadline.Token);
            socket.Abort();

            using var hostile = new ClientWebSocket(); hostile.Options.SetRequestHeader("Origin", "https://attacker.invalid");
            await Assert.ThrowsAsync<WebSocketException>(() => hostile.ConnectAsync(new Uri(address.Replace("http:", "ws:") + AgentActivityEndpoint.Prefix + "/stream"), deadline.Token));
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync(AgentActivityEndpoint.Prefix + "/clear", new StringContent("{}", Encoding.UTF8, "application/json"), deadline.Token)).StatusCode);
            await app.StopAsync(deadline.Token);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnacknowledgedConsumerIsDisconnectedWithinTheProgressDeadline()
    {
        var root = Path.Combine(Path.GetTempPath(), "agm-activity-ack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new MemoryGraphHostOptions { Enabled = true, StoragePath = Path.Combine(root, "store"), ActorId = "actor", Scope = new() { TenantId = "tenant" } };
            await using var graph = new LocalMemoryGraphFeature(options, root, new EphemeralDataProtectionProvider());
            using var activity = new LocalAgentActivityFeature(options, root, graph);
            await using var app = await StartAsync(activity);
            var address = app.Urls.Single(); using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", address);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            await socket.ConnectAsync(new Uri(address.Replace("http:", "ws:") + AgentActivityEndpoint.Prefix + "/stream"), deadline.Token);
            await SendAsync(socket, new { type = "subscribe" }, deadline.Token);
            using var snapshot = await ReceiveAsync(socket, deadline.Token);
            Assert.Equal("snapshot", snapshot.RootElement.GetProperty("type").GetString());
            await Assert.ThrowsAsync<WebSocketException>(() => ReceiveAsync(socket, deadline.Token));
            await app.StopAsync(deadline.Token);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RepeatedIdleCloseAndUnexpectedBufferedAckDoNotExhaustStreamSlots()
    {
        var root = Path.Combine(Path.GetTempPath(), "agm-activity-idle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var options = new MemoryGraphHostOptions { Enabled = true, StoragePath = Path.Combine(root, "store"), ActorId = "actor", Scope = new() { TenantId = "tenant" } };
            await using var graph = new LocalMemoryGraphFeature(options, root, new EphemeralDataProtectionProvider());
            using var activity = new LocalAgentActivityFeature(options, root, graph); await using var app = await StartAsync(activity);
            var address = app.Urls.Single(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            for (var index = 0; index < 40; index++)
            {
                using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", address);
                await socket.ConnectAsync(new Uri(address.Replace("http:", "ws:") + AgentActivityEndpoint.Prefix + "/stream"), deadline.Token);
                await SendAsync(socket, new { type = "subscribe" }, deadline.Token);
                using var snapshot = await ReceiveAsync(socket, deadline.Token);
                var ack = new { type = "ack", messageId = snapshot.RootElement.GetProperty("messageId").GetString(), cursor = snapshot.RootElement.GetProperty("cursor").Clone() };
                await SendAsync(socket, ack, deadline.Token);
                if (index % 2 == 1) await SendAsync(socket, ack, deadline.Token);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
                await Task.Delay(30, deadline.Token);
            }
            await app.StopAsync(deadline.Token);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HistoryByteBudgetIsEnforcedBeforeSendingUnicodePreviews()
    {
        var root = Path.Combine(Path.GetTempPath(), "agm-activity-budget-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var storePath = Path.Combine(root, "store");
            var options = new MemoryGraphHostOptions { Enabled = true, StoragePath = storePath, ActorId = "actor", Scope = new() { TenantId = "tenant" } };
            var configuration = LocalAgMemoryMcpConfiguration.Create(storePath, "actor", "tenant");
            var partition = ActivityIdentity.Partition(configuration.Actor, configuration.Scope, configuration.StoragePath);
            using (var journal = new SegmentJournal(Path.Combine(root, "agent-activity"), new()))
            {
                for (var index = 0; index < 100; index++) journal.Append([new(partition, new ActivityEvent {
                    SessionId = "session", OperationId = Guid.NewGuid().ToString("N"), Operation = "RecallAsync", Phase = "finished", Outcome = "succeeded",
                    Query = new("preview", Preview: new string('я', 256)), Request = new("recall", new string('я', 128)), Response = new(new string('я', 128)) })]);
            }
            await using var graph = new LocalMemoryGraphFeature(options, root, new EphemeralDataProtectionProvider());
            using var activity = new LocalAgentActivityFeature(options, root, graph); await using var app = await StartAsync(activity);
            using var http = new HttpClient { BaseAddress = new(app.Urls.Single()) };
            var bytes = await http.GetByteArrayAsync(AgentActivityEndpoint.Prefix + "/history?limit=100");
            Assert.InRange(bytes.Length, 1, 262_144); using var page = JsonDocument.Parse(bytes);
            Assert.True(page.RootElement.GetProperty("hasMore").GetBoolean()); Assert.InRange(page.RootElement.GetProperty("events").GetArrayLength(), 1, 99);
            await app.StopAsync();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string McpAssemblyPath()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "ag-memory.slnx"))) folder = folder.Parent;
        if (folder is null) throw new InvalidOperationException("Repository test fixture is unavailable.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(folder.FullName, "src", "AgMemory.McpServer", "bin", configuration, "net10.0", "AgMemory.McpServer.dll");
    }

    /// <summary>Hosts only the real activity endpoints with a fixed server authority and ephemeral loopback port.</summary>
    private static async Task<WebApplication> StartAsync(LocalAgentActivityFeature feature)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(feature); builder.Services.AddAntiforgery();
        var app = builder.Build(); app.UseWebSockets(); AgentActivityEndpoint.Map(app); await app.StartAsync(); return app;
    }
    private static Task SendAsync(ClientWebSocket socket, object value, CancellationToken cancellationToken) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, ActivityJson.Options), WebSocketMessageType.Text, true, cancellationToken);
    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var bytes = new byte[65_536]; var offset = 0;
        while (true)
        {
            var received = await socket.ReceiveAsync(bytes.AsMemory(offset), cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed");
            offset += received.Count;
            if (received.EndOfMessage) return JsonDocument.Parse(bytes.AsMemory(0, offset));
        }
    }
}
