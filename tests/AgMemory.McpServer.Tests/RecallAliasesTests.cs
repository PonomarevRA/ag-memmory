using AgMemory.Core;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Checks domain-controlled alias resolution without changing canonical text or broadening authority.</summary>
public sealed class RecallAliasesTests
{
    /// <summary>AFTER resolves as an explicit acronym; ordinary English after and substring lookalikes keep their literal meaning.</summary>
    [Theory]
    [InlineData("AFTER", true)]
    [InlineData("what is AFTER?", true)]
    [InlineData("Исследование «AFTER» о переносимости процедур", true)]
    [InlineData("“AFTER”", true)]
    [InlineData("after", false)]
    [InlineData("After", false)]
    [InlineData("AFTERPARTY", false)]
    public void AcronymsPreserveCaseAndBoundaries(string query, bool expanded)
    {
        var found = RecallQueryAliases.Expand(query).Any(item => item.Concept == "procedure-transfer");
        Assert.Equal(expanded, found);
    }

    /// <summary>RU phrases work inside longer formulations; lexical expansion is finite and unknown text remains unknown.</summary>
    [Fact]
    public void RussianPhrasesExpandWholeTokensWithBoundedWork()
    {
        var phrase = Assert.Single(RecallQueryAliases.Expand("перенос процедур между моделями"));
        Assert.Equal("procedure transfer", phrase.Query);
        Assert.Empty(RecallQueryAliases.Expand("переноска процедурный"));
        Assert.Empty(RecallQueryAliases.Expand("volcanic purple spaceship"));
        Assert.InRange(RecallQueryAliases.Expand("AFTER CAS RRF FTS срок жизни рабочего контекста").Count, 1, 3);
        Assert.Equal(RecallQueryAliases.Expand("AFTER CAS RRF FTS"), RecallQueryAliases.Expand("AFTER CAS RRF FTS"));
    }

    /// <summary>Configuration can retain ordinary lexical/BM25 behavior independently of dictionary expansion.</summary>
    [Fact]
    public void OperatorsCanDisableAliasesExplicitly()
    {
        Assert.True(LocalAgMemoryMcpConfiguration.ParseQueryAliases(null));
        Assert.False(LocalAgMemoryMcpConfiguration.ParseQueryAliases(" OFF "));
        Assert.Throws<ArgumentException>(() => LocalAgMemoryMcpConfiguration.ParseQueryAliases("automatic-model"));
    }

    /// <summary>More than ten common-word distractors cannot suppress the distinct acronym's domain source.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AliasFusionFindsCrossLanguageAndAcronymSource(bool bm25)
    {
        var path = Path.Combine(Path.GetTempPath(), "agmemory-alias-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = LocalAgMemoryMcpConfiguration.Create(path, "alias-test", "alias-tenant") with { UseBm25Recall = bm25 };
            await using var runtime = new LocalAgMemoryMcpRuntime(config);
            for (var index = 0; index < 18; index++)
                await runtime.RememberAsync("After completion generic maintenance note " + index, "Fact", .5, .7, null, default);
            const string source = "AFTER research studies procedure transfer across tasks and models";
            await runtime.RememberAsync(source, "Outcome", .5, .7, null, default);
            foreach (var query in new[] { "AFTER", "перенос процедур между моделями" })
            {
                var context = await runtime.AgentContextAsync(query, 8192, default);
                Assert.Null(context.Error);
                Assert.Contains(context.Data!.Items, item => item.Content == source);
                Assert.InRange(context.Data.Items.Count, 1, 10);
            }
            Assert.Equal(RecallQueryAliases.Version, (await runtime.AgentCapabilitiesAsync()).Data!.QueryAliasesVersion);
            await using var literal = new LocalAgMemoryMcpRuntime(config with { UseQueryAliases = false });
            Assert.Empty((await literal.AgentContextAsync("перенос процедур между моделями", 8192, default)).Data!.Items);
            await using var foreign = new LocalAgMemoryMcpRuntime(config with { Scope = config.Scope with { WorkspaceId = new("other") } });
            Assert.Empty((await foreign.AgentContextAsync("перенос процедур между моделями", 8192, default)).Data!.Items);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }
}
