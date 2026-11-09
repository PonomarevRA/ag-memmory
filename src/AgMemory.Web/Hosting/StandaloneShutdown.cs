namespace AgMemory.Web.Hosting;

/// <summary>Bounds standalone host draining on every OS without changing server deployment policy.</summary>
public static class StandaloneShutdown
{
    /// <summary>Leaves time for the native launcher to reap its child before its own seven-second deadline.</summary>
    public static void Configure(IServiceCollection services, bool standalone)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (standalone)
            services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(6));
    }
}
