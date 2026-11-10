using AgMemory.McpServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (KnowledgeMaintenanceCommand.IsCommand(args))
{
    Environment.ExitCode = await KnowledgeMaintenanceCommand.RunAsync(args);
    return;
}

var builder = Host.CreateApplicationBuilder(args);
// MCP uses stdout as a protocol channel. Diagnostics must never corrupt that stream.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(LocalAgMemoryMcpRuntime.FromEnvironment());
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<AgMemoryTools>()
    .WithTools<AgentMemoryTools>()
    .WithTools<AgentSkillTools>()
    .WithTools<KnowledgeMemoryTools>()
    .WithTools<ClaimMemoryTools>()
    .WithTools<SharedProcedureMemoryTools>()
    .WithTools<TemporaryNoteMemoryTools>();

await builder.Build().RunAsync();
