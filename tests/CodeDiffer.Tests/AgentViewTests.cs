using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The agent-facing views behind the MCP server: background compare by id, constant-size summary, paged and
/// filtered file lists, capped per-file diffs (with the overflow written to a .patch file), and the export.
/// </summary>
public class AgentViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-views-" + Guid.NewGuid().ToString("N"));
    private readonly string _left, _right;

    public AgentViewTests()
    {
        _left = Path.Combine(_dir, "L");
        _right = Path.Combine(_dir, "R");
        Environment.SetEnvironmentVariable("CODEDIFFER_OUT_DIR", Path.Combine(_dir, "out"));
        for (int i = 0; i < 120; i++)
        {
            Put($"L/src/f{i:D3}.c", Lines(1, 20, $"f{i}"));
            Put($"R/src/f{i:D3}.c", i % 2 == 0 ? Lines(1, 20, $"f{i}") : Lines(1, 10, $"f{i}") + "changed\n" + Lines(12, 20, $"f{i}"));
        }
        Put("L/big.txt", Lines(1, 5000));
        Put("R/big.txt", string.Concat(Enumerable.Range(1, 5000).Select(i => i % 10 == 0 ? $"edited {i}\n" : $"line {i}\n")));
        Put("L/gone.h", "x\n");
        Put("R/new.h", "y\n");
        Put("L/eol.txt", "a\nb\n");
        Put("R/eol.txt", "a\r\nb\r\n");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEDIFFER_OUT_DIR", null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Put(string rel, string text)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(text));
    }

    private static string Lines(int from, int to, string prefix = "line") =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"{prefix} {i}\n"));

    private CompareSession Run(SessionStore store)
    {
        var s = store.Start(_left, _right, new CompareOptions { Cache = CacheMode.Off });
        Assert.True(s.Wait(TimeSpan.FromSeconds(60)));
        Assert.Null(s.Error);
        return s;
    }

    [Fact]
    public void Summary_CountsEveryStatus_AndLatestIsDefault()
    {
        var store = new SessionStore(save: false);
        var s = Run(store);
        Assert.Same(s, store.Get(null));
        Assert.Same(s, store.Get(s.Id.ToUpperInvariant()));
        var text = AgentViews.Summary(s);
        Assert.Contains("identical       60", text);
        Assert.Contains("modified        62", text); // 60 .c + big.txt + eol.txt
        Assert.Contains("eol 1", text);
        Assert.Contains("added            1", text);
        Assert.Contains("removed          1", text);
        Assert.DoesNotContain("src/f000.c", text); // constant size: no file list
    }

    [Fact]
    public void ListFiles_Pages_Filters_AndCountsLines()
    {
        var s = Run(new SessionStore(save: false));
        var p1 = AgentViews.ListFiles(s, pageSize: 50);
        Assert.Contains("64 file(s) match · page 1/2", p1);
        Assert.Contains("next page: page=2", p1);
        Assert.DoesNotContain("f000.c", p1); // identical hidden by default

        var hs = AgentViews.ListFiles(s, pathGlob: "*.h");
        Assert.Contains("2 file(s) match", hs);
        Assert.Contains("A new.h", hs);
        Assert.Contains("D gone.h", hs);

        var src = AgentViews.ListFiles(s, pathGlob: "src/**", status: "modified", pageSize: 5, lines: true);
        Assert.Contains("60 file(s) match", src);
        Assert.Contains("M src/f001.c  [content]", src);
        Assert.Contains("+1 -1 (1 hunk(s))", src);

        Assert.Contains("1 file(s) match", AgentViews.ListFiles(s, reason: "eol"));
        Assert.StartsWith("error:", AgentViews.ListFiles(s, status: "bogus"));
        Assert.Contains("f000.c", AgentViews.ListFiles(s, status: "identical"));
    }

    [Fact]
    public void FileDiff_SmallIsInline_LargeIsCappedToAPatchFile()
    {
        var s = Run(new SessionStore(save: false));
        var small = AgentViews.FileDiff(s, "src\\f001.c");
        Assert.Contains("+1 -1 in 1 hunk(s)", small);
        Assert.Contains("-f1 11", small);
        Assert.Contains("+changed", small);

        var big = AgentViews.FileDiff(s, "big.txt", maxLines: 100);
        Assert.Contains("capped: showing lines 1-100 of", big);
        Assert.Contains("next: startLine=101", big);
        Assert.Contains("hunks (", big);
        int at = big.IndexOf("full patch: ", StringComparison.Ordinal) + "full patch: ".Length;
        var file = big[at..big.IndexOf('\n', at)];
        Assert.True(File.Exists(file), file);
        Assert.Contains("+edited 5000", File.ReadAllText(file));

        var page2 = AgentViews.FileDiff(s, "big.txt", maxLines: 100, startLine: 101);
        Assert.Contains("showing lines 101-200", page2);

        Assert.Contains("identical", AgentViews.FileDiff(s, "src/f000.c"));
        Assert.Contains("did you mean: src/f001.c", AgentViews.FileDiff(s, "f001.c"));
    }

    [Fact]
    public void Export_WritesAPatchFile_AndStatsReportRenderedMovers()
    {
        var s = Run(new SessionStore(save: false));
        AgentViews.FileDiff(s, "big.txt");
        var stats = AgentViews.Stats(s);
        Assert.Contains("most-changed lines", stats);
        Assert.Contains("+500 -500", stats);
        Assert.Contains("read cost:", stats);

        var outFile = Path.Combine(_dir, "x", "all.patch");
        var exp = AgentViews.Export(s, outFile, literal: true);
        Assert.Contains(outFile, exp);
        var patch = File.ReadAllText(outFile);
        Assert.Contains("diff --git a/new.h b/new.h", patch);
        Assert.Contains("diff --git a/eol.txt b/eol.txt", patch);
        Assert.DoesNotContain("diff --git a/src/f000.c", patch);
    }

    [Fact]
    public void BadRoot_IsALoudError_AndRunningIsReported()
    {
        var store = new SessionStore(save: false);
        Assert.ThrowsAny<Exception>(() => store.Start(Path.Combine(_dir, "nope"), _right));
        var s = store.Start(_left, _right, new CompareOptions { Cache = CacheMode.Off });
        var text = AgentViews.Summary(s); // may or may not have finished yet — either form is valid
        Assert.True(text.Contains("running") || text.Contains("files   (finished"), text);
        s.Wait(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Store_EvictsOldestFinished_PastCapacity()
    {
        var store = new SessionStore(capacity: 2, save: false);
        var a = Run(store);
        Run(store);
        Run(store);
        Assert.Equal(2, store.All().Count);
        Assert.Null(store.Get(a.Id));
    }

    [Fact]
    public void Glob_SegmentsAndNames()
    {
        Assert.True(AgentViews.Glob("*.c")("a/b/x.c"));
        Assert.False(AgentViews.Glob("src/*.c")("src/sub/x.c"));
        Assert.True(AgentViews.Glob("src/**/*.c")("src/sub/x.c"));
        Assert.True(AgentViews.Glob("src/**/*.c")("src/x.c"));
        Assert.True(AgentViews.Glob("SRC/**")("src/a/b"));
        Assert.False(AgentViews.Glob("src/**")("lib/a"));
    }
}
