using AgMemory.Web.Components;
using AgMemory.Web.Features.Chat;
using AgMemory.Web.Features.MemoryGraph;
using AgMemory.Web.Features.Navigation;
using AgMemory.Web.Gateway;
using AgMemory.Web.Hosting;
using System.Threading.RateLimiting;

var desktopHost = MacDesktopHost.Detect();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = desktopHost.IsEnabled ? Environments.Development : null,
    ContentRootPath = desktopHost.IsEnabled ? AppContext.BaseDirectory : null
});
desktopHost.ConfigureLoopbackKestrel(builder);
var applicationDataDirectory = LocalApplicationPaths.ResolveDataDirectory();
Directory.CreateDirectory(applicationDataDirectory);
builder.Configuration
    .AddJsonFile(LocalApplicationPaths.PersistentSettingsPath(applicationDataDirectory), optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
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
builder.Services.AddHttpClient(OpenAiCompatibleChatGateway.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
});
builder.Services.AddSingleton<IModelChatGateway, OpenAiCompatibleChatGateway>();
builder.Services.AddScoped<BrowserStateInterop>();
var memoryGraphOptions = builder.Configuration.GetSection(MemoryGraphHostOptions.SectionName).Get<MemoryGraphHostOptions>() ?? new();
builder.Services.AddSingleton(new LocalMemoryGraphFeature(memoryGraphOptions, applicationDataDirectory));
builder.Services.AddSingleton(new LocalChatMemoryFeature(memoryGraphOptions, applicationDataDirectory));

var app = builder.Build();
app.Logger.LogInformation(
    "Local memory graph composition: configured={Configured}; development={IsDevelopment}",
    memoryGraphOptions.TryCreate(applicationDataDirectory) is not null,
    app.Environment.IsDevelopment());

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!desktopHost.IsEnabled)
    app.UseHttpsRedirection();

app.UseAntiforgery();
app.UseRateLimiter();

app.MapStaticAssets();
app.MapPost(ChatEndpoint.Route, ChatEndpoint.HandleAsync)
    .RequireRateLimiting(ChatEndpoint.RateLimitPolicy);
app.MapGet(MemoryGraphEndpoint.Route, MemoryGraphEndpoint.HandleAsync);
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

desktopHost.OpenBrowserWhenStarted(app);
app.Run();

public partial class Program;
