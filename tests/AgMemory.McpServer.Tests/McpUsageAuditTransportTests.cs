using System.IO.Pipes;
using System.Text.Json;
using Apache.Arrow;
using AgMemory.Contracts;
using AgMemory.McpServer;
using lancedb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgMemory.McpServer.Tests;

public sealed class McpUsageAuditTransportTests
{
    private static readonly string AuditKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    /// <summary>Checks the complete advertised tool set and one privacy-safe audit event per legacy invocation.</summary>
    [Fact]
    public async Task StdioInvocationsAppendExactlyOnePrivacySafeEventAndPreserveToolResponses()
    {
        var path = TemporaryPath();
        try
        {
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment[LocalAgMemoryMcpConfiguration.StoragePathVariable] = path;
            environment["AGMEMORY_DATA_DIR"] = Path.Combine(path, ".activity-data");
            environment[LocalAgMemoryMcpConfiguration.ActorIdVariable] = "transport-private-actor";
            environment[LocalAgMemoryMcpConfiguration.TenantIdVariable] = "transport-private-tenant";
            environment[LocalAgMemoryMcpConfiguration.ClientIdVariable] = "cursor";
            environment[LocalMcpUsageAuditConfiguration.EnabledVariable] = "true";
            environment[LocalMcpUsageAuditConfiguration.HmacKeyVariable] = AuditKey;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var transport = new StdioClientTransport(new()
            {
                Name = "agmemory-audit-acceptance",
                Command = "dotnet",
                Arguments = [typeof(LocalAgMemoryMcpRuntime).Assembly.Location],
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment
            });
            await using (var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token))
            {
                var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
                string[] expectedTools =
                [
                    "knowledge_capabilities", "memory_application", "memory_capabilities", "memory_claim", "memory_claims", "memory_context", "memory_correct",
                    "memory_decision", "memory_evidence", "memory_feedback", "memory_handoff", "memory_metadata", "memory_occurrence",
                    "memory_occurrence_update", "memory_present", "memory_procedure", "memory_procedure_discovery", "memory_procedure_facets", "memory_procedures", "memory_recall",
                    "memory_remember", "memory_resolve", "memory_review", "memory_skill", "memory_status", "memory_warm_exclude",
                    "task_context", "task_working_update", "working_context", "working_update"
                ];
                Assert.Equal(expectedTools, tools.Select(tool => tool.Name).Order().ToArray());
                Assert.All(tools, tool =>
                {
                    var schema = tool.JsonSchema.GetRawText();
                    Assert.DoesNotContain("usageAudit", schema, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("areaId", schema, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("actor", schema, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("scope", schema, StringComparison.OrdinalIgnoreCase);
                });

                var remember = await client.CallToolAsync("memory_remember", new Dictionary<string, object?> { ["content"] = "Transport reusable secretless marker Orion-42." }, cancellationToken: deadline.Token);
                Assert.NotEqual(true, remember.IsError);
                Assert.Contains("Created", ResponseText(remember), StringComparison.Ordinal);
                var recall = await client.CallToolAsync("memory_recall", new Dictionary<string, object?> { ["query"] = "Orion", ["clientLabel"] = "claude", ["areaId"] = "unknown-area" }, cancellationToken: deadline.Token);
                Assert.NotEqual(true, recall.IsError);
                Assert.Contains("Orion-42", ResponseText(recall), StringComparison.Ordinal);
                Assert.DoesNotContain("RowsScanned", ResponseText(recall), StringComparison.OrdinalIgnoreCase);
                var empty = await client.CallToolAsync("memory_recall", new Dictionary<string, object?> { ["query"] = "NeverMatchingTransportMarker" }, cancellationToken: deadline.Token);
                Assert.NotEqual(true, empty.IsError);
                Assert.Equal("[]", ResponseText(empty));
                var invalid = await client.CallToolAsync("memory_recall", new Dictionary<string, object?> { ["query"] = "" }, cancellationToken: deadline.Token);
                Assert.True(invalid.IsError);
                var status = await client.CallToolAsync("memory_status", cancellationToken: deadline.Token);
                Assert.NotEqual(true, status.IsError);
                Assert.Contains("\"ActiveMemoryCount\":1", ResponseText(status), StringComparison.Ordinal);
            }

            using var connection = new Connection();
            await connection.Connect(path);
            using var table = await connection.OpenTable("usage_audit_events");
            var rows = await table.Query().ToArrow();
            Assert.Equal(5, rows.Length);
            Assert.Equal(3, Enumerable.Range(0, rows.Length).Count(index => Cell(rows, "operation", index) == "Recall"));
            Assert.Equal(1, Enumerable.Range(0, rows.Length).Count(index => Cell(rows, "operation", index) == "Remember"));
            Assert.Equal(1, Enumerable.Range(0, rows.Length).Count(index => Cell(rows, "operation", index) == "Status"));
            Assert.Equal(1, Enumerable.Range(0, rows.Length).Count(index => Cell(rows, "outcome", index) == "Failed"));
            Assert.Equal(5, Enumerable.Range(0, rows.Length).Select(index => Cell(rows, "event_id", index)).Distinct().Count());
            for (var index = 0; index < rows.Length; index++)
            {
                Assert.Equal("Cursor", Cell(rows, "client_label", index));
                Assert.True(string.IsNullOrEmpty(Cell(rows, "area_id", index)));
                foreach (var field in rows.Schema.FieldsList)
                {
                    var value = Cell(rows, field.Name, index);
                    Assert.DoesNotContain("transport-private", value, StringComparison.Ordinal);
                    Assert.DoesNotContain("Orion-42", value, StringComparison.Ordinal);
                    Assert.DoesNotContain("NeverMatchingTransportMarker", value, StringComparison.Ordinal);
                    Assert.DoesNotContain(AuditKey, value, StringComparison.Ordinal);
                }
            }
        }
        finally { DeletePath(path); }
    }

    [Fact]
    public async Task RealMcpStreamKeepsResponsesAndErrorsWhenAuditStorageFails()
    {
        var path = TemporaryPath();
        // macOS domain socket paths include a long temporary-directory prefix.
        var pipeName = $"agm-{Guid.NewGuid():N}"[..20];
        try
        {
            var failing = new FailingAuditStore();
            var configuration = LocalAgMemoryMcpConfiguration.Create(path, "actor", "tenant")
                with { UsageAudit = LocalMcpUsageAuditConfiguration.Create(true, AuditKey) };
            await using var runtime = new LocalAgMemoryMcpRuntime(configuration, failing);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            var connection = serverPipe.WaitForConnectionAsync(deadline.Token);
            await clientPipe.ConnectAsync(deadline.Token);
            await connection;
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(runtime);
            builder.Services.AddMcpServer().WithStreamServerTransport(serverPipe, serverPipe).WithTools<AgMemoryTools>();
            using var host = builder.Build();
            await host.StartAsync(deadline.Token);
            await using (var client = await McpClient.CreateAsync(new StreamClientTransport(clientPipe, clientPipe), cancellationToken: deadline.Token))
            {
                var status = await client.CallToolAsync("memory_status", cancellationToken: deadline.Token);
                Assert.NotEqual(true, status.IsError);
                Assert.Contains("\"ActiveMemoryCount\":0", ResponseText(status), StringComparison.Ordinal);
                var invalid = await client.CallToolAsync("memory_recall", new Dictionary<string, object?> { ["query"] = "", ["limit"] = 5 }, cancellationToken: deadline.Token);
                Assert.True(invalid.IsError);
                var empty = await client.CallToolAsync("memory_recall", new Dictionary<string, object?> { ["query"] = "empty" }, cancellationToken: deadline.Token);
                Assert.NotEqual(true, empty.IsError);
                Assert.Equal("[]", ResponseText(empty));
                Assert.Equal(3, failing.AppendAttempts);
            }
            await host.StopAsync(deadline.Token);
        }
        finally { DeletePath(path); }
    }

    private static string ResponseText(CallToolResult response) => string.Concat(response.Content.OfType<TextContentBlock>().Select(block => block.Text));
    private static string Cell(RecordBatch rows, string column, int index) => ((StringArray)rows.Column(column)).GetString(index) ?? "";
    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"agmemory-mcp-transport-{Guid.NewGuid():N}");
    private static void DeletePath(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }

    /// <summary>Simulates unavailable audit storage while the actual runtime's memory store remains healthy.</summary>
    private sealed class FailingAuditStore : IUsageAuditStore
    {
        public int AppendAttempts { get; private set; }
        public Task<UsageAuditAppendResult> AppendAsync(UsageAuditEvent usageEvent, CancellationToken cancellationToken)
        {
            AppendAttempts++;
            return Task.FromException<UsageAuditAppendResult>(new IOException("Audit storage unavailable."));
        }
        public Task<int> DeleteExpiredAsync(DateTimeOffset cutoffExclusiveUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
