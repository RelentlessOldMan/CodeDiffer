// CodeDiffer.Mcp.exe — the agent-facing stdio MCP server (tool surface: McpServerInfo.PlannedTools).
// The server itself is the next build step; until then this exe says so instead of pretending to serve.
Console.Error.WriteLine($"{CodeDiffer.Mcp.McpServerInfo.ServerName} MCP server: not implemented yet — use CodeDiffer.Cli.exe.");
return 1;
