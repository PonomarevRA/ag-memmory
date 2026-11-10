using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Checks the shared procedure boundary through real stdio MCP connections.</summary>
public sealed class SharedProcedureTransportTests
{
    /// <summary>Projects publish and discover in the existing local library while private notes and L2 remain scoped.</summary>
    [Fact]
    public async Task OrdinaryProjectsPublishAndDiscoverWithoutGrantingUpdates()
    {
        var path = Path.Combine(Path.GetTempPath(), "agmemory-shared-wire-" + Guid.NewGuid().ToString("N"));
        try
        {
            var partition = LocalAgMemoryMcpRuntime.SharedProcedurePartition;
            var legacy = LocalAgMemoryMcpConfiguration.Create(path, "maintainer", "tenant", partition, knowledgeTeamId: partition);
            await using var oldPublisher = new LocalAgMemoryMcpRuntime(legacy);
            Assert.Null((await oldPublisher.KnowledgeProcedureAsync(null, 0, "Legacy broker procedure", [], "dev",
                "Read access", "Read the queue", "Check queue units", null, "legacy_broker")).Error);

            var first = LocalAgMemoryMcpConfiguration.Create(path, "agent-a", "tenant", "repo-a", knowledgeTeamId: "same-team");
            var second = LocalAgMemoryMcpConfiguration.Create(path, "agent-b", "tenant", "repo-b", knowledgeTeamId: "same-team");
            await using var repoA = new LocalAgMemoryMcpRuntime(first);
            await using var repoB = new LocalAgMemoryMcpRuntime(second);
            Assert.Null((await repoA.KnowledgeProcedureAsync(null, 0, "Kafka private procedure", [], "dev",
                "Read access", "Use local fixture", "Check result", null, "private_kafka")).Error);
            Assert.Null((await repoA.TaskWorkingAsync("same-task", "Private L2", [], [], [], "Continue", 0, "private_l2")).Error);
            Assert.Null((await repoB.TaskContextCompactAsync("same-task")).Data!.WorkingSet);
            Assert.Empty((await repoB.KnowledgeProcedureDiscoveryAsync("Kafka", "dev", [])).Data!);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var token = deadline.Token;
            await using var a = await ConnectAsync(first with { UseKnowledgeJournal = true }, token);
            await using var b = await ConnectAsync(second with { KnowledgeTeamId = null }, token);
            await using var foreign = await ConnectAsync(
                LocalAgMemoryMcpConfiguration.Create(path, "agent-c", "other-tenant", "repo-c", knowledgeTeamId: "same-team"), token);

            var tools = await a.ListToolsAsync(cancellationToken: token);
            var publish = Assert.Single(tools, tool => tool.Name == "memory_shared_procedure_publish");
            var schema = publish.JsonSchema.GetProperty("properties");
            Assert.DoesNotContain(schema.EnumerateObject(), property =>
                property.Name is "procedureRef" or "expectedRevision" or "runbookRef" or "tenantId" or "projectId");
            var capabilities = Data(await CallAsync(a, "knowledge_capabilities", [], token));
            Assert.Contains(capabilities.GetProperty("Operations").EnumerateArray(), operation => operation.GetString() == publish.Name);

            var publication = Procedure("Kafka lag triage");
            var created = Data(await CallAsync(a, publish.Name, publication, token));
            var replayed = Data(await CallAsync(a, publish.Name, publication, token));
            Assert.Equal(1, created.GetProperty("Revision").GetInt64());
            Assert.Equal(created.GetProperty("UpdatedAtUtc").GetString(), replayed.GetProperty("UpdatedAtUtc").GetString());

            var changed = new Dictionary<string, object?>(publication) { ["steps"] = "Try a different procedure" };
            AssertError(await CallAsync(a, publish.Name, changed, token), "request_conflict");
            var unsafePublication = new Dictionary<string, object?>(publication)
                { ["steps"] = "Read token=placeholder-private-value", ["requestKey"] = "unsafe_kafka" };
            AssertError(await CallAsync(a, publish.Name, unsafePublication, token), "unsafe_content");
            var invalidEnvironment = new Dictionary<string, object?>(publication)
                { ["applicability"] = new string('x', 501), ["requestKey"] = "invalid_environment" };
            AssertError(await CallAsync(a, publish.Name, invalidEnvironment, token), "invalid_argument");

            // A caller in another project can use the same symbolic request key without taking over A's card.
            Assert.Equal(1, Data(await CallAsync(b, publish.Name, Procedure("Redis eviction triage"), token))
                .GetProperty("Revision").GetInt64());
            var found = Assert.Single((await DiscoverAsync(b, "Kafka", token)).EnumerateArray());
            var discovered = found.GetProperty("Procedure");
            Assert.Equal("Kafka lag triage", discovered.GetProperty("Goal").GetString());
            Assert.Equal("caller_reported_unverified", found.GetProperty("EvidenceTrust").GetString());
            Assert.Empty((await DiscoverAsync(foreign, "Kafka", token)).EnumerateArray());
            Assert.Single((await DiscoverAsync(a, "Redis", token)).EnumerateArray());
            Assert.Single((await DiscoverAsync(b, "Legacy broker", token)).EnumerateArray());

            var update = Procedure("Invalid overwrite");
            update["procedureRef"] = discovered.GetProperty("ProcedureRef").GetString();
            update["expectedRevision"] = 1;
            update["runbookRef"] = null;
            AssertError(await CallAsync(b, "memory_procedure", update, token), "reference_unavailable");
            Assert.Single((await DiscoverAsync(b, "Kafka", token)).EnumerateArray());
            Assert.Empty((await repoB.KnowledgeProcedureDiscoveryAsync("Kafka", "dev", [])).Data!);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    private static Dictionary<string, object?> Procedure(string goal) => new()
    {
        ["goal"] = goal, ["aliases"] = Array.Empty<string>(), ["applicability"] = "dev",
        ["preconditions"] = "Read access", ["steps"] = "Read the lag", ["verification"] = "Check lag units",
        ["requestKey"] = "triage"
    };

    private static async Task<JsonElement> DiscoverAsync(McpClient client, string query, CancellationToken token) =>
        Data(await CallAsync(client, "memory_shared_procedure_discovery", new()
        {
            ["query"] = query, ["environment"] = "dev", ["capabilities"] = Array.Empty<string>()
        }, token));

    private static async Task<JsonElement> CallAsync(McpClient client, string name, Dictionary<string, object?> arguments,
        CancellationToken token)
    {
        var response = await client.CallToolAsync(name, arguments, cancellationToken: token);
        var content = Assert.IsType<TextContentBlock>(Assert.Single(response.Content));
        Assert.False(response.IsError is true, $"{name}: {content.Text}");
        using var json = JsonDocument.Parse(content.Text);
        return json.RootElement.Clone();
    }

    private static JsonElement Data(JsonElement response)
    {
        Assert.Equal(JsonValueKind.Null, response.GetProperty("Error").ValueKind);
        return response.GetProperty("Data");
    }

    private static void AssertError(JsonElement response, string code) =>
        Assert.Equal(code, response.GetProperty("Error").GetProperty("Code").GetString());

    /// <summary>Starts an ordinary project client with isolated configuration and activity storage.</summary>
    private static Task<McpClient> ConnectAsync(LocalAgMemoryMcpConfiguration config, CancellationToken token)
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment[LocalAgMemoryMcpConfiguration.StoragePathVariable] = config.StoragePath;
        environment["AGMEMORY_DATA_DIR"] = Path.Combine(config.StoragePath, ".library-activity");
        environment[LocalAgMemoryMcpConfiguration.ActorIdVariable] = config.Actor.Value;
        environment[LocalAgMemoryMcpConfiguration.TenantIdVariable] = config.Scope.TenantId.Value;
        environment[LocalAgMemoryMcpConfiguration.ProjectIdVariable] = config.Scope.ProjectId!.Value.Value;
        if (config.KnowledgeTeamId is { } team) environment[LocalAgMemoryMcpConfiguration.KnowledgeTeamIdVariable] = team;
        environment[LocalAgMemoryMcpConfiguration.KnowledgeStorageVariable] = config.UseKnowledgeJournal ? "journal" : "legacy";
        var transport = new StdioClientTransport(new()
        {
            Name = "shared-library", Command = "dotnet",
            Arguments = [typeof(LocalAgMemoryMcpRuntime).Assembly.Location],
            InheritEnvironmentVariables = false, EnvironmentVariables = environment
        });
        return McpClient.CreateAsync(transport, cancellationToken: token);
    }
}
