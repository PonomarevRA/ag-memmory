using Agm.Memory.Abstractions;
using Agm.Memory.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Agm.Memory.Tests;

public sealed class SqliteMemoryIntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"agm-memory-{Guid.NewGuid():N}");

    [Fact]
    public async Task EmptyDatabase_CanBeInitializedAndUsedByStandaloneConsumer()
    {
        Directory.CreateDirectory(directory);
        var source = Source();
        await SqliteMemorySchema.InitializeAsync(source);
        await SqliteMemorySchema.InitializeAsync(source);
        var workspaceId = Guid.NewGuid();
        var service = new SqliteWorkspaceMemoryService(source, TimeProvider.System);

        var initial = await service.GetAsync(workspaceId, CancellationToken.None);
        var written = await service.WriteAsync(
            workspaceId,
            "Architecture decision\ntoken=must-not-survive",
            "consumer-write",
            [],
            0,
            CancellationToken.None);
        var injection = await service.GetInjectionAsync(workspaceId, CancellationToken.None);

        Assert.NotNull(initial);
        Assert.Equal(0, initial.SettingsVersion);
        Assert.Equal(WorkspaceMemoryWriteOutcome.Updated, written.Outcome);
        Assert.Equal(1, written.Snapshot?.Version);
        Assert.Contains("Architecture decision", injection?.Content);
        Assert.DoesNotContain("must-not-survive", injection?.Content);
    }

    [Fact]
    public async Task StandaloneFanOutStore_DoesNotRequireAgmScopeTables()
    {
        Directory.CreateDirectory(directory);
        var source = Source();
        await SqliteMemorySchema.InitializeAsync(source);
        var store = new SqliteFanOutMemoryStore(source, TimeProvider.System);
        var scope = new FanOutMemoryScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var result = await store.AppendAsync(
            scope, "Reusable context", "capture-1", 0, TimeSpan.FromMinutes(5),
            CancellationToken.None);
        var replay = await store.AppendAsync(
            scope, "Ignored duplicate", "capture-1", 1, TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.Equal(FanOutMemoryWriteOutcome.Updated, result.Outcome);
        Assert.Equal(FanOutMemoryWriteOutcome.Unchanged, replay.Outcome);
        Assert.Equal("Reusable context", replay.Entry?.Content);
    }

    [Fact]
    public async Task HostValidator_CanRejectForeignScopes()
    {
        Directory.CreateDirectory(directory);
        var source = Source();
        await SqliteMemorySchema.InitializeAsync(source);
        var validator = new RejectingScopeValidator();
        var workspace = new SqliteWorkspaceMemoryService(source, TimeProvider.System, validator);
        var fanOut = new SqliteFanOutMemoryStore(source, TimeProvider.System, validator);

        Assert.Null(await workspace.GetAsync(Guid.NewGuid(), CancellationToken.None));
        var result = await fanOut.AppendAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            "context", null, 0, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Equal(FanOutMemoryWriteOutcome.Conflict, result.Outcome);
    }

    private SqliteConnectionStringSource Source() => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "memory.db"),
            ForeignKeys = true
        }.ToString());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class RejectingScopeValidator : IMemoryScopeValidator
    {
        public ValueTask<bool> WorkspaceExistsAsync(
            Guid workspaceId, CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public ValueTask<bool> FanOutScopeExistsAsync(
            FanOutMemoryScope scope, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}
