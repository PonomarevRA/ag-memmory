using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.McpServer;
using Xunit;

namespace AgMemory.McpServer.Tests;

public sealed class AgentProtocolAuditTests
{
    [Fact]
    public async Task V2AuditsOneSafeLogicalOperationIncludingValidationFailures()
    {
        var audit = new AuditStore();
        var configuration = LocalAgMemoryMcpConfiguration.Create(Path.Combine(Path.GetTempPath(), "agent-audit-" + Guid.NewGuid().ToString("N")),
            "private-agent", "private-scope") with
        { UsageAudit = LocalMcpUsageAuditConfiguration.Create(true, Convert.ToBase64String(new byte[32])) };
        await using var runtime = new LocalAgMemoryMcpRuntime(configuration, audit);
        await runtime.AgentCapabilitiesAsync();
        await runtime.AgentContextAsync("NeverMatchingPrivateQuery", 2048, default);
        await runtime.AgentResolveAsync("private-reference", default);
        var handoff = await runtime.AgentHandoffAsync(new(["PrivateHandoffMarker completed"], [], [], "Inspect tests"), "private-request", null, default);
        await runtime.AgentDecisionAsync(new("Choose mode", "Local tests", ["v1", "v2"], "v2", "Bounded context", []), "decision-request", null, default);
        var item = handoff.Data!.Memory!;
        await runtime.AgentCorrectAsync(item.CitationRef, item.Revision, "private-correction", null, default);
        Assert.Equal(6, audit.Events.Count);
        Assert.Equal([UsageAuditOperation.Status, UsageAuditOperation.Recall, UsageAuditOperation.Recall,
            UsageAuditOperation.Remember, UsageAuditOperation.Remember, UsageAuditOperation.Remember], audit.Events.Select(item => item.Operation));
        Assert.Equal(UsageAuditOutcome.Failed, audit.Events[2].Outcome);
        var serialized = JsonSerializer.Serialize(audit.Events);
        foreach (var privateValue in new[] { "NeverMatchingPrivateQuery", "private-reference", "PrivateHandoffMarker", "private-request", "private-correction", configuration.StoragePath })
            Assert.DoesNotContain(privateValue, serialized);
    }

    private sealed class AuditStore : IUsageAuditStore
    {
        public List<UsageAuditEvent> Events { get; } = [];
        public Task<UsageAuditAppendResult> AppendAsync(UsageAuditEvent usageEvent, CancellationToken cancellationToken)
        { Events.Add(usageEvent); return Task.FromResult(UsageAuditAppendResult.Appended); }
        public Task<int> DeleteExpiredAsync(DateTimeOffset cutoffExclusiveUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
