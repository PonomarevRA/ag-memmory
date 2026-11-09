using AgMemory.McpServer;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Proves that incident lessons can be replayed and shared by configured agents without crossing scope.</summary>
public sealed class AgentIncidentWorkflowTests
{
    /// <summary>Two configured clients recover the same incident and verified solution; exact retries do not add rows.</summary>
    [Fact]
    public async Task SecondAgentCanRecallVerifiedLessonWithoutDuplicatingTheIncident()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agmemory-incident-{Guid.NewGuid():N}");
        var configuration = LocalAgMemoryMcpConfiguration.Create(directory, "incident-agent", "incident-tenant", "incident-project");
        const string incident = "Incident key=reader-stale-v1; operation=read next page; symptom=changed continuation; cause=unknown; status=open.";
        const string lesson = "Lesson key=reader-stale-v1; cause=catalog generation changed; solution=refresh the affected stream; verification=stale-page regression test passed; status=resolved.";
        try
        {
            await using (var agentA = new LocalAgMemoryMcpRuntime(configuration))
            {
                await agentA.RememberAsync(incident, "Incident", .6d, .8d, ["agent-error", "reader"], default);
                var retry = await agentA.RememberAsync(incident, "Incident", .6d, .8d, ["agent-error", "reader"], default);
                Assert.Equal("IdempotencyReplay", retry.Outcome);
                await agentA.RememberAsync(lesson, "LessonLearned", .6d, .8d, ["agent-error", "reader"], default);
            }

            await using var agentB = new LocalAgMemoryMcpRuntime(configuration with { OriginClient = "cursor" });
            var hits = await agentB.RecallAsync("reader-stale-v1", 10, default);
            Assert.Equal(2, hits.Count);
            Assert.Contains(hits, hit => hit.Type == "Incident" && hit.Content.Contains("status=open", StringComparison.Ordinal));
            Assert.Contains(hits, hit => hit.Type == "LessonLearned" && hit.Content.Contains("verification=stale-page regression test passed", StringComparison.Ordinal));
            Assert.Equal(2, (await agentB.GetStatusAsync(default)).ActiveMemoryCount);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Unresolved and workaround entries stay distinguishable and are invisible to another exact project scope.</summary>
    [Fact]
    public async Task WorkaroundIsNotPresentedAsResolvedOrSharedAcrossProjects()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agmemory-incident-{Guid.NewGuid():N}");
        var configuration = LocalAgMemoryMcpConfiguration.Create(directory, "incident-agent", "incident-tenant", "incident-project");
        try
        {
            await using (var writer = new LocalAgMemoryMcpRuntime(configuration))
            {
                await writer.RememberAsync("Incident key=native-timeout-v1; symptom=query timeout; cause=unknown; status=open.", "Incident", .6d, .8d, ["agent-error"], default);
                await writer.RememberAsync("Lesson key=native-timeout-v1; workaround=retry once after availability check; verification=single retry succeeded; cause=unknown; status=workaround; next=investigate native IO.", "LessonLearned", .6d, .8d, ["agent-error"], default);
            }

            await using (var reader = new LocalAgMemoryMcpRuntime(configuration))
            {
                var hits = await reader.RecallAsync("native-timeout-v1", 10, default);
                Assert.Equal(2, hits.Count);
                Assert.Contains(hits, hit => hit.Type == "LessonLearned" && hit.Content.Contains("status=workaround", StringComparison.Ordinal));
                Assert.DoesNotContain(hits, hit => hit.Content.Contains("status=resolved", StringComparison.Ordinal));
            }

            await using var isolated = new LocalAgMemoryMcpRuntime(configuration with { Scope = configuration.Scope with { ProjectId = new("other-project") } });
            Assert.Empty(await isolated.RecallAsync("native-timeout-v1", 10, default));
            Assert.Equal(0, (await isolated.GetStatusAsync(default)).ActiveMemoryCount);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
