using System.Text;
using System.Text.Json;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Report;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The on-disk result store (save on finish, reopen by id without re-comparing, refuse to write inside a
/// compared tree, honest about unfinished results and files changed since) and the HTML report built from it
/// (lazy per-file chunks, caps with the whole diff linked, limits disclosed, everything JSON-escaped).
/// </summary>
public class ResultStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-store-" + Guid.NewGuid().ToString("N"));
    private string Results => Path.Combine(_dir, "results");
    private string L => Path.Combine(_dir, "L");
    private string R => Path.Combine(_dir, "R");
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public ResultStoreTests()
    {
        for (int i = 0; i < 6; i++)
        {
            Put($"L/src/f{i}.c", Lines(1, 30, $"f{i}"));
            Put($"R/src/f{i}.c", i < 3 ? Lines(1, 30, $"f{i}") : Lines(1, 9, $"f{i}") + "changed\n" + Lines(11, 30, $"f{i}"));
        }
        Put("L/big.txt", Lines(1, 4000));
        Put("R/big.txt", string.Concat(Enumerable.Range(1, 4000).Select(i => i % 5 == 0 ? $"edited {i}\n" : $"line {i}\n")));
        Put("L/gone.h", "x\n");
        Put("R/new.h", "</script><img src=x onerror=alert(1)> & 'quote'\n");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Put(string rel, string text)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(text));
    }

    private static string Lines(int from, int to, string prefix = "line") =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"{prefix} {i}\n"));

    private static T Done<T>(T s) where T : Session
    {
        Assert.True(s.Wait(TimeSpan.FromSeconds(60)));
        Assert.Null(s.Error);
        Assert.Null(s.SaveError);
        return s;
    }

    [Fact]
    public void FinishedCompare_IsSaved_AndReopensByIdWithoutRecomparing()
    {
        var s = Done(new SessionStore(resultsRoot: Results).Start(L, R, NoCache));
        Assert.NotNull(s.ResultDir);
        Assert.StartsWith(Results, s.ResultDir);
        Assert.Matches($@"\d{{8}}-\d{{6}}-{s.Id}$", s.ResultDir);
        using (var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(s.ResultDir!, "compare.json"))))
        {
            Assert.Equal("done", meta.RootElement.GetProperty("state").GetString());
            Assert.Equal(4, meta.RootElement.GetProperty("counts").GetProperty("modified").GetInt32()); // f3..f5 + big.txt
        }

        // A new store (a new server process) knows nothing in memory — the id reopens from disk.
        var fresh = new SessionStore(resultsRoot: Results);
        var back = Assert.IsType<CompareSession>(fresh.Get(s.Id));
        Assert.True(back.Reopened);
        Assert.Equal(s.Report!.Changes, back.Report!.Changes);
        Assert.Equal(s.Report.ComparedPairs, back.Report.ComparedPairs);
        Assert.Equal(s.Left, back.Left);
        Assert.Contains("saved compare from", AgentViews.Summary(back));
        Assert.Contains("+changed", AgentViews.FileDiff(back, "src/f3.c"));
        Assert.Same(back, fresh.Get(s.ResultDir)); // by directory too

        var listed = Assert.Single(ResultStore.List(Results));
        Assert.Equal((s.Id, "compare", "done"), (listed.Id, listed.Kind, listed.State));
    }

    /// <summary>V2 = a copy of L (the base) with one file replaced.</summary>
    private void MakeV2(string rel, string text)
    {
        foreach (var f in Directory.GetFiles(L, "*", SearchOption.AllDirectories))
            Put(Path.Combine("V2", Path.GetRelativePath(L, f)), File.ReadAllText(f));
        Put("V2/" + rel, text);
    }

    [Fact]
    public void ThreeWay_IsSaved_AndReopens()
    {
        MakeV2("src/f0.c", Lines(1, 30, "f0").Replace("f0 20\n", "v2 edit\n"));
        var s = Done(new SessionStore(resultsRoot: Results).Start3(L, R, Path.Combine(_dir, "V2"), NoCache));
        var back = Assert.IsType<Compare3Session>(new SessionStore(resultsRoot: Results).Get(s.Id));
        Assert.Equal(s.Report!.Entries, back.Report!.Entries);
        Assert.Equal(s.Report.V1Report.Changes, back.Report.V1Report.Changes);
        Assert.Equal(s.Report.Timings.Count, back.Report.Timings.Count);
        Assert.Contains(back.Report.Entries, e => e.Outcome == Merge3Outcome.V2Only && e.Path == "src/f0.c");
    }

    [Fact]
    public void ResultsInsideACompareTree_IsRefused()
    {
        var store = new SessionStore(resultsRoot: Path.Combine(L, "results"));
        var ex = Assert.Throws<ArgumentException>(() => store.Start(L, R, NoCache));
        Assert.Contains("inside the compared tree", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(L, "results")));
    }

    [Fact]
    public void UnfinishedResult_IsReportedNotLoaded()
    {
        var dir = ResultStore.CreateRunDir(Results, "beef", "compare", [L, R]); // as if the process died mid-compare
        var ex = Assert.Throws<InvalidDataException>(() => new SessionStore(resultsRoot: Results).Get("beef"));
        Assert.Contains("never finished", ex.Message);
        Assert.Equal("running", Assert.Single(ResultStore.List(Results)).State);
        Assert.Equal(dir, ResultStore.Find(Results, "beef"));
        Assert.Null(ResultStore.Find(Results, "*"));  // ids are hex only — never a wildcard
    }

    [Fact]
    public void FileChangedSinceTheCompare_IsFlagged()
    {
        var s = Done(new SessionStore(resultsRoot: Results).Start(L, R, NoCache));
        Put("R/src/f4.c", "rewritten after the compare\n");
        var back = (CompareSession)new SessionStore(resultsRoot: Results).Get(s.Id)!;
        Assert.Contains("WARNING: changed since the compare — the right file is now", AgentViews.FileDiff(back, "src/f4.c"));
        Assert.DoesNotContain("WARNING", AgentViews.FileDiff(back, "src/f3.c"));
    }

    [Fact]
    public void HtmlReport_WritesShellIndexAndOneChunkPerDiff_Escaped()
    {
        var s = Done(new SessionStore(resultsRoot: Results).Start(L, R, NoCache));
        var r = HtmlReport.Write(s, new HtmlReportOptions { MaxLines = 500 });
        var root = Path.GetDirectoryName(r.IndexPath)!;
        Assert.Equal(Path.Combine(s.ResultDir!, "report"), root);
        Assert.Equal(6, r.Files);      // f3..f5, big.txt, gone.h, new.h — identical not listed
        Assert.Equal(6, r.Rendered);
        Assert.Equal(1, r.Capped);     // big.txt: 1,600 changed lines > 500

        var index = Data(Path.Combine(root, "data", "index.js"), "CD.index(");
        var files = index.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(["big.txt", "gone.h", "new.h", "src/f3.c", "src/f4.c", "src/f5.c"], files.Select(f => f.GetProperty("p").GetString()));
        var big = files[0];
        Assert.Equal(800, big.GetProperty("a").GetInt32());
        Assert.Equal(6, Directory.GetFiles(Path.Combine(root, "data", "d")).Length);

        var chunk = Data(Path.Combine(root, "data", "d", "0.js"), "CD.diff(0,");
        Assert.Equal(500, chunk.GetProperty("shown").GetInt32());
        var full = Path.Combine(root, chunk.GetProperty("full").GetString()!);
        Assert.Contains("+edited 4000", File.ReadAllText(full)); // the whole diff, linked

        // A hostile line reaches the page only as an escaped JSON string.
        var raw = File.ReadAllText(Path.Combine(root, "data", "d", "2.js"));
        Assert.DoesNotContain("</script>", raw);
        Assert.DoesNotContain("<img", raw);
        Assert.Contains("</script><img", Data(Path.Combine(root, "data", "d", "2.js"), "CD.diff(2,").GetProperty("text").GetString());

        var html = File.ReadAllText(r.IndexPath);
        Assert.Contains("<script src=\"data/index.js\"", html);
        Assert.DoesNotContain("innerHTML", html);
        Assert.DoesNotContain("src/f3.c", html); // the shell holds no data

        // Regenerating replaces our own report; a foreign directory is never deleted.
        Assert.Equal(6, HtmlReport.Write(s).Rendered);
        var foreign = Path.Combine(_dir, "mine");
        Put("mine/keep.txt", "keep");
        Assert.Throws<IOException>(() => HtmlReport.Write(s, new HtmlReportOptions { OutDir = foreign }));
        Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
    }

    [Fact]
    public void HtmlReport_NeverDeletesAProjectThatMerelyLooksLikeOne()
    {
        var s = Done(new SessionStore(resultsRoot: Results).Start(L, R, NoCache));
        // A JS project with data\index.js of its own: not a report, so it is refused, not deleted.
        Put("proj/data/index.js", "export default 1;\n");
        Put("proj/src/precious.cs", "keep\n");
        var proj = Path.Combine(_dir, "proj");
        Assert.Throws<IOException>(() => HtmlReport.Write(s, new HtmlReportOptions { OutDir = proj }));
        Assert.True(File.Exists(Path.Combine(proj, "src", "precious.cs")));
        // Even one holding only index.html/data/full, unless data\index.js is ours.
        File.Delete(Path.Combine(proj, "src", "precious.cs"));
        Directory.Delete(Path.Combine(proj, "src"));
        Put("proj/index.html", "<p>mine</p>\n");
        Assert.Throws<IOException>(() => HtmlReport.Write(s, new HtmlReportOptions { OutDir = proj }));
        Assert.True(File.Exists(Path.Combine(proj, "index.html")));

        // Inside a compared tree, or holding one: refused before anything is touched.
        Assert.Throws<IOException>(() => HtmlReport.Write(s, new HtmlReportOptions { OutDir = Path.Combine(R, "report") }));
        Assert.False(Directory.Exists(Path.Combine(R, "report")));
        Assert.Throws<IOException>(() => HtmlReport.Write(s, new HtmlReportOptions { OutDir = _dir }));
        Assert.True(Directory.Exists(L));

        // A report a stopped writer left half done (no data\index.js yet) is still ours to replace.
        var out1 = Path.Combine(_dir, "rep");
        HtmlReport.Write(s, new HtmlReportOptions { OutDir = out1 });
        File.Delete(Path.Combine(out1, "data", "index.js"));
        Assert.Equal(6, HtmlReport.Write(s, new HtmlReportOptions { OutDir = out1 }).Rendered);
        // And a report from before the marker (index.html, data\, full\ only) is replaced too.
        File.Delete(Path.Combine(out1, HtmlReport.Marker));
        Assert.Equal(6, HtmlReport.Write(s, new HtmlReportOptions { OutDir = out1 }).Rendered);
    }

    [Fact]
    public void HtmlReport_DisclosesLimits()
    {
        var s = Done(new SessionStore(resultsRoot: Results).Start(L, R, NoCache));
        var r = HtmlReport.Write(s, new HtmlReportOptions { MaxDiffs = 2, IncludeIdentical = true });
        Assert.Equal(9, r.Files);  // + 3 identical
        Assert.Equal(2, r.Rendered);
        Assert.Equal(4, r.NotRendered);
        var files = Data(Path.Combine(Path.GetDirectoryName(r.IndexPath)!, "data", "index.js"), "CD.index(").GetProperty("files").EnumerateArray().ToList();
        Assert.Contains(files, f => f.TryGetProperty("n", out var n) && n.GetString()!.Contains("report limit 2"));
        Assert.Equal(3, files.Count(f => f.GetProperty("t").GetString() == "="));
    }

    [Fact]
    public void HtmlReport_ThreeWay_CarriesTheMergeWithMarkers()
    {
        MakeV2("src/f3.c", Lines(1, 9, "f3") + "other\n" + Lines(11, 30, "f3"));
        var s = Done(new SessionStore(resultsRoot: Results).Start3(L, R, Path.Combine(_dir, "V2"), NoCache));
        var r = HtmlReport.Write(s);
        var root = Path.GetDirectoryName(r.IndexPath)!;
        var index = Data(Path.Combine(root, "data", "index.js"), "CD.index(");
        Assert.Equal("compare3", index.GetProperty("kind").GetString());
        var files = index.GetProperty("files").EnumerateArray().ToList();
        int cf = files.FindIndex(f => f.GetProperty("t").GetString() == "CF");
        Assert.Equal("src/f3.c", files[cf].GetProperty("p").GetString());
        var chunk = Data(Path.Combine(root, "data", "d", $"{cf}.js"), $"CD.diff({cf},");
        Assert.Equal("merge", chunk.GetProperty("mode").GetString());
        Assert.Equal(1, chunk.GetProperty("blocks").GetInt32());
        var seg = chunk.GetProperty("segs")[0];
        Assert.Equal(7, seg[0].GetInt32()); // the block at line 10, with 3 lines of context
        Assert.StartsWith("f3 7\nf3 8\nf3 9\n<<<<<<< v1", seg[1].GetString());
        Assert.False(chunk.TryGetProperty("full", out var full) && full.ValueKind != JsonValueKind.Null); // every block fits
    }

    [Fact]
    public void HtmlReport_CleanMerge_IsShownAsBaseToMergedDiff()
    {
        MakeV2("src/f4.c", Lines(1, 24, "f4") + "v2 tail\n" + Lines(26, 30, "f4")); // v1 edits line 10, v2 line 25
        var s = Done(new SessionStore(resultsRoot: Results).Start3(L, R, Path.Combine(_dir, "V2"), NoCache));
        var r = HtmlReport.Write(s);
        var root = Path.GetDirectoryName(r.IndexPath)!;
        var files = Data(Path.Combine(root, "data", "index.js"), "CD.index(").GetProperty("files").EnumerateArray().ToList();
        int mg = files.FindIndex(f => f.GetProperty("t").GetString() == "MG");
        var text = Data(Path.Combine(root, "data", "d", $"{mg}.js"), $"CD.diff({mg},").GetProperty("text").GetString()!;
        Assert.Contains("-f4 10\n+changed\n", text);
        Assert.Contains("-f4 25\n+v2 tail\n", text);
    }

    [Fact]
    public void Condense_KeepsConflictBlocksWithContext_AndStopsAtTheCap()
    {
        var lines = new List<string>();
        for (int b = 0; b < 5; b++)
        {
            lines.AddRange(Enumerable.Range(0, 100).Select(i => $"plain {b}.{i}"));
            lines.AddRange(["<<<<<<< v1 (x:1)", "a", "||||||| base (x:1)", "b", "=======", "c", ">>>>>>> v2 (x:1)"]);
        }
        var arr = lines.Append("").ToArray();
        var all = HtmlReport.Condense(arr, lines.Count, 3, 1000);
        Assert.Equal((5, 5), (all.Blocks, all.ShownBlocks));
        Assert.Equal(5, all.Segments.Count);
        Assert.Equal(98, all.Segments[0].Start);   // 1-based: block at line 101, 3 lines of context
        Assert.Equal(4 * 13 + 10, all.Kept);       // the last block ends the file: no trailing context
        var cut = HtmlReport.Condense(arr, lines.Count, 3, 30);
        Assert.Equal(2, cut.ShownBlocks);          // 13 + 13 fits in 30, a third block would not
    }

    /// <summary>Parse a data file's JSON payload: <c>{prefix}{json});</c>.</summary>
    private static JsonElement Data(string path, string prefix)
    {
        var text = File.ReadAllText(path).TrimEnd();
        Assert.StartsWith(prefix, text);
        Assert.EndsWith(");", text);
        return JsonDocument.Parse(text[prefix.Length..^2]).RootElement.Clone();
    }
}
