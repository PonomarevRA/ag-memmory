using AgMemory.Web.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgMemory.Web.Tests;

/// <summary>Checks the OS-independent standalone drain budget without terminating an installed service.</summary>
public sealed class StandaloneShutdownTests
{
    [Fact]
    public void StandaloneDrainFinishesBeforeTheNativeDeadline()
    {
        var services = new ServiceCollection();
        StandaloneShutdown.Configure(services, standalone: true);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromSeconds(6), provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }

    [Fact]
    public void ServerDeploymentRetainsItsOwnShutdownPolicy()
    {
        var services = new ServiceCollection();
        services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(42));
        StandaloneShutdown.Configure(services, standalone: false);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromSeconds(42), provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }
}
