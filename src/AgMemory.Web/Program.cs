using AgMemory.Web.Features.Desktop;
using AgMemory.Web.Features.AgentSetup;
using AgMemory.Web.Features.TechnicalLogs;
using AgMemory.Web.Features.Backups;
using AgMemory.Web.Features.AgentActivity;
using AgMemory.Web.Features.Antiforgery;
using AgMemory.Web.Features.AppVersion;
using AgMemory.Web.Features.Chat;
using AgMemory.Web.Features.MemoryGraph;
using AgMemory.Web.Features.MemoryExperience;
using AgMemory.Web.Features.MemoryReader;
using AgMemory.Web.Features.MemoryStatus;
using AgMemory.Web.Features.Knowledge;
using AgMemory.Web.Gateway;
using AgMemory.Web.Hosting;
using Microsoft.AspNetCore.DataProtection;
using System.Threading.RateLimiting;

if (LocalBackupCommand.IsCommand(args))
{
    Environment.ExitCode = await LocalBackupCommand.RunAsync(args);
    return;
}

var desktopHost = MacDesktopHost.Detect();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = desktopHost.IsEnabled ? Environments.Development : null,
    ContentRootPath = desktopHost.IsEnabled ? AppContext.BaseDirectory : null
});
desktopHost.ConfigureLoopbackKestrel(builder);
StandaloneShutdown.Configure(builder.Services, desktopHost.IsEnabled);
var applicationDataDirectory = LocalApplicationPaths.ResolveDataDirectory();
Directory.CreateDirectory(applicationDataDirectory);
builder.Configuration
    .AddJsonFile(LocalApplicationPaths.PersistentSettingsPath(applicationDataDirectory), optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.AddTechnicalLogs(applicationDataDirectory);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(applicationDataDirectory, "data-protection")));
builder.Services.AddAntiforgery();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(ChatEndpoint.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 12,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.Configure<ModelGatewayOptions>(builder.Configuration.GetSection(ModelGatewayOptions.SectionName));
// These switches are server-only: nothing in the SPA configuration may expose them.
builder.Services.Configure<MemoryExperienceOptions>(builder.Configuration.GetSection(MemoryExperienceOptions.SectionName));
builder.Services.AddHttpClient(OpenAiCompatibleChatGateway.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
});
builder.Services.AddSingleton<IModelChatGateway, OpenAiCompatibleChatGateway>();
var memoryGraphOptions = builder.Configuration.GetSection(MemoryGraphHostOptions.SectionName).Get<MemoryGraphHostOptions>() ?? new();
var memoryReaderOptions = builder.Configuration.GetSection(MemoryReaderHostOptions.SectionName).Get<MemoryReaderHostOptions>() ?? new();
builder.Services.AddSingleton(new LocalKnowledgeFeature(memoryGraphOptions, applicationDataDirectory));
builder.Services.AddSingleton<LocalMemoryGraphFeature>(services => new(memoryGraphOptions, applicationDataDirectory,
    services.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddSingleton(services => new ChatMemorySourceLinks(memoryReaderOptions, applicationDataDirectory,
    services.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddSingleton(services => new LocalChatMemoryFeature(memoryGraphOptions, applicationDataDirectory,
    services.GetRequiredService<ChatMemorySourceLinks>()));
builder.Services.AddSingleton<LocalMemoryReaderFeature>(services => new(
    memoryReaderOptions,
    applicationDataDirectory,
    services.GetRequiredService<IDataProtectionProvider>()));

builder.Services.AddSingleton(services => new LocalAgentActivityFeature(memoryGraphOptions, applicationDataDirectory,
    services.GetRequiredService<LocalMemoryGraphFeature>()));
builder.Services.AddSingleton<IDesktopWidget, DesktopWidget>();
var app = builder.Build();
DesktopOwnerLifetime.Register(app);
app.UseTechnicalLogs();
app.Logger.LogInformation(
    "Local memory graph composition: configured={Configured}; development={IsDevelopment}",
    memoryGraphOptions.TryCreate(applicationDataDirectory) is not null,
    app.Environment.IsDevelopment());
app.Logger.LogInformation(
    "Local memory reader composition: configured={Configured}; development={IsDevelopment}",
    memoryReaderOptions.TryCreate(applicationDataDirectory) is not null,
    app.Environment.IsDevelopment());

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
// Client unknown routes are handled by the SPA fallback below. Do not re-execute API 404s into it.
if (!desktopHost.IsEnabled)
    app.UseHttpsRedirection();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAntiforgery();
app.UseRateLimiter();

app.UseDefaultFiles();
app.UseStaticFiles();
AgentActivityEndpoint.Map(app);
DesktopWidgetEndpoint.Map(app);
app.MapGet(AntiforgeryEndpoint.Route, AntiforgeryEndpoint.HandleAsync);
app.MapGet(AppVersionEndpoint.Route, AppVersionEndpoint.Handle);
app.MapGet(ChatMemorySourceLinks.Route, ChatMemorySourceLinks.HandleAsync);
app.MapPost(ChatEndpoint.Route, ChatEndpoint.HandleAsync)
    .RequireRateLimiting(ChatEndpoint.RateLimitPolicy);
app.MapGet(MemoryGraphEndpoint.Route, (HttpContext context, string? continuation, string? area, IHostEnvironment environment,
    LocalMemoryGraphFeature feature, LocalMemoryReaderFeature reader, CancellationToken cancellationToken) =>
    MemoryGraphEndpoint.HandleAsync(context, environment, feature, cancellationToken, continuation, reader, area));
app.MapGet(MemoryStatusEndpoint.Route, MemoryStatusEndpoint.HandleAsync);
app.MapGet(KnowledgeEndpoint.Route, KnowledgeEndpoint.HandleAsync);
app.MapGet(MemoryRecordBrowserEndpoint.Route, MemoryRecordBrowserEndpoint.HandleAsync);
app.MapGet(MemoryReaderEndpoint.AreasRoute, MemoryReaderEndpoint.HandleAreas);
app.MapGet(MemoryReaderCardsEndpoint.Route, MemoryReaderCardsEndpoint.HandleAsync);
app.MapGet(MemoryReaderEndpoint.CatalogRoute, (HttpContext context, string? continuation, string? @namespace, string? tag, string? search, string? area,
    IHostEnvironment environment, LocalMemoryReaderFeature feature, CancellationToken cancellationToken) =>
    MemoryReaderEndpoint.HandleCatalogAsync(context, continuation, @namespace, tag, search, environment, feature, cancellationToken, area));
app.MapGet(MemoryReaderEndpoint.HomeRoute, (HttpContext context, string? area, IHostEnvironment environment, LocalMemoryReaderFeature feature, CancellationToken cancellationToken) =>
    MemoryReaderEndpoint.HandleHomeAsync(context, environment, feature, cancellationToken, area));
app.MapGet(MemoryReaderEndpoint.TreeRoute, (HttpContext context, string? continuation, string? area, IHostEnvironment environment,
    LocalMemoryReaderFeature feature, CancellationToken cancellationToken) =>
    MemoryReaderEndpoint.HandleTreeAsync(context, continuation, environment, feature, cancellationToken, area));
app.MapGet(MemoryReaderEndpoint.TagRoute, (HttpContext context, string? continuation, string? area, IHostEnvironment environment, LocalMemoryReaderFeature feature, CancellationToken cancellationToken) =>
    MemoryReaderEndpoint.HandleTagsAsync(context, continuation, environment, feature, cancellationToken, area));
app.MapGet(MemoryReaderEndpoint.DocumentRoute, (HttpContext context, string routeKey, string? block, string? area, IHostEnvironment environment, LocalMemoryReaderFeature feature, CancellationToken cancellationToken) =>
    MemoryReaderEndpoint.HandleDocumentAsync(context, routeKey, block, environment, feature, cancellationToken, area));
AgentSetupEndpoint.Map(app);
app.MapFallback(context =>
{
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
    context.Response.Headers.CacheControl = "no-store";
    // SendFileAsync does not infer MIME type; the SPA shell must be rendered as HTML in browsers and WebViews.
    context.Response.ContentType = "text/html; charset=utf-8";
    return context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "dist", "index.html"));
});

desktopHost.OpenBrowserWhenStarted(app);
app.Run();

public partial class Program;
