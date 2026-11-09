using Agm.Memory.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agm.Memory.Sqlite;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAgmMemorySqlite(
        this IServiceCollection services,
        Func<IServiceProvider, ISqliteConnectionSource> connectionSourceFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionSourceFactory);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMemoryScopeValidator>(AllowAllMemoryScopeValidator.Instance);
        services.AddSingleton<ISqliteConnectionSource>(connectionSourceFactory);
        services.AddSingleton<SqliteWorkspaceMemoryService>();
        services.AddSingleton<IWorkspaceMemoryService>(provider =>
            provider.GetRequiredService<SqliteWorkspaceMemoryService>());
        services.AddSingleton<IWorkspaceMemoryWriter>(provider =>
            provider.GetRequiredService<SqliteWorkspaceMemoryService>());
        services.AddSingleton<IFanOutMemoryStore>(provider =>
            new SqliteFanOutMemoryStore(
                provider.GetRequiredService<ISqliteConnectionSource>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<IMemoryScopeValidator>()));
        return services;
    }

    public static IServiceCollection AddAgmMemorySqlite(
        this IServiceCollection services,
        string connectionString) =>
        services.AddAgmMemorySqlite(_ => new SqliteConnectionStringSource(connectionString));
}
