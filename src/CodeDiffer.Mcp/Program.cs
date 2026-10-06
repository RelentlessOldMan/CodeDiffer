using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// CodeDiffer.Mcp.exe — the agent-facing stdio MCP server. stdout is the JSON-RPC transport, so every log
// line goes to stderr. Compares run in the background inside this process and are queried by id.
Console.Error.WriteLine($"{CodeDiffer.Mcp.McpServerInfo.ServerName} MCP {CodeDiffer.Mcp.McpServerInfo.Version}: ready (stdio)");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = CodeDiffer.Mcp.McpServerInfo.ServerName, Version = CodeDiffer.Mcp.McpServerInfo.Version })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();
