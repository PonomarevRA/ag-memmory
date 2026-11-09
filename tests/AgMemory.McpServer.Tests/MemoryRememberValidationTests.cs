using AgMemory.Contracts;
using AgMemory.McpServer;
using AgMemory.Storage.LanceDb;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Exercises local MCP input identity independently of Core's deduplication behavior.</summary>
public sealed class MemoryRememberValidationTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("999")]
    [InlineData("Fact, Constraint")]
    [InlineData("Unknown")]
    public async Task RejectsNonSymbolicOrUnsupportedTypeBeforeAccessingStorage(string memoryType)
    {
        var directory = NewDirectory();
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration(directory));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => runtime.RememberAsync(
            "Reusable fact.", memoryType, .6d, .8d, null, default));

        Assert.Equal("memoryType", error.ParamName);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task EquivalentWhitespaceEntityOrderAndCaseReplayWithoutReinforcement()
    {
        var directory = NewDirectory();
        try
        {
            await using var runtime = new LocalAgMemoryMcpRuntime(Configuration(directory));
            var first = await runtime.RememberAsync("  Shared   context. ", "fact", .6d, .8d, ["beta", " Alpha "], default);
            var repeated = await runtime.RememberAsync("Shared context.", " Fact ", .6d, .8d, ["alpha", "BETA", "beta"], default);

            Assert.Equal("Created", first.Outcome);
            Assert.Equal("IdempotencyReplay", repeated.Outcome);
            Assert.Equal(1, (await runtime.GetStatusAsync(default)).ActiveMemoryCount);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task HigherImportanceAndConfidenceReachCoreReinforcement()
    {
        var directory = NewDirectory();
        try
        {
            await using var runtime = new LocalAgMemoryMcpRuntime(Configuration(directory));
            await runtime.RememberAsync("Metadata reinforcement marker.", "Fact", .2d, .3d, ["marker"], default);
            var updated = await runtime.RememberAsync("Metadata reinforcement marker.", "Fact", .9d, .95d, ["marker"], default);
            var hit = Assert.Single(await runtime.RecallAsync("reinforcement", 1, default));

            Assert.Equal("Reinforced", updated.Outcome);
            Assert.Equal(.9d, hit.Importance);
            Assert.Equal(.95d, hit.Confidence);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task DelimitedEntityLabelsCannotAliasASecondRequest()
    {
        var directory = NewDirectory();
        try
        {
            await using var runtime = new LocalAgMemoryMcpRuntime(Configuration(directory));
            await runtime.RememberAsync("Delimiter marker.", "Fact", .6d, .8d, ["one|two"], default);
            var second = await runtime.RememberAsync("Delimiter marker.", "Fact", .6d, .8d, ["one", "two"], default);

            Assert.Equal("Reinforced", second.Outcome);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task DistinctConfiguredClientsContributeTheirOwnProvenance()
    {
        var directory = NewDirectory();
        var configuration = Configuration(directory);
        try
        {
            await using (var codex = new LocalAgMemoryMcpRuntime(configuration))
                await codex.RememberAsync("Shared client marker.", "Fact", .6d, .8d, null, default);
            await using (var cursor = new LocalAgMemoryMcpRuntime(configuration with { OriginClient = "cursor" }))
                Assert.Equal("Reinforced", (await cursor.RememberAsync("Shared client marker.", "Fact", .6d, .8d, null, default)).Outcome);

            await using var store = new LanceDbMemoryStore(new(directory));
            var record = Assert.Single(await store.ListAsync(new AuthorizedScopeSet([new ScopeSelector(configuration.Scope)]), default));
            Assert.Contains(record.Provenance.Evidence, evidence => evidence.Identity.StartsWith("codex-mcp:", StringComparison.Ordinal));
            Assert.Contains(record.Provenance.Evidence, evidence => evidence.Identity.StartsWith("cursor-mcp:", StringComparison.Ordinal));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task RecallAndStatusExcludeAnotherExactScopeInTheSameStore()
    {
        var directory = NewDirectory();
        var configuration = Configuration(directory);
        try
        {
            await using (var first = new LocalAgMemoryMcpRuntime(configuration))
                await first.RememberAsync("Visible isolation marker.", "Fact", .6d, .8d, null, default);
            await using (var second = new LocalAgMemoryMcpRuntime(configuration with { Scope = configuration.Scope with { WorkspaceId = new("other-workspace") } }))
                await second.RememberAsync("Hidden isolation marker.", "Fact", .6d, .8d, null, default);
            await using var reader = new LocalAgMemoryMcpRuntime(configuration);

            Assert.Equal("Visible isolation marker.", Assert.Single(await reader.RecallAsync("isolation", 5, default)).Content);
            Assert.Equal(1, (await reader.GetStatusAsync(default)).ActiveMemoryCount);
        }
        finally { DeleteDirectory(directory); }
    }

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), $"agmemory-mcp-validation-{Guid.NewGuid():N}");
    private static LocalAgMemoryMcpConfiguration Configuration(string directory) =>
        LocalAgMemoryMcpConfiguration.Create(directory, "test-actor", "test-tenant", projectId: "test-project");
    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
