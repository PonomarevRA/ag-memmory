using System.Text.Json;
using AgMemory.McpServer;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgMemory.McpServer.Tests;

public sealed class AgentProtocolTransportTests
{
    [Fact]
    public async Task NewToolsRoundTripThroughRealStdioAndKeepSafeEnvelopes()
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment[LocalAgMemoryMcpConfiguration.StoragePathVariable] = Path.Combine(Path.GetTempPath(), "agmemory-agent-wire-" + Guid.NewGuid().ToString("N"));
        environment["AGMEMORY_DATA_DIR"] = Path.Combine(environment[LocalAgMemoryMcpConfiguration.StoragePathVariable]!, ".activity-data");
        environment[LocalAgMemoryMcpConfiguration.ActorIdVariable] = "wire-actor";
        environment[LocalAgMemoryMcpConfiguration.TenantIdVariable] = "wire-tenant";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var transport = new StdioClientTransport(new() { Name = "agent-v2-wire", Command = "dotnet",
            Arguments = [typeof(LocalAgMemoryMcpRuntime).Assembly.Location], InheritEnvironmentVariables = false, EnvironmentVariables = environment });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
        var capabilities = await Call("memory_capabilities", new());
        Assert.Equal("lexical", capabilities.GetProperty("Data").GetProperty("RetrievalMode").GetString());
        var handoff = await Call("memory_handoff", new()
        {
            ["handoff"] = new { Facts = new[] { "WireMarker bounded context" }, Assumptions = Array.Empty<string>(),
                OpenQuestions = Array.Empty<string>(), NextAction = "Review output", Evidence = new[] { new { Label = "test", Claim = "reported test" } } },
            ["requestKey"] = "wire_handoff"
        });
        Assert.Equal(JsonValueKind.Null, handoff.GetProperty("Error").ValueKind);
        var decision = await Call("memory_decision", new()
        {
            ["decision"] = new { Problem = "Choose wire protocol", Context = "Local tests", Options = new[] { "v1", "v2" },
                Decision = "v2", Reason = "Bounded output", Consequences = new[] { "Keep compatibility" } }, ["requestKey"] = "wire_decision"
        });
        Assert.Equal(JsonValueKind.Null, decision.GetProperty("Error").ValueKind);
        var context = await Call("memory_context", new() { ["query"] = "WireMarker", ["budget"] = 2048 });
        var memory = Assert.Single(context.GetProperty("Data").GetProperty("Items").EnumerateArray());
        var citation = memory.GetProperty("CitationRef").GetString();
        var resolved = await Call("memory_resolve", new() { ["citationRef"] = citation });
        Assert.Equal(JsonValueKind.Null, resolved.GetProperty("Error").ValueKind);
        var corrected = await Call("memory_correct", new() { ["citationRef"] = citation,
            ["expectedRevision"] = memory.GetProperty("Revision").GetInt64(), ["requestKey"] = "wire_retract" });
        Assert.Equal("Applied", corrected.GetProperty("Data").GetProperty("Outcome").GetString());
        var stale = await Call("memory_resolve", new() { ["citationRef"] = citation });
        Assert.Equal("reference_unavailable", stale.GetProperty("Error").GetProperty("Code").GetString());

        async Task<JsonElement> Call(string tool, Dictionary<string, object?> arguments)
        {
            var response = await client.CallToolAsync(tool, arguments, cancellationToken: deadline.Token);
            Assert.NotEqual(true, response.IsError);
            var content = Assert.IsType<TextContentBlock>(Assert.Single(response.Content));
            using var json = JsonDocument.Parse(content.Text);
            Assert.Equal("agmemory-agent-v2", json.RootElement.GetProperty("SchemaVersion").GetString());
            return json.RootElement.Clone();
        }
    }
}
