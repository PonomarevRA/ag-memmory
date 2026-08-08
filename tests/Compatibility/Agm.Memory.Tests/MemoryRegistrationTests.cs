using Agm.Memory.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agm.Memory.Tests;

public sealed class MemoryRegistrationTests
{
    [Fact]
    public void AddAgmMemoryCore_RegistersAdvancedPipelineAsSharedServices()
    {
        var services = new ServiceCollection();

        services.AddAgmMemoryCore();
        using var provider = services.BuildServiceProvider();

        var ingestion = provider.GetRequiredService<IMemoryIngestionService>();
        Assert.Same(ingestion, provider.GetRequiredService<ICanonicalMemoryCatalog>());
        Assert.Same(ingestion, provider.GetRequiredService<ICanonicalMemoryLifecycle>());
        Assert.NotNull(provider.GetRequiredService<IMemorySourceAccessPolicy>());
        Assert.NotNull(provider.GetRequiredService<IMemoryGoldenCorpusEvaluator>());
        Assert.NotNull(provider.GetRequiredService<IMemoryGraphReranker>());
        Assert.NotNull(provider.GetRequiredService<IMemoryContextReranker>());
        Assert.NotNull(provider.GetRequiredService<IMemoryContextBuilder>());
        Assert.NotNull(provider.GetRequiredService<IMemoryOperationsTelemetry>());
        Assert.NotNull(provider.GetRequiredService<IMemoryCanaryGate>());
        Assert.NotNull(provider.GetRequiredService<IMemoryConfigurationRegistry<string>>());
        Assert.NotNull(provider.GetRequiredService<IMemoryRetryQueue<string>>());
    }
}
