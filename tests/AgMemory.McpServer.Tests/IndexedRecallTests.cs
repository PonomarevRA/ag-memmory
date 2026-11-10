using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Checks index/scan parity and real cache use, including source-generation invalidation and legacy contracts.</summary>
public sealed class IndexedRecallTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "agmemory-postings-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private LocalAgMemoryMcpConfiguration Config => LocalAgMemoryMcpConfiguration.Create(_path, "index-reader", "index-tenant");

    /// <summary>Indexed scores and IDs match the scan for exact eligibility; a foreign corpus cannot change BM25 IDF.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexParityExpiryTypesAndNativeVersionInvalidation(bool bm25)
    {
        await using var store = new LanceDbMemoryStore(new(_path));
        var scope = new AuthorizedScopeSet([new ScopeSelector(Config.Scope)]);
        var records = new[] { Record("a", "alpha beta alpha"), Record("b", "beta filler"), Record("c", "rare"),
            Record("expired", "rare") with { ExpiresAt = _now }, Record("invalid", "rare") with { Status = MemoryLifecycleStatus.Invalid },
            Record("foreign", "rare") with { Scope = Config.Scope with { WorkspaceId = new("foreign") } } };
        await WriteAsync(store, records);
        var eligibility = new MemorySearchEligibility(scope, null, _now);
        foreach (var query in new[] { "rare alpha beta", "alpha", "unknown", "BETA alpha beta" })
        {
            var expected = await store.SearchRecallCandidatesAsync(eligibility, query, 64, bm25, default);
            var actual = await store.SearchIndexedRecallCandidatesAsync(eligibility, query, 64, bm25, default);
            Assert.Equal("sqlite-postings-v1", actual.Work.Provider);
            Assert.Equal(expected.Items, actual.Items);
            var filtered = eligibility with { Types = new HashSet<MemoryRecordType> { MemoryRecordType.Outcome } };
            Assert.Empty((await store.SearchIndexedRecallCandidatesAsync(filtered, query, 64, bm25, default)).Items);
        }
        Assert.Equal(2, (await store.SearchIndexedRecallCandidatesAsync(eligibility, "rare", 64, bm25, default)).Work.RowsScanned);
        await WriteAsync(store, [Record("new", "rare alpha")]);
        var rebuilt = await store.SearchIndexedRecallCandidatesAsync(eligibility, "rare", 64, bm25, default);
        Assert.Contains(rebuilt.Items, item => item.Id.Value == "new");
        Assert.True(rebuilt.Work.IndexRebuilt);
        Assert.Equal((await store.SearchRecallCandidatesAsync(eligibility, "rare", 64, bm25, default)).Items, rebuilt.Items);
    }

    /// <summary>Warm recall fetches all retained documents in one statement without changing rank or the pre-dedup cutoff.</summary>
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 5)]
    public async Task WarmIndexedRecallBatchesRankedDocuments(bool bm25, int expectedQueries)
    {
        await using var store = new LanceDbMemoryStore(new(_path));
        var scope = new AuthorizedScopeSet([new ScopeSelector(Config.Scope)]);
        var eligibility = new MemorySearchEligibility(scope, null, _now);
        var records = new List<MemoryRecord>();
        for (var index = 0; index < 70; index++)
        {
            var record = Record($"candidate-{index:D2}", "broad candidate");
            records.Add(index < 2 ? record with { DeduplicationKey = "shared" } : record);
        }
        await WriteAsync(store, records);
        await store.SearchIndexedRecallCandidatesAsync(eligibility, "broad", 64, bm25, default);

        var warm = await store.SearchIndexedRecallCandidatesAsync(eligibility, "broad", 64, bm25, default);
        var scan = await store.SearchRecallCandidatesAsync(eligibility, "broad", 64, bm25, default);
        Assert.Equal("sqlite-postings-v1", warm.Work.Provider);
        Assert.False(warm.Work.IndexRebuilt);
        Assert.Equal(expectedQueries, warm.Work.QueryExecutions);
        Assert.Equal(scan.Items, warm.Items);

        var miss = await store.SearchIndexedRecallCandidatesAsync(eligibility, "missing", 64, bm25, default);
        Assert.Empty(miss.Items);
        Assert.Equal(expectedQueries - 2, miss.Work.QueryExecutions);
    }

    /// <summary>Alias channels deduplicate logical keys even when their record IDs differ; long legacy queries keep working.</summary>
    [Fact]
    public async Task FusionPreservesDedupAndLegacyLongQuery()
    {
        await using (var store = new LanceDbMemoryStore(new(_path)))
            await WriteAsync(store, [Record("a", "AFTER acronym source") with { DeduplicationKey = "shared" },
                Record("b", "procedure transfer source") with { DeduplicationKey = "shared" }]);
        await using var runtime = new LocalAgMemoryMcpRuntime(Config);
        Assert.Single(await runtime.RecallAsync("AFTER", 10, default));
        var longQuery = "AFTER " + new string('x', 1001);
        Assert.Single(await runtime.RecallAsync(longQuery, 10, default));
        await using var literal = new LocalAgMemoryMcpRuntime(Config with { UseQueryAliases = false, UseRecallIndex = false });
        Assert.Single(await literal.RecallAsync(longQuery, 10, default));
    }

    /// <summary>A copied cache from another exact scope must rebuild; a forged derived row must never bypass canonical eligibility.</summary>
    [Fact]
    public async Task WrongScopeCacheAndCorruptDerivedCandidatesCannotLeakThroughLegacyRecall()
    {
        await using var store = new LanceDbMemoryStore(new(_path));
        var foreignScope = Config.Scope with { WorkspaceId = new("foreign") };
        await WriteAsync(store, [Record("own", "alpha own"), Record("foreign", "foreignsecret") with { Scope = foreignScope }]);
        var own = new MemorySearchEligibility(new AuthorizedScopeSet([new ScopeSelector(Config.Scope)]), null, _now);
        var foreign = own with { AuthorizedScopes = new([new ScopeSelector(foreignScope)]) };
        await store.SearchIndexedRecallCandidatesAsync(own, "alpha", 64, false, default);
        await store.SearchIndexedRecallCandidatesAsync(foreign, "foreignsecret", 64, false, default);
        var ownCache = FindCache("own");
        File.Copy(FindCache("foreign"), ownCache, true);
        Assert.Empty((await store.SearchIndexedRecallCandidatesAsync(own, "foreignsecret", 64, false, default)).Items);
        Assert.Equal("own", Assert.Single((await store.SearchIndexedRecallCandidatesAsync(own, "alpha", 64, false, default)).Items).Id.Value);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ownCache, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO documents SELECT 'foreign','Fact','foreignsecret',.5,.8,updated,'forged','Active',NULL,1 FROM documents LIMIT 1;
                INSERT INTO postings VALUES('FOREIGNSECRET','foreign',1,0);
                """;
            command.ExecuteNonQuery();
        }
        await using var runtime = new LocalAgMemoryMcpRuntime(Config);
        Assert.Empty(await runtime.RecallAsync("foreignsecret", 10, default));
        Assert.Empty((await runtime.AgentContextAsync("foreignsecret", 8192, default)).Data!.Items);
    }

    /// <summary>A single current projection rechecks lifecycle/type/expiry/text and reports the actual current revision.</summary>
    [Fact]
    public async Task BatchSourcesRecheckEligibilityAndRevisionAfterRanking()
    {
        await using var store = new LanceDbMemoryStore(new(_path));
        var initial = new[] { Record("a", "alpha a"), Record("b", "alpha b"), Record("c", "alpha c"), Record("d", "alpha d"), Record("e", "alpha e") };
        await WriteAsync(store, initial);
        var scopes = new AuthorizedScopeSet([new ScopeSelector(Config.Scope)]);
        var eligibility = new MemorySearchEligibility(scopes, new HashSet<MemoryRecordType> { MemoryRecordType.Fact }, _now);
        var ranked = await store.SearchRecallCandidatesAsync(eligibility, "alpha", 64, false, default);
        var updated = initial.Select(record => record with { Version = 2 }).ToArray();
        updated[0] = updated[0] with { Status = MemoryLifecycleStatus.Invalid };
        updated[1] = updated[1] with { ExpiresAt = _now };
        updated[2] = updated[2] with { Type = MemoryRecordType.Procedure };
        updated[3] = updated[3] with { CanonicalText = "Changed source" };
        await using (var transaction = await store.BeginTransactionAsync(default))
        {
            Assert.All(await transaction.WriteRecordsAsync(scopes, updated.Select(record => new ConditionalRecordWrite(record, 1)).ToArray(), default), item => Assert.True(item.Applied));
            await transaction.CommitAsync(default);
        }
        var current = Assert.Single(await store.ReadCurrentRecallSourcesAsync(eligibility, ranked.Items, default));
        Assert.Equal("e", current.Id.Value); Assert.Equal(2, current.Revision);
    }

    /// <summary>Identifies this isolated fixture's cache without depending on private scope-hash implementation.</summary>
    private string FindCache(string sourceId)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_path, ".agmemory-recall-index"), "*.sqlite"))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM documents WHERE id=$id";
            command.Parameters.AddWithValue("$id", sourceId);
            if (Convert.ToInt64(command.ExecuteScalar()) > 0) return file;
        }
        throw new InvalidOperationException("Fixture cache is missing.");
    }

    /// <summary>Denied cache writes cannot disable a readable canonical store; the operator can still retrieve through the scan fallback.</summary>
    [Fact]
    public async Task UnwritableDerivedCacheFallsBackToCanonicalRecall()
    {
        if (OperatingSystem.IsWindows()) return;
        var cache = Path.Combine(_path, ".agmemory-recall-index");
        try
        {
            await using (var seed = new LocalAgMemoryMcpRuntime(Config with { UseRecallIndex = false }))
                await seed.RememberAsync("alpha cache permission fixture", "Fact", .5, .8, null, default);
            Directory.CreateDirectory(cache);
            File.SetUnixFileMode(cache, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            await using var runtime = new LocalAgMemoryMcpRuntime(Config);
            Assert.Single(await runtime.RecallAsync("alpha", 10, default));
        }
        finally { if (Directory.Exists(cache)) File.SetUnixFileMode(cache, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    private MemoryRecord Record(string id, string text) => new(new(id), Config.Scope, MemoryRecordType.Fact,
        MemoryLifecycleStatus.Active, text, null, .5, .8, 5, _now, _now, 1, [],
        new("index-fixture", null, null, null, null, null, null, [new("fixture-" + id)]), null, null, "dedup-" + id);

    /// <summary>Writes all scopes explicitly through the canonical transaction, never directly into index tables.</summary>
    private static async Task WriteAsync(LanceDbMemoryStore store, IReadOnlyList<MemoryRecord> records)
    {
        await using var transaction = await store.BeginTransactionAsync(default);
        var scopes = new AuthorizedScopeSet(records.Select(record => new ScopeSelector(record.Scope)).Distinct());
        Assert.All(await transaction.WriteRecordsAsync(scopes, records.Select(record => new ConditionalRecordWrite(record, 0)).ToArray(), default), result => Assert.True(result.Applied));
        await transaction.CommitAsync(default);
    }

    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }
}
