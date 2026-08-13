using AgMemory.McpServer;
using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using Xunit;

namespace AgMemory.McpServer.Tests;

public sealed class LocalAgMemoryMcpRuntimeTests
{
    [Fact]
    public async Task RemembersAndRecallsOnlyItsConfiguredLocalScope()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agmemory-mcp-{Guid.NewGuid():N}");
        var configuration = LocalAgMemoryMcpConfiguration.Create(
            directory, "codex-test", "tenant-test", projectId: "project-test");
        await using var runtime = new LocalAgMemoryMcpRuntime(configuration);

        var receipt = await runtime.RememberAsync(
            "The Codex memory test marker is Orion-42.", "Fact", .8d, .9d, ["test-memory"], CancellationToken.None);
        var hits = await runtime.RecallAsync("Which marker is Orion?", 5, CancellationToken.None);
        var status = await runtime.GetStatusAsync(CancellationToken.None);

        Assert.Equal("Created", receipt.Outcome);
        var hit = Assert.Single(hits);
        Assert.Equal("Fact", hit.Type);
        Assert.Equal("The Codex memory test marker is Orion-42.", hit.Content);
        Assert.Equal(1, status.ActiveMemoryCount);
    }

    [Fact]
    public void ConfigurationRejectsImplicitIdentityOrScope()
    {
        Assert.Throws<InvalidOperationException>(() => LocalAgMemoryMcpConfiguration.Create(null, "actor", "tenant"));
        Assert.Throws<InvalidOperationException>(() => LocalAgMemoryMcpConfiguration.Create("/tmp/store", null, "tenant"));
        Assert.Throws<InvalidOperationException>(() => LocalAgMemoryMcpConfiguration.Create("/tmp/store", "actor", null));
    }

    [Fact]
    public async Task RecallUsesCoreRetrievalAndConfiguredOriginClientForProvenance()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agmemory-mcp-{Guid.NewGuid():N}");
        try
        {
            var configuration = LocalAgMemoryMcpConfiguration.Create(
                directory, "cursor-test", "tenant-test", originClient: "cursor");
            await using (var runtime = new LocalAgMemoryMcpRuntime(configuration))
            {
                await runtime.RememberAsync("The shared handoff protocol uses compact citations.", "Fact", .8d, .9d, null, default);
                var hits = await runtime.RecallAsync("Which protocol uses citations?", 1, default);

                Assert.Equal("The shared handoff protocol uses compact citations.", Assert.Single(hits).Content);
            }

            await using var store = new LanceDbMemoryStore(new(configuration.StoragePath));
            var records = await store.ListAsync(new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]), default);
            Assert.Equal("cursor-mcp", Assert.Single(records).Provenance.SourceSystem);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsDecisionMemoryBecauseTheMcpToolCannotSupplyItsRequiredTrace()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agmemory-mcp-{Guid.NewGuid():N}");
        try
        {
            var configuration = LocalAgMemoryMcpConfiguration.Create(directory, "codex-test", "tenant-test");
            await using var runtime = new LocalAgMemoryMcpRuntime(configuration);

            var error = await Assert.ThrowsAsync<ArgumentException>(() => runtime.RememberAsync(
                "Adopt the new policy.", "Decision", .8d, .9d, null, CancellationToken.None));

            Assert.Equal("memoryType", error.ParamName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
