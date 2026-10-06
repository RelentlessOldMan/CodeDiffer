using System.Reflection;

namespace CodeDiffer.Mcp;

public static class McpServerInfo
{
    public const string ServerName = "codediffer";

    public static string Version { get; } =
        typeof(McpServerInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
