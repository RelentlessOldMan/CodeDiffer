using CodeDiffer.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// CodeDiffer.Mcp.exe — the agent-facing stdio MCP server. stdout is the JSON-RPC transport, so every log
// line goes to stderr. Compares run in the background inside this process and are queried by id.
Console.Error.WriteLine($"{CodeDiffer.Mcp.McpServerInfo.ServerName} MCP {CodeDiffer.Mcp.McpServerInfo.Version}: ready (stdio)");

// A crash is recorded in each running compare's run.json (numbers only), so a dead run says what killed it.
AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Session.RecordCrash(ex); };

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = CodeDiffer.Mcp.McpServerInfo.ServerName, Version = CodeDiffer.Mcp.McpServerInfo.Version })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
var app = builder.Build();
// The host closing the server (stdin closed, a stop signal) would otherwise end a running compare mid-read with nothing
// saved: stop it instead, so it keeps the hashes it read (a re-run picks up there) and records why it stopped.
app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(
    () => Session.StopAll("stopped: the MCP server was shut down", TimeSpan.FromSeconds(20)));
await app.RunAsync();
Session.StopAll("stopped: the MCP server was shut down", TimeSpan.FromSeconds(20));
