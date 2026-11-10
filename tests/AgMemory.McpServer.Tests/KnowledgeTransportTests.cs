using System.Text.Json;
using AgMemory.McpServer;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Checks discovery and serialization through a real isolated stdio MCP process.</summary>
public sealed class KnowledgeTransportTests
{
    /// <summary>Old tools remain discoverable alongside knowledge tools, and optional arguments bind over actual transport.</summary>
    [Fact]
    public async Task DiscoversCompatibleToolSchemasAndRoundTripsWorkingContext()
    {
        var path = Path.Combine(Path.GetTempPath(), "agmemory-knowledge-wire-" + Guid.NewGuid().ToString("N"));
        try
        {
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment[LocalAgMemoryMcpConfiguration.StoragePathVariable] = path;
            environment["AGMEMORY_DATA_DIR"] = Path.Combine(path, ".activity-data");
            environment[LocalAgMemoryMcpConfiguration.ActorIdVariable] = "knowledge-wire-actor";
            environment[LocalAgMemoryMcpConfiguration.TenantIdVariable] = "knowledge-wire-tenant";
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var transport = new StdioClientTransport(new()
            {
                Name = "knowledge-wire", Command = "dotnet", Arguments = [typeof(LocalAgMemoryMcpRuntime).Assembly.Location],
                InheritEnvironmentVariables = false, EnvironmentVariables = environment
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
            var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
            var names = new HashSet<string>(tools.Select(tool => tool.Name), StringComparer.Ordinal);
            string[] legacy = ["memory_status", "memory_remember", "memory_recall", "memory_capabilities", "memory_context", "memory_resolve", "memory_correct", "memory_handoff", "memory_decision"];
            string[] knowledge = ["knowledge_capabilities", "memory_metadata", "memory_procedures", "memory_procedure", "memory_occurrence", "memory_occurrence_update", "memory_application", "memory_present", "memory_feedback", "working_update", "working_context", "memory_review", "memory_warm_exclude"];
            foreach (var name in legacy) Assert.Contains(name, names);
            foreach (var name in knowledge) Assert.Contains(name, names);
            Assert.Contains("memory_skill", names);
            var setup = await Call("memory_skill", new());
            Assert.Equal("2.1.0", setup.GetProperty("Data").GetProperty("Manifest").GetProperty("Version").GetString());
            Assert.Contains("knowledge_capabilities", setup.GetProperty("Data").GetProperty("Content").GetString());
            var discovery = await Call("memory_capabilities", new());
            Assert.Equal("memory_skill", discovery.GetProperty("Data").GetProperty("Skill").GetProperty("Operation").GetString());
            var rejected = await Call("memory_skill", new() { ["resource"] = "../appsettings.json" });
            Assert.Equal("invalid_argument", rejected.GetProperty("Error").GetProperty("Code").GetString());
            var capabilities = await Call("knowledge_capabilities", new());
            var data = capabilities.GetProperty("Data");
            Assert.Equal("caller_labels_unverified", data.GetProperty("TaskIdentity").GetString());
            Assert.False(data.GetProperty("VerifiedSuccess").GetBoolean());
            Assert.False(data.GetProperty("FoundationApproval").GetBoolean());
            foreach (var operation in data.GetProperty("Operations").EnumerateArray()) Assert.Contains(operation.GetString()!, names);
            var update = await Call("working_update", new()
            {
                ["goal"] = "Wire working goal", ["facts"] = new[] { "Wire confirmed fact" },
                ["assumptions"] = Array.Empty<string>(), ["questions"] = Array.Empty<string>(),
                ["nextAction"] = "Inspect fixture", ["expectedRevision"] = 0, ["requestKey"] = "wire_working"
            });
            Assert.Equal(JsonValueKind.Null, update.GetProperty("Error").ValueKind);
            var context = await Call("working_context", new());
            Assert.Equal("Wire working goal", context.GetProperty("Data").GetProperty("WorkingSet").GetProperty("Goal").GetString());
            Assert.Equal(1, context.GetProperty("Data").GetProperty("WorkingSetRevision").GetInt64());
            Assert.Equal("knowledge-working-v2", context.GetProperty("Data").GetProperty("PolicyVersion").GetString());
            Assert.Contains("task_context", names);
            Assert.Contains("task_working_update", names);
            var taskUpdate = await Call("task_working_update", new()
            {
                ["taskKey"] = "wire-task", ["goal"] = "Task wire goal", ["facts"] = Array.Empty<string>(),
                ["assumptions"] = Array.Empty<string>(), ["questions"] = Array.Empty<string>(),
                ["nextAction"] = "Continue task", ["expectedRevision"] = 0, ["requestKey"] = "task_wire"
            });
            Assert.Equal(JsonValueKind.Null, taskUpdate.GetProperty("Error").ValueKind);
            var taskContext = await Call("task_context", new() { ["taskKey"] = "wire-task" });
            Assert.Equal("Task wire goal", taskContext.GetProperty("Data").GetProperty("WorkingSet").GetProperty("Goal").GetString());
            var otherTask = await Call("task_context", new() { ["taskKey"] = "wire-other" });
            Assert.Equal(JsonValueKind.Null, otherTask.GetProperty("Data").GetProperty("WorkingSet").ValueKind);
            var compact = await Call("task_context_compact", new()
            {
                ["taskKey"] = "wire-task", ["query"] = "wire fixture",
                ["queryFacets"] = new[] { "focused fixture" }, ["maxCards"] = 3
            });
            Assert.Equal(1, compact.GetProperty("Data").GetProperty("WorkingSetRevision").GetInt64());
            var tooMany = await Call("task_context_compact", new()
            {
                ["taskKey"] = "wire-task", ["query"] = "wire fixture",
                ["queryFacets"] = new[] { "first", "second", "third", "fourth" }
            });
            Assert.Equal("invalid_argument", tooMany.GetProperty("Error").GetProperty("Code").GetString());

            var note = await Call("memory_temporary_note_create", new()
            {
                ["title"] = "Wire note", ["content"] = "Fixture body", ["requestKey"] = "wire_note",
                ["conversationId"] = "chat-one"
            });
            var noteId = note.GetProperty("Data").GetProperty("NoteId").GetString();
            var listed = await Call("memory_temporary_notes", new());
            var entry = Assert.Single(listed.GetProperty("Data").EnumerateArray());
            Assert.False(entry.TryGetProperty("Content", out _));
            Assert.Empty(entry.GetProperty("Deliveries").EnumerateArray());
            var delivered = await Call("memory_temporary_note_read", new() { ["noteId"] = noteId, ["conversationId"] = "chat-two" });
            var read = delivered.GetProperty("Data");
            Assert.Equal("Fixture body", read.GetProperty("Content").GetString());
            var delivery = Assert.Single(read.GetProperty("Note").GetProperty("Deliveries").EnumerateArray());
            Assert.Equal("chat-two", delivery.GetProperty("Recipient").GetProperty("ConversationId").GetString());

            // Exercise the public JSON schemas and serializer rather than only direct runtime signatures.
            async Task<JsonElement> Call(string name, Dictionary<string, object?> arguments)
            {
                var response = await client.CallToolAsync(name, arguments, cancellationToken: deadline.Token);
                Assert.NotEqual(true, response.IsError);
                var content = Assert.IsType<TextContentBlock>(Assert.Single(response.Content));
                using var json = JsonDocument.Parse(content.Text);
                Assert.Equal("agmemory-agent-v2", json.RootElement.GetProperty("SchemaVersion").GetString());
                return json.RootElement.Clone();
            }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }
}
