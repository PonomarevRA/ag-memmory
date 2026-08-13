using AgMemory.Web.Features.Chat;
using AgMemory.Web.Features.MemoryGraph;
using Xunit;

namespace AgMemory.Web.Tests;

public sealed class LocalChatMemoryFeatureTests
{
    [Fact]
    public async Task RemembersChatMessagesAndRecallsThemForTheNextQuestion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agmemory-chat-memory-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await using var feature = new LocalChatMemoryFeature(new MemoryGraphHostOptions
            {
                Enabled = true,
                ActorId = "local-development",
                Scope = new MemoryGraphScopeOptions { TenantId = "local-development" }
            }, root);

            await feature.RememberAsync("thread-1", "user", "Yuki lives on the local Mac.", default);
            await feature.RememberAsync("thread-1", "assistant", "Yuki is available through the local llama server.", default);
            var recalled = await feature.RecallAsync("Where does Yuki live?", default);

            Assert.NotNull(recalled);
            Assert.Contains("local", recalled, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("- ", recalled, StringComparison.Ordinal);
            Assert.DoesNotContain("[", recalled, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
