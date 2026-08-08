using AgMemory.McpServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
// MCP uses stdout as a protocol channel. Diagnostics must never corrupt that stream.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(LocalAgMemoryMcpRuntime.FromEnvironment());
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<AgMemoryTools>();

await builder.Build().RunAsync();
