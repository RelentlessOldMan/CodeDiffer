using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeDiffer.Tests;

/// <summary>The tests that start an MCP server and wait on its replies run one at a time, and after the rest, alone: on
/// one core, the selftest's children (and then the other test classes) starved the stdio test's server past its 180 s wait.</summary>
[CollectionDefinition("ServerProcesses", DisableParallelization = true)]
public sealed class ServerProcessesCollection;

/// <summary>
/// <c>codediffer selftest</c> run as a user runs it, on small invented trees: it passes on trees that are what they
/// are, writes its report, json and history, leaves the trees as they were, and refuses an output inside a tree.
/// </summary>
[Collection("ServerProcesses")]
public sealed class SelfTestTests : IDisposable
{
    private readonly TempDir _t = new();
    private string State => _t.P("state");

    public void Dispose() => _t.Dispose();

    private ExeResult Cli(params string[] args) => ExeRunner.Run(ExeRunner.Exe("CodeDiffer.Cli.exe"), _t.Root, State, args, 300_000);

    /// <summary>base → v1 and base → v2: every status, every reason, a merge, a conflict.</summary>
    private void Trees()
    {
        foreach (var v in new[] { "base", "v1", "v2" })
        {
            _t.Put($"{v}/src/same.c", TempDir.Lines(1, 40));
            _t.Put($"{v}/docs/guide.md", "# Guide\n\nRead me.\n");
        }
        _t.Put("base/src/engine.cs", TempDir.Lines(1, 60, "op"));
        _t.Put("v1/src/engine.cs", TempDir.Lines(1, 60, "op").Replace("op 10\n", "op ten\n"));
        _t.Put("v2/src/engine.cs", TempDir.Lines(1, 60, "op").Replace("op 50\n", "op fifty\n"));
        _t.Put("base/src/eol.h", "int a;\nint b;\n");
        _t.Put("v1/src/eol.h", "int a;\r\nint b;\r\n");
        _t.Put("v2/src/eol.h", "int a;\nint b;\n");
        _t.Put("base/src/ws.py", "def f():\n    return 1\n");
        _t.Put("v1/src/ws.py", "def f():\n    return  1   \n");
        _t.Put("v2/src/ws.py", "def f():\n    return 1\n");
        _t.Put("base/build/proj.mk", "all:\n\tcc x.c\n");
        _t.Put("v1/build/proj.mk", "all:\n\tcc -O2 x.c\n");
        _t.Put("v2/build/proj.mk", "all:\n\tcc -O3 x.c\n");
        _t.Put("base/old.txt", "gone soon\n");
        _t.Put("v2/old.txt", "gone soon\n");
        _t.Put("v1/new.txt", "fresh\n");
        _t.Put("v2/new.txt", "fresh\n");
        _t.Put("base/src/util.cs", TempDir.Lines(1, 30, "u"));
        _t.Put("v1/src/helpers.cs", TempDir.Lines(1, 30, "u").Replace("u 3\n", "u three\n"));
        _t.Put("v2/src/util.cs", TempDir.Lines(1, 30, "u"));
        _t.Put("v2/src/only2.cs", "class Only2 {}\n");
        for (int i = 0; i < 1005; i++) // over a thousand changes: the tools print 1,005, which the checks must read
        {
            _t.Put($"base/gen/t{i}.txt", $"value {i}\n");
            _t.Put($"v1/gen/t{i}.txt", $"value {i + 1}\n");
            _t.Put($"v2/gen/t{i}.txt", $"value {i}\n");
        }
        foreach (var v in new[] { "base", "v1", "v2" })
            _t.PutBytes($"{v}/art/logo.bin", v == "v1" ? [0, 1, 2, 3, 9] : [0, 1, 2, 3, 4]);
    }

    [Fact]
    public void ThreeTrees_PassAndWriteTheReport()
    {
        Trees();
        var before = new[] { "base", "v1", "v2" }.Select(v => TempDir.Snapshot(_t.P(v))).ToList();
        var outDir = _t.P("st");
        var r = Cli("selftest", _t.P("base"), _t.P("v1"), _t.P("v2"), "--name", "demo app", "--out", outDir, "--threads", "1");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.DoesNotContain("FAIL ", r.Stdout);
        Assert.Contains("selftest PASSED", r.Stdout);

        var run = Directory.GetDirectories(Path.Combine(outDir, "demo_app")).Single();
        var html = File.ReadAllText(Path.Combine(run, "index.html"));
        Assert.Contains("PASSED", html);
        Assert.Contains("start_compare3 gives the CLI&#39;s merge", html);
        Assert.Contains("diff patches turn left into right", html);
        Assert.DoesNotContain("<script", html);

        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run, "selftest.json")));
        Assert.Equal("codediffer-selftest", json.RootElement.GetProperty("format").GetString());
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToList();
        Assert.True(checks.Count >= 30, $"{checks.Count} checks");
        Assert.DoesNotContain(checks, c => c.GetProperty("verdict").GetString() == "fail");
        foreach (var must in new[] { "warm compare gives the same verdicts", "export_changeset turns left into right", "write_merge is the CLI's overlay",
                                     "apply onto right: everything already there", "overlay holds what the merge says" })
            Assert.Contains(checks, c => c.GetProperty("Name").GetString() == must && c.GetProperty("verdict").GetString() == "pass");
        Assert.True(json.RootElement.GetProperty("metrics").GetArrayLength() >= 4);
        Assert.Single(File.ReadAllLines(Path.Combine(outDir, "demo_app", "history.jsonl")));

        for (int i = 0; i < 3; i++) Assert.Equal(before[i], TempDir.Snapshot(_t.P(new[] { "base", "v1", "v2" }[i])));
    }

    [Fact]
    public void AnOutputInsideATree_IsRefused()
    {
        Trees();
        var r = Cli("selftest", _t.P("base"), _t.P("v1"), "--out", _t.P("v1/reports"));
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("inside a compared tree", r.Stderr);
        Assert.False(Directory.Exists(_t.P("v1/reports")));
    }

    [Fact]
    public void Usage_IsExit64()
    {
        Assert.Equal(64, Cli("selftest", _t.Root).ExitCode);
        Assert.Equal(64, Cli("selftest", _t.Root, _t.Root, "--sample", "0").ExitCode);
        Assert.Equal(64, Cli("selftest", _t.Root, _t.Root, "--bogus").ExitCode);
    }

    [Fact]
    public void TheApplier_RoundTripsWhatItIsGiven()
    {
        const string left = "a\r\nb\nc\nd";
        const string patch = "--- a/x\n+++ b/x\n@@ -1,4 +1,4 @@\n-a\r\n+A\r\n b\n c\n-d\n\\ No newline at end of file\n+d\n";
        Assert.Equal("A\r\nb\nc\nd\n", CodeDiffer.Cli.PatchApplier.Apply(left, CodeDiffer.Cli.PatchApplier.Lines(patch), out var why));
        Assert.Null(why);
        Assert.Null(CodeDiffer.Cli.PatchApplier.Apply("z\nb\n", CodeDiffer.Cli.PatchApplier.Lines("@@ -1 +1 @@\n-a\n+A\n"), out why));
        Assert.NotNull(why);
    }
}
