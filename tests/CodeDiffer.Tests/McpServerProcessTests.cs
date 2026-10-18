using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeDiffer.Tests;

/// <summary>
/// CodeDiffer.Mcp.exe end to end over stdio, as an agent host starts it: the initialize handshake, tools/list (the 13
/// tools, no CancellationToken leaking into a schema) and a real tools/call of a compare.
/// </summary>
public sealed class McpServerProcessTests : IDisposable
{
    private readonly TempDir _t = new();

    public void Dispose() => _t.Dispose();

    private static readonly string[] ToolNames =
    [
        "start_compare", "start_compare3", "get_summary", "list_files", "get_file_diff", "get_stats", "export_changeset",
        "apply_changeset", "list_compares", "get_run_telemetry", "cancel_compare", "write_merge", "write_report",
    ];

    [Fact]
    public async Task Stdio_Initialize_ListTools_CallATool()
    {
        _t.Put("L/a.txt", "one\ntwo\n");
        _t.Put("R/a.txt", "one\nTWO\n");
        var psi = new ProcessStartInfo(ExeRunner.Exe("CodeDiffer.Mcp.exe"))
        {
            WorkingDirectory = _t.Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        ExeRunner.Isolate(psi, _t.P("state"));
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        // Longer than the compare's own wait_seconds below, with room for a slow start on a busy box.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try
        {
            async Task Send(object message)
            {
                await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
                await p.StandardInput.FlushAsync();
            }

            async Task<JsonElement> Reply(int id)
            {
                while (true)
                {
                    var line = await p.StandardOutput.ReadLineAsync(timeout.Token);
                    Assert.True(line is not null, "the server closed stdout; stderr: " + (p.HasExited ? await stderr : ""));
                    if (line.Trim().Length == 0) continue;
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var got) && got.ValueKind == JsonValueKind.Number && got.GetInt32() == id)
                        return doc.RootElement.Clone();
                }
            }

            await Send(new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "codediffer-tests", version = "1.0" } },
            });
            var init = await Reply(1);
            Assert.False(init.TryGetProperty("error", out _), init.ToString());
            var info = init.GetProperty("result").GetProperty("serverInfo");
            Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("name").GetString()));
            Assert.True(init.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out _));

            await Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
            await Send(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
            var list = await Reply(2);
            var tools = list.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
            Assert.Equal(ToolNames.Order(), tools.Select(t => t.GetProperty("name").GetString()!).Order());
            foreach (var tool in tools)
            {
                var name = tool.GetProperty("name").GetString();
                Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()), name);
                var schema = tool.GetProperty("inputSchema");
                var raw = schema.GetRawText();
                Assert.DoesNotContain("cancellationToken", raw, StringComparison.OrdinalIgnoreCase);
                if (schema.TryGetProperty("properties", out var props))
                    foreach (var prop in props.EnumerateObject())
                        Assert.True(prop.Value.TryGetProperty("description", out _), $"{name}.{prop.Name} has no description");
            }
            // The required parameters are the ones without a default.
            var start = tools.Single(t => t.GetProperty("name").GetString() == "start_compare").GetProperty("inputSchema");
            Assert.Equal(["left", "right"], start.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order());

            await Send(new
            {
                jsonrpc = "2.0", id = 3, method = "tools/call",
                @params = new { name = "start_compare", arguments = new { left = _t.P("L"), right = _t.P("R"), cache = "off", wait_seconds = 60 } },
            });
            var call = await Reply(3);
            var text = string.Concat(call.GetProperty("result").GetProperty("content").EnumerateArray()
                .Where(c => c.GetProperty("type").GetString() == "text").Select(c => c.GetProperty("text").GetString()));
            Assert.Contains("finished in", text);
            Assert.Contains("modified", text);
            Assert.Contains(_t.P("state"), text); // saved under the child's own results root

            await Send(new
            {
                jsonrpc = "2.0", id = 4, method = "tools/call",
                @params = new { name = "get_file_diff", arguments = new { path = "a.txt" } },
            });
            var diff = await Reply(4);
            Assert.Contains("-two\n+TWO\n", diff.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        }
        finally
        {
            p.StandardInput.Close(); // end of input: the server exits
            if (!p.WaitForExit(15_000)) p.Kill(entireProcessTree: true);
        }
    }
}
