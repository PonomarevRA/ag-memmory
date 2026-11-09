using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using AgMemory.McpServer;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Checks useful retrieval against independent expected answers in isolated stores, including warm distractors and tight budgets.</summary>
public sealed class ContextEffectivenessTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "agmemory-effectiveness-" + Guid.NewGuid().ToString("N"));

    /// <summary>A partial warm hit must not hide a better canonical match, regardless of the query's lexical separators.</summary>
    [Theory]
    [InlineData("release database")]
    [InlineData("RELEASE/DATABASE")]
    [InlineData("release-database")]
    [InlineData("release\tdatabase")]
    public async Task WarmDistractorCannotHideBetterCanonicalAnswer(string query)
    {
        await using var runtime = Runtime();
        const string distractor = "release generic checklist";
        const string answer = "release database requires verified migration backup";
        await RememberAsync(runtime, distractor);
        await PresentAsync(runtime, "generic", "distractor");
        await RememberAsync(runtime, answer);

        var search = await runtime.WorkingContextAsync(query, 8192, "search");
        var working = await runtime.WorkingContextAsync(query, 8192);
        Assert.Null(search.Error);
        Assert.Null(working.Error);
        Assert.Equal(answer, working.Data!.Items[0].Memory.Content);
        Assert.Equal(search.Data!.Items.Select(item => item.Memory.Content), working.Data.Items.Select(item => item.Memory.Content));
        Assert.Equal("L3", Assert.Single(working.Data.Items, item => item.Memory.Content == distractor).Level);
        Assert.Equal("L10", working.Data.Items[0].Level);
    }

    /// <summary>Substring-only warm matches must not widen the canonical whole-token query semantics.</summary>
    [Fact]
    public async Task WarmSubstringCannotReplaceWholeTokenAnswer()
    {
        await using var runtime = Runtime();
        await RememberAsync(runtime, "alphabet generic guide");
        await PresentAsync(runtime, "alphabet", "substring");
        const string answer = "alpha migration advice";
        await RememberAsync(runtime, answer);
        var response = await runtime.WorkingContextAsync("alpha", 8192);
        Assert.Null(response.Error);
        Assert.Equal(answer, Assert.Single(response.Data!.Items).Memory.Content);
    }

    /// <summary>Working search must retain smaller ranked answers when the top candidate cannot fit as a whole.</summary>
    [Theory]
    [InlineData("working")]
    [InlineData("search")]
    public async Task WorkingBudgetSkipsOversizedCandidateAndPreservesRequiredSet(string mode)
    {
        await using var runtime = Runtime();
        await SeedBudgetAsync(runtime);
        Assert.Null((await runtime.KnowledgeWorkingAsync("Budget check", ["Preserve scope"], [], [], "Verify answer", 0, "working_budget")).Error);
        var response = await runtime.WorkingContextAsync("budgetfixture oversized", 512, mode);
        Assert.Null(response.Error);
        Assert.Equal("budgetfixture concise answer", Assert.Single(response.Data!.Items).Memory.Content);
        Assert.Equal(1, response.Data.OmittedCandidateCount);
        Assert.InRange(response.Data.EstimatedTokenCost, 1, 512);
        Assert.Equal((JsonSerializer.Serialize(response).Length + 3) / 4, response.Data.EstimatedTokenCost);
        if (mode == "working") Assert.Equal("Budget check", response.Data.WorkingSet!.Goal);
        else Assert.Null(response.Data.WorkingSet);
    }

    /// <summary>The v2 canonical endpoint must also fill available space with complete smaller candidates.</summary>
    [Fact]
    public async Task CanonicalBudgetSkipsOversizedCandidate()
    {
        await using var runtime = Runtime();
        await SeedBudgetAsync(runtime);
        var response = await runtime.AgentContextAsync("budgetfixture oversized", 512, default);
        Assert.Null(response.Error);
        Assert.Equal("budgetfixture concise answer", Assert.Single(response.Data!.Items).Content);
        Assert.Equal(1, response.Data.OmittedCandidateCount);
        Assert.InRange(response.Data.EstimatedTokenCost, 1, 512);
        Assert.Equal((JsonSerializer.Serialize(response).Length + 3) / 4, response.Data.EstimatedTokenCost);
    }

    /// <summary>Empty working requests remain bounded warm-only reads; exclusion affects residency, not explicit canonical access.</summary>
    [Fact]
    public async Task EmptyWorkingQueryAndWarmExclusionKeepTheirBoundaries()
    {
        await using var runtime = Runtime();
        await RememberAsync(runtime, "residentfixture remembered answer");
        var classified = Assert.Single((await runtime.AgentContextAsync("residentfixture", 8192, default)).Data!.Items);
        Assert.Null((await runtime.KnowledgeMetadataAsync(classified.CitationRef, "Knowledge", "fixture", "classify_resident")).Error);
        var source = await PresentAsync(runtime, "residentfixture", "resident");
        await RememberAsync(runtime, "canonicalfixture unpresented answer");
        Assert.Single((await runtime.WorkingContextAsync("")).Data!.Items);
        Assert.Null((await runtime.KnowledgeWarmExcludeAsync(source.CitationRef, true, "exclude_resident")).Error);
        Assert.Empty((await runtime.WorkingContextAsync("")).Data!.Items);
        var searched = await runtime.WorkingContextAsync("residentfixture");
        Assert.Equal("L11", Assert.Single(searched.Data!.Items).Level);
    }

    /// <summary>An expired shared set remains invisible as content but exposes its actual CAS fence for safe renewal.</summary>
    [Fact]
    public async Task ExpiredWorkingSetExposesRevisionWithoutReturningExpiredContent()
    {
        await using var runtime = Runtime();
        var profile = (await runtime.KnowledgeCapabilitiesAsync()).Data!.KnowledgeProfileRef;
        var store = new KnowledgeFileStore(_path);
        var state = new KnowledgeState
        {
            Version = 1,
            WorkingSet = new(7, "Expired goal", ["Expired fact"], [], [], "Old action", DateTimeOffset.UtcNow.AddDays(-1))
        };
        Assert.True(await store.CompareExchangeAsync(profile, 0, state, default));
        var response = await runtime.WorkingContextAsync("");
        Assert.Null(response.Error);
        Assert.Null(response.Data!.WorkingSet);
        var json = JsonSerializer.SerializeToElement(response.Data);
        Assert.True(json.TryGetProperty("WorkingSetRevision", out var revision));
        Assert.Equal(7, revision.GetInt64());
        Assert.Equal("revision_conflict", (await runtime.KnowledgeWorkingAsync("Wrong goal", [], [], [], "Wrong update", 0, "stale_renewal")).Error!.Code);
        var renewed = await runtime.KnowledgeWorkingAsync("Current goal", [], [], [], "Current action", revision.GetInt64(), "renewal");
        Assert.Null(renewed.Error);
        Assert.Equal(8, (await runtime.WorkingContextAsync("")).Data!.WorkingSet!.Revision);
        var conflict = await runtime.KnowledgeWorkingAsync("Racing goal", [], [], [], "Racing action", revision.GetInt64(), "racing_renewal");
        Assert.Equal("revision_conflict", conflict.Error!.Code);
    }

    /// <summary>Seeds a strongly ranked oversized answer followed by a lower-ranked useful answer; this ranking does not depend on random IDs.</summary>
    private static async Task SeedBudgetAsync(LocalAgMemoryMcpRuntime runtime)
    {
        await RememberAsync(runtime, "budgetfixture oversized " + new string('x', 5500));
        await RememberAsync(runtime, "budgetfixture concise answer");
    }

    /// <summary>Ten high-scoring large records must not hide the eleventh useful answer at the old retrieval cutoff.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackingSearchesBeyondOldTenCandidateCutoff(bool bm25)
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(LocalAgMemoryMcpConfiguration.Create(
            _path, "fixture-actor", "fixture-tenant", "fixture-project") with { UseBm25Recall = bm25 });
        // All records have three tokens: BM25 length normalization cannot demote oversized high-ranked hits.
        for (var index = 0; index < 10; index++)
            await RememberAsync(runtime, "packingfixture oversized " + index + new string('x', 5500));
        await RememberAsync(runtime, "packingfixture concise answer");
        var canonical = await runtime.AgentContextAsync("packingfixture oversized", 512, default);
        Assert.Null(canonical.Error);
        Assert.Equal("packingfixture concise answer", Assert.Single(canonical.Data!.Items).Content);
        Assert.Equal(10, canonical.Data.OmittedCandidateCount);
        foreach (var mode in new[] { "search", "working" })
        {
            var working = await runtime.WorkingContextAsync("packingfixture oversized", 512, mode);
            Assert.Null(working.Error);
            Assert.Equal("packingfixture concise answer", Assert.Single(working.Data!.Items).Memory.Content);
            Assert.Equal(10, working.Data.OmittedCandidateCount);
            Assert.InRange(working.Data.EstimatedTokenCost, 1, 512);
        }
        Assert.Equal(bm25 ? "bm25" : "lexical", (await runtime.AgentCapabilitiesAsync()).Data!.RetrievalMode);
    }

    /// <summary>Wider internal retrieval never widens public response cardinality.</summary>
    [Fact]
    public async Task InternalPoolRetainsPublicTenRecordLimit()
    {
        await using var runtime = Runtime();
        for (var index = 0; index < 14; index++) await RememberAsync(runtime, "poolfixture answer " + index);
        var canonical = (await runtime.AgentContextAsync("poolfixture", 8192, default)).Data!;
        var working = (await runtime.WorkingContextAsync("poolfixture", 8192)).Data!;
        Assert.Equal(10, canonical.Items.Count);
        Assert.Equal(10, working.Items.Count);
        Assert.Equal(4, canonical.OmittedCandidateCount);
        Assert.Equal(4, working.OmittedCandidateCount);
    }

    /// <summary>Retracting a resident must backfill the canonical top ten while keeping stale content and stale warmth out.</summary>
    [Fact]
    public async Task CanonicalBackfillKeepsTenAnswersAfterWarmRetraction()
    {
        await using var runtime = Runtime();
        for (var index = 0; index < 11; index++)
            await RememberAsync(runtime, "refreshfixture answer " + index);
        var first = (await runtime.AgentContextAsync("refreshfixture", 8192, default)).Data!.Items;
        Assert.Equal(10, first.Count);
        for (var index = 0; index < first.Count; index++)
            Assert.Null((await runtime.KnowledgePresentAsync(first[index].CitationRef, "refresh_fixture", "refresh_" + index)).Error);
        var removed = first[0];
        Assert.Null((await runtime.AgentCorrectAsync(removed.CitationRef, removed.Revision, "refresh_retract", null, default)).Error);
        var response = await runtime.WorkingContextAsync("refreshfixture", 8192);
        Assert.Null(response.Error);
        Assert.Equal(10, response.Data!.Items.Count);
        Assert.DoesNotContain(response.Data.Items, item => item.Memory.Content == removed.Content);
        Assert.Equal(9, response.Data.Items.Count(item => item.Level == "L3"));
        Assert.Single(response.Data.Items, item => item.Level == "L10");
        Assert.Equal(9, (await runtime.WorkingContextAsync("", 8192)).Data!.Items.Count);
    }

    /// <summary>Registers a genuine fixture presentation so the test exercises real warm admission.</summary>
    private static async Task<AgentMemory> PresentAsync(LocalAgMemoryMcpRuntime runtime, string query, string label)
    {
        var source = Assert.Single((await runtime.AgentContextAsync(query, 8192, default)).Data!.Items);
        Assert.Null((await runtime.KnowledgePresentAsync(source.CitationRef, "effectiveness_fixture", "present_" + label)).Error);
        return source;
    }

    private LocalAgMemoryMcpRuntime Runtime() => new(LocalAgMemoryMcpConfiguration.Create(_path, "fixture-actor", "fixture-tenant", "fixture-project"));
    private static async Task RememberAsync(LocalAgMemoryMcpRuntime runtime, string content) =>
        await runtime.RememberAsync(content, "Fact", .6, .7, null, default);

    /// <summary>Deletes only this fixture's new temporary store after each runtime has closed.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
    }
}
