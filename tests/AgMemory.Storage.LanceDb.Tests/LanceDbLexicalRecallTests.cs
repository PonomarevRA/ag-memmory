using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using Apache.Arrow;
using lancedb;
using Xunit;

namespace AgMemory.Storage.LanceDb.Tests;

/// <summary>Proves parity with the existing lexical port and the physical work bounds of local projected recall.</summary>
public sealed class LanceDbLexicalRecallTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly MemoryScope Scope = new(new("lexical-tenant"), new("lexical-project"));

    /// <summary>Metadata boosts are deliberately absent; whole-token matches and ordinal IDs determine the old ranks.</summary>
    [Theory]
    [InlineData("alpha beta", 10)]
    [InlineData("ALPHA/alpha_beta", 3)]
    [InlineData("alpha-beta", 1)]
    [InlineData("ёжик café", 10)]
    [InlineData("alphabet", 10)]
    [InlineData("none", 10)]
    [InlineData("!?/_", 10)]
    public async Task ProjectedRecallMatchesExistingLexicalRankingAndMetadata(string query, int limit)
    {
        var path = NewDirectory();
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var records = new[]
            {
                Record("z-full", "alpha beta ёжик", .99, .99, Now),
                Record("a-full", "ALPHA/beta café", .1, .2, Now.AddDays(-1)),
                Record("b-single", "alpha", .9, .9, Now.AddDays(1)),
                Record("c-single", "beta", .8, .8, Now),
                Record("d-substring", "alphabet betamax", .7, .8, Now),
                Record("e-unicode", "ёжик café", .3, .4, Now),
                Record("f-foreign", "alpha beta", .7, .8, Now) with { Scope = Scope with { WorkspaceId = new("foreign") } },
                Record("g-expired", "alpha beta", .7, .8, Now) with { ExpiresAt = Now },
                Record("h-invalid", "alpha beta", .7, .8, Now) with { Status = MemoryLifecycleStatus.Invalid },
                Record("i-future-expiry", "alpha beta", .4, .5, Now) with { ExpiresAt = Now.AddTicks(1), Type = MemoryRecordType.Outcome }
            };
            await WriteAsync(store, records);
            var eligibility = Eligibility();
            await AssertParityAsync(store, eligibility, query, limit);
            await AssertParityAsync(store, eligibility with { Types = new HashSet<MemoryRecordType> { MemoryRecordType.Outcome } }, query, limit);
            var emptyTypes = await store.SearchLexicalRecallAsync(eligibility with { Types = new HashSet<MemoryRecordType>() }, query, limit, default);
            Assert.Empty(emptyTypes.Items);
            Assert.Equal(0, emptyTypes.Work.QueryExecutions);
        }
        finally { DeleteDirectory(path); }
    }

    /// <summary>Deduplication follows the original provider limit, so duplicate keys do not backfill lower-ranked rows.</summary>
    [Fact]
    public async Task DuplicateDeduplicationKeysPreserveTheExistingLimitedProviderBehavior()
    {
        var path = NewDirectory();
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var first = Record("a", "alpha", .2, .3, Now) with { DeduplicationKey = "shared-key" };
            var duplicate = Record("b", "alpha", .9, .9, Now) with { DeduplicationKey = "shared-key" };
            await WriteAsync(store, [first, duplicate, Record("c", "alpha", .7, .8, Now)]);
            await AssertParityAsync(store, Eligibility(), "alpha", 2);
            var page = await store.SearchLexicalRecallAsync(Eligibility(), "alpha", 2, default);
            Assert.Equal("a", Assert.Single(page.Items).Id.Value);
        }
        finally { DeleteDirectory(path); }
    }

    /// <summary>A multi-batch corpus is scanned once with seven columns and no retained candidate set above ten.</summary>
    [Fact]
    public async Task RecallStreamsOneSelectedColumnScanWithBoundedBatchesAndCandidates()
    {
        var path = NewDirectory();
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var records = Enumerable.Range(0, 1031)
                .Select(index => Record($"id-{1030 - index:D5}", "alpha beta reusable context", .7, .8, Now)).ToArray();
            await WriteAsync(store, records);
            var page = await store.SearchLexicalRecallAsync(Eligibility(), "alpha beta", 10, default);

            Assert.Equal(1031, page.Work.RowsScanned);
            Assert.InRange(page.Work.MaximumBatchRows, 1, 256);
            Assert.Equal(10, page.Work.PeakCandidates);
            Assert.Equal(1, page.Work.QueryExecutions);
            Assert.Equal(7, page.Work.SelectedColumnCount);
            Assert.Equal(Enumerable.Range(0, 10).Select(index => $"id-{index:D5}"), page.Items.Select(item => item.Id.Value));
            await AssertParityAsync(store, Eligibility(), "alpha beta", 10);
        }
        finally { DeleteDirectory(path); }
    }

    /// <summary>Invalid unrelated JSON demonstrates that projection never invokes the full MemoryRecord materializer.</summary>
    [Fact]
    public async Task RecallDoesNotReadOrDeserializeUnselectedJsonColumns()
    {
        var path = NewDirectory();
        try
        {
            await using (var store = new LanceDbMemoryStore(new(path)))
                await WriteAsync(store, [Record("json-proof", "alpha projection", .7, .8, Now)]);
            using (var connection = new Connection())
            {
                await connection.Connect(path);
                using var table = await connection.OpenTable("memory_records");
                using var original = await table.Query().ToArrow();
                using var invalidJson = new StringArray.Builder().Append("invalid json is never projected").Build();
                var arrays = original.Schema.FieldsList.Select(field => field.Name is "provenance_json" or "entities_json" or "embedding_json"
                    ? invalidJson : original.Column(field.Name)).ToArray();
                using var corrupted = new RecordBatch(original.Schema, arrays, original.Length);
                await table.MergeInsert("id").WhenMatchedUpdateAll().WhenNotMatchedInsertAll().Execute(corrupted);
            }
            await using var reader = new LanceDbMemoryStore(new(path));
            var page = await reader.SearchLexicalRecallAsync(Eligibility(), "alpha", 5, default);
            Assert.Equal("alpha projection", Assert.Single(page.Items).Content);
        }
        finally { DeleteDirectory(path); }
    }

    /// <summary>Cancellation and invalid bounds stop before opening a physical store.</summary>
    [Fact]
    public async Task InvalidScopeLimitAndCancellationNeverAccessStorage()
    {
        var path = NewDirectory();
        await using var store = new LanceDbMemoryStore(new(path));
        var multiScope = Eligibility() with { AuthorizedScopes = new AuthorizedScopeSet([new(Scope), new(Scope with { WorkspaceId = new("other") })]) };
        await Assert.ThrowsAsync<ArgumentException>(() => store.SearchLexicalRecallAsync(multiScope, "alpha", 5, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SearchLexicalRecallAsync(Eligibility(), "alpha", 11, default));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SearchLexicalRecallAsync(Eligibility(), "alpha", 5, new CancellationToken(true)));
        Assert.False(Directory.Exists(path));
    }

    /// <summary>Uses the unchanged lexical port as an oracle; lexical-only reciprocal rank fusion preserves provider rank.</summary>
    private static async Task AssertParityAsync(LanceDbMemoryStore store, MemorySearchEligibility eligibility, string query, int limit)
    {
        var oldCandidates = await store.SearchAsync(new(eligibility, query, null, null, null, limit), default);
        var expected = oldCandidates.GroupBy(candidate => candidate.Record.DeduplicationKey, StringComparer.Ordinal)
            .Select(group => group.OrderBy(candidate => candidate.ProviderRank).ThenBy(candidate => candidate.Record.Id.Value, StringComparer.Ordinal).First())
            .OrderBy(candidate => candidate.ProviderRank).ThenBy(candidate => candidate.Record.Id.Value, StringComparer.Ordinal).ToArray();
        var actual = await store.SearchLexicalRecallAsync(eligibility, query, limit, default);
        Assert.Equal(expected.Length, actual.Items.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var record = expected[index].Record;
            var item = actual.Items[index];
            Assert.Equal(record.Id, item.Id);
            Assert.Equal(record.Type, item.Type);
            Assert.Equal(record.CanonicalText, item.Content);
            Assert.Equal(record.Importance, item.Importance);
            Assert.Equal(record.Confidence, item.Confidence);
            Assert.Equal(record.UpdatedAt, item.UpdatedAt);
            Assert.Equal(expected[index].ProviderScore, item.Score);
        }
    }

    private static MemorySearchEligibility Eligibility() => new(new AuthorizedScopeSet([new(Scope)]), null, Now);

    /// <summary>BM25 distinguishes rare terms, saturates frequency and normalizes length; foreign/inactive rows cannot alter its scores.</summary>
    [Fact]
    public async Task Bm25UsesOnlyExactEligibleCorpusStatistics()
    {
        var path = NewDirectory();
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            await WriteAsync(store, [Record("a-common", "common", .5, .5, Now),
                Record("b-common", "common filler", .5, .5, Now),
                Record("c-common", "common filler", .5, .5, Now),
                Record("z-rare", "rare", .5, .5, Now)]);
            var lexical = await store.SearchLexicalRecallAsync(Eligibility(), "common rare", 10, default);
            var ranked = await store.SearchRecallCandidatesAsync(Eligibility(), "common rare", 64, true, default);
            Assert.Equal("a-common", lexical.Items[0].Id.Value);
            Assert.Equal("z-rare", ranked.Items[0].Id.Value);
            // Manual Robertson positive-IDF score: N=4, df=1, length=1, avgdl=1.5.
            var expectedRare = Math.Log(1 + 3.5 / 1.5) * 2.2 / (1 + 1.2 * (.25 + .75 / 1.5));
            Assert.Equal(expectedRare, ranked.Items[0].Score, 10);
            Assert.Equal(8, ranked.Work.RowsScanned);
            Assert.Equal(2, ranked.Work.QueryExecutions);
            var excluded = new List<MemoryRecord>();
            for (var index = 0; index < 15; index++)
                excluded.Add(Record("foreign-" + index, "rare", .5, .5, Now) with { Scope = Scope with { WorkspaceId = new("foreign") } });
            excluded.Add(Record("expired", "rare", .5, .5, Now) with { ExpiresAt = Now });
            excluded.Add(Record("invalid", "rare", .5, .5, Now) with { Status = MemoryLifecycleStatus.Invalid });
            await WriteAsync(store, excluded);
            var after = await store.SearchRecallCandidatesAsync(Eligibility(), "common rare", 64, true, default);
            Assert.Equal(ranked.Items, after.Items);
            Assert.Equal(8, after.Work.RowsScanned);
        }
        finally { DeleteDirectory(path); }
    }

    /// <summary>Query normalization stays whole-token and BM25 TF grows sublinearly; long irrelevant text reduces the score.</summary>
    [Fact]
    public async Task Bm25FrequencySaturatesAndDocumentLengthNormalizes()
    {
        var path = NewDirectory();
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            await WriteAsync(store, [Record("single", "ёжик", .5, .5, Now),
                Record("repeat", "ёжик ёжик ёжик", .5, .5, Now),
                Record("long", "ёжик " + string.Join(' ', Enumerable.Repeat("filler", 30)), .5, .5, Now),
                Record("substring", "ёжиковый", .5, .5, Now)]);
            var ranked = await store.SearchRecallCandidatesAsync(Eligibility(), "ЁЖИК/ёжик", 64, true, default);
            Assert.Equal(new[] { "repeat", "single", "long" }, ranked.Items.Select(item => item.Id.Value));
            Assert.True(ranked.Items[0].Score < 3 * ranked.Items[1].Score);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SearchRecallCandidatesAsync(Eligibility(), "ёжик", 65, true, default));
        }
        finally { DeleteDirectory(path); }
    }
    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), $"agmemory-lexical-recall-{Guid.NewGuid():N}");
    private static void DeleteDirectory(string path) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    private static MemoryRecord Record(string id, string text, double importance, double confidence, DateTimeOffset updatedAt) => new(
        new(id), Scope, MemoryRecordType.Fact, MemoryLifecycleStatus.Active, text, null, importance, confidence, 5,
        Now, updatedAt, 1, ["lexical"], new("recall-proof", null, null, null, null, null, null, [new($"evidence-{id}")]),
        null, null, $"dedup-{id}");

    /// <summary>Seeds one atomic corpus including foreign-scope negative controls.</summary>
    private static async Task WriteAsync(LanceDbMemoryStore store, IReadOnlyList<MemoryRecord> records)
    {
        var scopes = new AuthorizedScopeSet(records.Select(record => new ScopeSelector(record.Scope)).Distinct());
        await using var transaction = await store.BeginTransactionAsync(default);
        var results = await transaction.WriteRecordsAsync(scopes, records.Select(record => new ConditionalRecordWrite(record, 0)).ToArray(), default);
        Assert.All(results, result => Assert.True(result.Applied));
        await transaction.CommitAsync(default);
    }
}
