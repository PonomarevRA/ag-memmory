using AgMemory.Web.Features.MemoryGraph;

namespace AgMemory.Web.Features.TechnicalLogs;

/// <summary>Composes the optional native sink and read API without knowledge of any collector.</summary>
public static class TechnicalLogRegistration
{
    /// <summary>Loads service settings and registers a failure-isolated native logger sink, preserving existing providers.</summary>
    public static void AddTechnicalLogs(this WebApplicationBuilder builder, string dataDirectory)
    {
        TechnicalLogOptions options;
        try { options = builder.Configuration.GetSection("TechnicalLogs").Get<TechnicalLogOptions>() ?? new(); }
        catch { options = new() { MaxSizeBytes = 0 }; }
        var provider = new TechnicalLogProvider(dataDirectory, options);
        builder.Services.AddSingleton(provider);
        string? ownedDataDirectory = null;
        try
        {
            var graph = builder.Configuration.GetSection(MemoryGraphHostOptions.SectionName).Get<MemoryGraphHostOptions>() ?? new();
            ownedDataDirectory = graph.TryCreate(dataDirectory)?.StoragePath;
        }
        catch { /* Invalid graph configuration cannot break independent diagnostics startup. */ }
        builder.Services.AddSingleton(new LocalServiceDiagnostics(dataDirectory, ownedDataDirectory));
        builder.Logging.AddProvider(provider);
        builder.Logging.AddFilter<TechnicalLogProvider>(null, LogLevel.Trace);
    }

    /// <summary>Suppresses self-read logging in the current request and adds only the two technical GET routes.</summary>
    public static void UseTechnicalLogs(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(TechnicalLogEndpoint.Prefix) && context.Request.Path != DiagnosticsEndpoint.Route) { await next(context); return; }
            var previous = TechnicalLogSuppression.Active.Value;
            TechnicalLogSuppression.Active.Value = true;
            try { await next(context); }
            finally { TechnicalLogSuppression.Active.Value = previous; }
        });
        TechnicalLogEndpoint.Map(app);
        DiagnosticsEndpoint.Map(app);
    }
}
