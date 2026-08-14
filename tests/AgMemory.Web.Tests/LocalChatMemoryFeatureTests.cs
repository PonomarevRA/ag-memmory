using AgMemory.Contracts;
using AgMemory.Web.Features.Chat;
using AgMemory.Web.Features.MemoryGraph;
using Xunit;

namespace AgMemory.Web.Tests;

public sealed class LocalChatMemoryFeatureTests
{
    [Fact]
    public async Task RemembersChatMessagesAndRecallsThemForTheNextQuestion()
    {
        var root = TemporaryPath();
        try
        {
            Directory.CreateDirectory(root);
            await using var feature = Configured(root);

            await feature.RememberAsync("thread-1", "user", "Yuki lives on the local Mac.", default);
            await feature.RememberAsync("thread-1", "assistant", "Yuki is available through the local llama server.", default);
            var recalled = await feature.RecallAsync("Where does Yuki live?", default);

            Assert.NotNull(recalled);
            Assert.Contains("local", recalled, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("- ", recalled, StringComparison.Ordinal);
            Assert.DoesNotContain("[", recalled, StringComparison.Ordinal);
            Assert.True(feature.LastInjectHitCount >= 1);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task EmptyStore_ReturnsNoContextAndZeroInjectCount()
    {
        var root = TemporaryPath();
        try
        {
            Directory.CreateDirectory(root);
            await using var feature = Configured(root);

            var recalled = await feature.RecallAsync("anything at all", default);

            Assert.Null(recalled);
            Assert.Equal(0, feature.LastInjectHitCount);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task UnmatchedPrompt_FallsBackToLatestEventHandoffLine()
    {
        var root = TemporaryPath();
        try
        {
            Directory.CreateDirectory(root);
            await using var feature = Configured(root);
            await feature.RememberAsync("thread-1", "user", "alpha-beta-gamma unique marker", default);

            var recalled = await feature.RecallAsync("qqqqzzzzmmmm unmatched query tokens", default);

            Assert.NotNull(recalled);
            Assert.StartsWith("- ", recalled, StringComparison.Ordinal);
            Assert.Contains("alpha-beta-gamma", recalled, StringComparison.Ordinal);
            Assert.DoesNotContain("[", recalled, StringComparison.Ordinal);
            Assert.Equal(1, feature.LastInjectHitCount);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task UnmatchedPrompt_PrefersLatestSummaryOverEventForHandoff()
    {
        var root = TemporaryPath();
        try
        {
            Directory.CreateDirectory(root);
            await using var feature = Configured(root);
            await feature.RememberAsync("thread-1", "user", "alpha-beta-gamma unique event", MemoryRecordType.Event, default);
            await feature.RememberAsync("thread-2", "assistant", "compact where-you-left-off summary", MemoryRecordType.Summary, default);

            var recalled = await feature.RecallAsync("qqqqzzzzmmmm unmatched query tokens", default);

            Assert.NotNull(recalled);
            Assert.Contains("where-you-left-off", recalled, StringComparison.Ordinal);
            Assert.DoesNotContain("alpha-beta-gamma", recalled, StringComparison.Ordinal);
            Assert.Equal(1, feature.LastInjectHitCount);
        }
        finally
        {
            Delete(root);
        }
    }

    private static LocalChatMemoryFeature Configured(string root) => new(new MemoryGraphHostOptions
    {
        Enabled = true,
        ActorId = "local-development",
        Scope = new MemoryGraphScopeOptions { TenantId = "local-development" }
    }, root);

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"agmemory-chat-memory-{Guid.NewGuid():N}");

    private static void Delete(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
