using AgMemory.Web.Components;
using AgMemory.Web.Features.Chat;
using AgMemory.Web.Features.Navigation;
using AgMemory.Web.Gateway;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();
app.UseRateLimiter();

app.MapStaticAssets();
app.MapPost(ChatEndpoint.Route, ChatEndpoint.HandleAsync)
    .RequireRateLimiting(ChatEndpoint.RateLimitPolicy);
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
