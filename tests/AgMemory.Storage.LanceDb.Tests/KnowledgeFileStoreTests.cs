using AgMemory.Contracts;
using Xunit;

namespace AgMemory.Storage.LanceDb.Tests;

/// <summary>Exercises the separate knowledge sidecar using only isolated temporary roots.</summary>
public sealed class KnowledgeFileStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "agmemory-knowledge-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>A new instance recovers the committed generation and its idempotency receipt together.</summary>
    [Fact]
    public async Task Reopen_PreservesStateAndReceipt_AndStaleReplayDoesNotOverwrite()
    {
        var store = new KnowledgeFileStore(_path);
        Assert.Equal(0, (await store.ReadAsync("server-profile", default)).Version);
        var state = State(1);
        state.Requests.Add("request", new("request", "fingerprint", "procedure", "procedure-1", 1, DateTimeOffset.UnixEpoch));
        Assert.True(await store.CompareExchangeAsync("server-profile", 0, state, default));

        var reopened = new KnowledgeFileStore(_path);
        var persisted = await reopened.ReadAsync("server-profile", default);
        Assert.Equal(1, persisted.Version);
        Assert.Equal("fingerprint", persisted.Requests["request"].Fingerprint);
        Assert.False(await reopened.CompareExchangeAsync("server-profile", 0, State(1), default));
        Assert.Single((await reopened.ReadAsync("server-profile", default)).Requests);
        Assert.Equal(0, (await reopened.ReadAsync("different-profile", default)).Version);
    }

    /// <summary>Concurrent instances cannot both publish the same predecessor version.</summary>
    [Fact]
    public async Task TwoInstances_AdmitExactlyOneCompareExchange()
    {
        var first = new KnowledgeFileStore(_path);
        var second = new KnowledgeFileStore(_path);
        var attempts = await Task.WhenAll(
            Task.Run(() => first.CompareExchangeAsync("profile", 0, State(1), default)),
            Task.Run(() => second.CompareExchangeAsync("profile", 0, State(1), default)));
        Assert.Equal(1, attempts.Count(success => success));
        Assert.Equal(1, (await second.ReadAsync("profile", default)).Version);
    }

    /// <summary>A failed oversized publication leaves the preceding complete document intact and removes staging files.</summary>
    [Fact]
    public async Task OversizedReplacement_PreservesOldDocumentAndCleansTemporaryFile()
    {
        var store = new KnowledgeFileStore(_path, new() { MaximumDocumentBytes = 1024 });
        Assert.True(await store.CompareExchangeAsync("profile", 0, State(1), default));
        var oversized = State(2) with { WorkingSet = new(1, new string('x', 2048), [], [], [], "next", DateTimeOffset.MaxValue) };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CompareExchangeAsync("profile", 1, oversized, default));
        Assert.Equal(1, (await store.ReadAsync("profile", default)).Version);
        Assert.Empty(Directory.GetFiles(Path.Combine(_path, ".agmemory-knowledge"), "*.tmp"));
    }

    /// <summary>Malformed data and unknown schemas fail closed instead of silently replacing durable knowledge.</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"SchemaVersion\":2,\"Version\":1}")]
    [InlineData("{\"SchemaVersion\":1,\"Version\":1,\"Metadata\":null}")]
    [InlineData("{\"SchemaVersion\":1,\"Version\":1,\"ProcedureHistory\":null}")]
    [InlineData("{\"SchemaVersion\":1,\"Version\":1,\"ProcedureHistory\":{\"broken\":null}}")]
    public async Task CorruptOrFutureDocument_IsRejected(string json)
    {
        var store = new KnowledgeFileStore(_path);
        await store.CompareExchangeAsync("profile", 0, State(1), default);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_path, ".agmemory-knowledge"), "*.json"));
        await File.WriteAllTextAsync(file, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync("profile", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CompareExchangeAsync("profile", 1, State(2), default));
        Assert.Equal(json, await File.ReadAllTextAsync(file));
    }

    /// <summary>Cancellation before entry does not create or change persistent state.</summary>
    [Fact]
    public async Task CancelledOperations_LeaveStateUnchanged()
    {
        var store = new KnowledgeFileStore(_path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync("profile", cancelled.Token));
        Assert.False(Directory.Exists(_path));
        await store.CompareExchangeAsync("profile", 0, State(1), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompareExchangeAsync("profile", 1, State(2), cancelled.Token));
        Assert.Equal(1, (await store.ReadAsync("profile", default)).Version);
    }

    /// <summary>An abandoned staging generation is discarded and never mistaken for committed state.</summary>
    [Fact]
    public async Task AbandonedTemporaryFile_IsDiscardedOnReopen()
    {
        var store = new KnowledgeFileStore(_path);
        await store.CompareExchangeAsync("profile", 0, State(1), default);
        var document = Assert.Single(Directory.GetFiles(Path.Combine(_path, ".agmemory-knowledge"), "*.json"));
        var abandoned = document + ".interrupted.tmp";
        await File.WriteAllTextAsync(abandoned, "{ incomplete generation");
        Assert.Equal(1, (await new KnowledgeFileStore(_path).ReadAsync("profile", default)).Version);
        Assert.False(File.Exists(abandoned));
    }

    /// <summary>Document, entry and profile bounds are enforced before replacing a valid generation.</summary>
    [Fact]
    public async Task BoundsAndVersion_AreEnforced()
    {
        var store = new KnowledgeFileStore(_path, new() { MaximumProfiles = 1, MaximumEntries = 1 });
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompareExchangeAsync("profile", 0, State(2), default));
        Assert.True(await store.CompareExchangeAsync("profile", 0, State(1), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CompareExchangeAsync("other", 0, State(1), default));
        var tooMany = State(2);
        tooMany.Warm.Add("one", new("one", 1, DateTimeOffset.MaxValue));
        tooMany.Warm.Add("two", new("two", 1, DateTimeOffset.MaxValue));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CompareExchangeAsync("profile", 1, tooMany, default));
        var document = Assert.Single(Directory.GetFiles(Path.Combine(_path, ".agmemory-knowledge"), "*.json"));
        await File.WriteAllTextAsync(document, new string('x', 8 * 1024 * 1024 + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync("profile", default));
    }

    /// <summary>Root and document symlinks fail before any read or write follows the link.</summary>
    [Fact]
    public async Task SymlinkRootsAndDocuments_AreRejected_AndFilesArePrivate()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_path);
        var target = Path.Combine(_path, "actual");
        Directory.CreateDirectory(target);
        var linkedRoot = Path.Combine(_path, "linked");
        Directory.CreateSymbolicLink(linkedRoot, target);
        await Assert.ThrowsAsync<IOException>(() => new KnowledgeFileStore(linkedRoot).ReadAsync("profile", default));
        var store = new KnowledgeFileStore(target);
        await store.CompareExchangeAsync("profile", 0, State(1), default);
        var directory = Path.Combine(target, ".agmemory-knowledge");
        var document = Assert.Single(Directory.GetFiles(directory, "*.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(document));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        var original = document + ".original";
        File.Move(document, original);
        File.CreateSymbolicLink(document, original);
        await Assert.ThrowsAsync<IOException>(() => store.ReadAsync("profile", default));
        await Assert.ThrowsAsync<IOException>(() => store.CompareExchangeAsync("profile", 1, State(2), default));
    }

    private static KnowledgeState State(long version) => new() { Version = version };

    /// <summary>Deletes only this fixture's disposable storage root.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
    }
}
