using Agm.Memory.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agm.Memory;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgmMemoryCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IWorkspaceMemoryExtractor, LocalWorkspaceMemoryExtractor>();
        services.TryAddSingleton<IWorkspaceMemoryConsolidator, LocalWorkspaceMemoryConsolidator>();
        services.TryAddSingleton<IMemorySourceAccessPolicy, StrictMemorySourceAccessPolicy>();
        services.TryAddSingleton<IMemoryGoldenCorpusEvaluator, MemoryGoldenCorpusEvaluator>();
        services.TryAddSingleton<MemoryIngestionService>();
        services.TryAddSingleton<IMemoryIngestionService>(provider =>
            provider.GetRequiredService<MemoryIngestionService>());
        services.TryAddSingleton<ICanonicalMemoryCatalog>(provider =>
            provider.GetRequiredService<MemoryIngestionService>());
        services.TryAddSingleton<ICanonicalMemoryLifecycle>(provider =>
            provider.GetRequiredService<MemoryIngestionService>());
        services.TryAddSingleton<IMemoryGraphReranker, MemoryGraphReranker>();
        services.TryAddSingleton<IMemoryContextReranker, DeterministicMemoryContextReranker>();
        services.TryAddSingleton<IMemoryContextBuilder, MemoryContextBuilder>();
        services.TryAddSingleton<IMemoryOperationsTelemetry, BoundedMemoryOperationsTelemetry>();
        services.TryAddSingleton<IMemoryCanaryGate, MemoryCanaryGate>();
        services.TryAddSingleton(typeof(IMemoryConfigurationRegistry<>),
            typeof(VersionedMemoryConfigurationRegistry<>));
        services.TryAddSingleton(typeof(IMemoryRetryQueue<>),
            typeof(DeterministicMemoryRetryQueue<>));
        return services;
    }
}
