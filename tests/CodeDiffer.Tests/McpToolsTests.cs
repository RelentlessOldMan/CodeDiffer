using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Mcp;
using ModelContextProtocol.Server;

namespace CodeDiffer.Tests;

/// <summary>The MCP tool class holds one static session store: its tests run one at a time, and with nothing else.</summary>
[CollectionDefinition("McpTools", DisableParallelization = true)]
public sealed class McpToolsCollection;

/// <summary>
/// The 13 MCP tools (CodeDifferTools), called in-process as the server calls them: background compares by id, the
/// constant-size summary, paged and filtered listings, capped per-file diffs with the overflow in a file, stats, export,
/// apply, the merge overlay, the HTML report, saved compares reopened by id, and cancel. A bad input is an answer
/// ("error: ..."), never an exception. The store saves under the per-run temp results root (ResultsIsolation).
/// </summary>
[Collection("McpTools")]
public sealed class McpToolsTests : IDisposable
{
    private readonly TempDir _t = new();

    public void Dispose() => _t.Dispose();

    private static SessionStore Store =>
        (SessionStore)typeof(CodeDifferTools).GetField("Sessions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    /// <summary>Forget every compare in memory (as a restarted server would): ids then reopen from the saved results.</summary>
    private static void ClearSessions()
    {
        var store = Store;
        var gate = typeof(SessionStore).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        lock (gate)
        {
            ((System.Collections.IList)typeof(SessionStore).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!).Clear();
            typeof(SessionStore).GetField("_lastStarted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(store, null);
        }
    }

    private static string Id(string text)
    {
        var m = Regex.Match(text, @"compare3? ([0-9a-f]{4})\b");
        Assert.True(m.Success, text);
        return m.Groups[1].Value;
    }

    private void TwoWay()
    {
        _t.Put("L/same.txt", "a\nb\nc\n");
        _t.Put("R/same.txt", "a\nb\nc\n");
        _t.Put("L/src/content.txt", "one\ntwo\nthree\n");
        _t.Put("R/src/content.txt", "one\nTWO\nthree\n");
        _t.Put("L/eol.txt", "x\ny\n");
        _t.Put("R/eol.txt", "x\r\ny\r\n");
        _t.Put("L/ws.txt", "p  q\nr\n");
        _t.Put("R/ws.txt", "p q\nr\n");
        _t.PutBytes("L/blob.bin", [0, 1, 2, 9, 9, 9]);
        _t.PutBytes("R/blob.bin", [0, 1, 3, 9, 9, 9]);
        _t.Put("L/gone.txt", "gone\n");
        _t.Put("R/src/fresh.txt", "fresh\n");
        _t.Put("L/sub/old.txt", TempDir.Lines(1, 30, "rename line"));
        _t.Put("R/sub/moved.txt", TempDir.Lines(1, 30, "rename line"));
        _t.Put("L/big.txt", TempDir.Lines(1, 3000));
        _t.Put("R/big.txt", string.Concat(Enumerable.Range(1, 3000).Select(i => i % 10 == 0 ? $"edited {i}\n" : $"line {i}\n")));
    }

    private void ThreeWay()
    {
        var merge = TempDir.Lines(1, 20);
        _t.Put("B/merge.txt", merge);
        _t.Put("V1/merge.txt", merge.Replace("line 2\n", "line 2 v1\n"));
        _t.Put("V2/merge.txt", merge.Replace("line 18\n", "line 18 v2\n"));
        _t.Put("B/conf.txt", "c1\nc2\nc3\n");
        _t.Put("V1/conf.txt", "c1\nV1\nc3\n");
        _t.Put("V2/conf.txt", "c1\nV2\nc3\n");
        _t.Put("B/only1.txt", "o\n");
        _t.Put("V1/only1.txt", "o1\n");
        _t.Put("V2/only1.txt", "o\n");
        _t.Put("B/only2.txt", "o\n");
        _t.Put("V1/only2.txt", "o\n");
        _t.Put("V2/only2.txt", "o2\n");
        _t.Put("B/agree.txt", "g\n");
        _t.Put("V1/agree.txt", "G\n");
        _t.Put("V2/agree.txt", "G\n");
        _t.Put("V1/addadd.txt", "aa1\n");
        _t.Put("V2/addadd.txt", "aa2\n");
        _t.Put("B/delmod.txt", "dm\n");
        _t.Put("V2/delmod.txt", "dm changed\n");
        _t.Put("B/del2.txt", "d\n");
        _t.Put("V1/del2.txt", "d\n");
    }

    private async Task<string> Compare2()
    {
        TwoWay();
        var text = await CodeDifferTools.StartCompare(_t.P("L"), _t.P("R"), cache: "off", wait_seconds: 60);
        Assert.Contains("finished in", text);
        return Id(text);
    }

    private async Task<string> Compare3()
    {
        ThreeWay();
        var text = await CodeDifferTools.StartCompare3(_t.P("B"), _t.P("V1"), _t.P("V2"), cache: "off", wait_seconds: 60);
        Assert.Contains("finished in", text);
        return Id(text);
    }

    private static List<string> Rows(string listing) =>
        listing.Split('\n').Skip(1).Where(l => l.Length > 0 && !l.StartsWith("next page") && !l.StartsWith("partial")).ToList();

    // ---------------------------------------------------------------- the tool surface

    private static readonly string[] ToolNames =
    [
        "start_compare", "start_compare3", "get_summary", "list_files", "get_file_diff", "get_stats", "export_changeset",
        "apply_changeset", "list_compares", "get_run_telemetry", "cancel_compare", "write_merge", "write_report",
    ];

    [Fact]
    public void Tools_AreTheThirteen_EachDescribed_EveryParameterToo()
    {
        Assert.NotNull(typeof(CodeDifferTools).GetCustomAttribute<McpServerToolTypeAttribute>());
        var tools = typeof(CodeDifferTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (m, a: m.GetCustomAttribute<McpServerToolAttribute>())).Where(x => x.a is not null).ToList();
        Assert.Equal(ToolNames.Order(), tools.Select(x => x.a!.Name!).Order());
        foreach (var (m, _) in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.GetCustomAttribute<DescriptionAttribute>()?.Description), m.Name);
            foreach (var p in m.GetParameters())
            {
                if (p.ParameterType == typeof(CancellationToken))
                {
                    Assert.True(p.HasDefaultValue, $"{m.Name}({p.Name})");
                    continue;
                }
                Assert.False(string.IsNullOrWhiteSpace(p.GetCustomAttribute<DescriptionAttribute>()?.Description), $"{m.Name}({p.Name}) has no [Description]");
            }
            Assert.True(m.ReturnType == typeof(string) || m.ReturnType == typeof(Task<string>), m.Name);
        }
    }

    // ---------------------------------------------------------------- start_compare, start_compare3, get_summary

    [Fact]
    public async Task StartCompare_WaitsForTheSummary()
    {
        TwoWay();
        var text = await CodeDifferTools.StartCompare(_t.P("L"), _t.P("R"), cache: "off", threads: 2, wait_seconds: 60);
        Assert.StartsWith("compare ", text);
        Assert.Contains("finished in", text);
        Assert.Matches(@"identical\s+1\s", text);
        Assert.Matches(@"modified\s+5\s", text); // content.txt, eol.txt, ws.txt, blob.bin, big.txt
        Assert.Matches(@"added\s+1\n", text);
        Assert.Matches(@"removed\s+1\n", text);
        Assert.Matches(@"renamed\s+1\s+\(1 pure, 0 rename\+edit\)", text);
        Assert.Contains("eol 1", text);
        Assert.Contains("whitespace 1", text);
        Assert.Contains("binary 1", text);
        Assert.Contains("saved: " + ResultsIsolation.Root, text);
        Assert.Contains("next: list_files", text);
    }

    [Fact]
    public async Task StartCompare_BadInputs_AreAnswers()
    {
        _t.Dir("L");
        var missing = await CodeDifferTools.StartCompare(_t.P("L"), _t.P("nowhere"), wait_seconds: 0);
        Assert.StartsWith("error:", missing);
        Assert.Contains("nowhere", missing);
        Assert.StartsWith("error:", await CodeDifferTools.StartCompare("", _t.P("L"), wait_seconds: 0));
        Assert.StartsWith("error:", await CodeDifferTools.StartCompare(_t.P("L") + "\0x", _t.P("L"), wait_seconds: 0));
        var cache = await CodeDifferTools.StartCompare(_t.P("L"), _t.P("L"), cache: "sometimes", wait_seconds: 0);
        Assert.Equal("error: cache must be on, off or rehash (got 'sometimes')", cache);
        Assert.StartsWith("error: cache must be", await CodeDifferTools.StartCompare3(_t.P("L"), _t.P("L"), _t.P("L"), cache: "maybe"));
        Assert.StartsWith("error:", await CodeDifferTools.StartCompare3(_t.P("L"), _t.P("nowhere"), _t.P("L")));
    }

    [Fact]
    public async Task StartCompare_AnyThreadCountAndCacheSpelling_Runs()
    {
        _t.Put("L/a.txt", "a\n");
        _t.Put("R/a.txt", "b\n");
        int local = CompareOptions.LocalThreads; // local trees: no thread count given is the local default
        foreach (var (cache, threads, used) in new[] { ("ON", -3, local), (" rehash ", 1000, 64), ("", 0, local), ("Off", 1, 1) })
        {
            var text = await CodeDifferTools.StartCompare(_t.P("L"), _t.P("R"), cache: cache, threads: threads, wait_seconds: 60);
            Assert.Contains("finished in", text);
            Assert.Matches(@"modified\s+1\s", text);
            Assert.Contains($"threads {used} ·", CodeDifferTools.GetStats(Id(text)));
        }
    }

    [Fact]
    public async Task StartCompare_NoWait_ThenGetSummaryWaits()
    {
        TwoWay();
        var started = await CodeDifferTools.StartCompare(_t.P("L"), _t.P("R"), cache: "off", wait_seconds: 0);
        var id = Id(started);
        var summary = await CodeDifferTools.GetSummary(id, wait_seconds: 60);
        Assert.StartsWith($"compare {id} ", summary);
        Assert.Contains("finished in", summary);
    }

    [Fact]
    public async Task GetSummary_OmittedIdIsTheMostRecent_UnknownIdIsAnError()
    {
        _t.Put("L/a.txt", "a\n");
        _t.Put("R/a.txt", "b\n");
        var first = Id(await CodeDifferTools.StartCompare(_t.P("L"), _t.P("R"), cache: "off"));
        var second = Id(await CodeDifferTools.StartCompare(_t.P("R"), _t.P("L"), cache: "off"));
        Assert.NotEqual(first, second);
        Assert.StartsWith($"compare {second} ", await CodeDifferTools.GetSummary());
        Assert.StartsWith($"compare {first} ", await CodeDifferTools.GetSummary(first.ToUpperInvariant()));
        // Reopening the older by id doesn't change what "most recent" means.
        Assert.StartsWith($"compare {second} ", await CodeDifferTools.GetSummary(null));

        var unknown = await CodeDifferTools.GetSummary("zzzz");
        Assert.StartsWith("unknown compare_id 'zzzz'", unknown);
        Assert.Contains(second, unknown);
        foreach (var answer in new[] { CodeDifferTools.ListFiles("zzzz"), CodeDifferTools.GetFileDiff("a.txt", "zzzz"), CodeDifferTools.GetStats("zzzz"),
                     CodeDifferTools.ExportChangeset("zzzz"), CodeDifferTools.ApplyChangeset(_t.P("L"), "zzzz"), CodeDifferTools.WriteMerge("zzzz"),
                     CodeDifferTools.WriteReport("zzzz"), await CodeDifferTools.CancelCompare("zzzz") })
            Assert.StartsWith("unknown compare_id 'zzzz'", answer);
    }

    [Fact]
    public async Task NoCompareYet_SaysWhatToCall()
    {
        ClearSessions();
        var text = await CodeDifferTools.GetSummary();
        Assert.StartsWith("no compare in this session yet", text);
        Assert.Contains("start_compare", text);
        Assert.StartsWith("no compare in this session yet", CodeDifferTools.ListFiles());
    }

    [Fact]
    public async Task GetSummary_IsConstantSize()
    {
        for (int i = 0; i < 700; i++)
        {
            _t.Put($"L/many/f{i:D4}.txt", $"left {i}\n");
            _t.Put($"R/many/f{i:D4}.txt", $"rite {i}\n");
        }
        var id = Id(await CodeDifferTools.StartCompare(_t.P("L"), _t.P("R"), cache: "off", wait_seconds: 60));
        var summary = await CodeDifferTools.GetSummary(id);
        Assert.Matches(@"modified\s+700\s", summary);
        Assert.True(summary.Split('\n').Length < 25, summary);
        Assert.True(summary.Length < 3000, summary);
        Assert.DoesNotContain("f0699", summary);
        Assert.DoesNotContain("f0300", summary);
    }

    [Fact]
    public async Task StartCompare3_ClassifiesEveryOutcome()
    {
        ThreeWay();
        var text = await CodeDifferTools.StartCompare3(_t.P("B"), _t.P("V1"), _t.P("V2"), cache: "off", wait_seconds: 60);
        Assert.StartsWith("compare3 ", text);
        Assert.Contains("finished in", text);
        Assert.Matches(@"v1 only\s+1\s", text);
        Assert.Matches(@"v2 only\s+2\s", text);
        Assert.Matches(@"agreed\s+1\s", text);
        Assert.Matches(@"merged\s+1\s", text);
        Assert.Matches(@"CONFLICT\s+3\s", text);
        Assert.Contains("add/add 1", text);
        Assert.Contains("content 1", text);
        Assert.Contains("delete/modify 1", text);
        Assert.StartsWith($"compare3 {Id(text)} ", await CodeDifferTools.GetSummary());
    }

    // ---------------------------------------------------------------- list_files

    [Fact]
    public async Task ListFiles_Filters_ByStatusReasonAndPath()
    {
        var id = await Compare2();
        var changed = CodeDifferTools.ListFiles(id);
        Assert.StartsWith($"compare {id} · 8 file(s) match · page 1/1", changed);
        Assert.DoesNotContain("same.txt", changed);
        Assert.Contains("R sub/old.txt -> sub/moved.txt  100% similar", changed);

        Assert.Contains("· 9 file(s) match", CodeDifferTools.ListFiles(id, status: "all"));
        var identical = Rows(CodeDifferTools.ListFiles(id, status: "identical"));
        Assert.Equal("same.txt", Assert.Single(identical).Split(' ')[1]);
        var addRemove = Rows(CodeDifferTools.ListFiles(id, status: "added,removed"));
        Assert.Equal(2, addRemove.Count);
        Assert.Contains(addRemove, r => r.StartsWith("A src/fresh.txt"));
        Assert.Contains(addRemove, r => r.StartsWith("D gone.txt"));

        foreach (var reason in new[] { "eol", "whitespace", "binary", "content" })
        {
            var rows = Rows(CodeDifferTools.ListFiles(id, reason: reason));
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Contains($"[{reason}]", r));
        }
        Assert.Contains(Rows(CodeDifferTools.ListFiles(id, reason: "content")), r => r.Contains("src/content.txt"));

        var src = Rows(CodeDifferTools.ListFiles(id, path_glob: "src/**"));
        Assert.Equal(2, src.Count);
        Assert.All(src, r => Assert.Contains("src/", r));
        Assert.Single(Rows(CodeDifferTools.ListFiles(id, path_glob: "*.bin")));
        Assert.Contains("· 0 file(s) match", CodeDifferTools.ListFiles(id, path_glob: "*.nothing"));

        Assert.StartsWith("error:", CodeDifferTools.ListFiles(id, status: "sideways"));
        Assert.StartsWith("error:", CodeDifferTools.ListFiles(id, reason: "vibes"));

        // lines=true reads the page's files and counts their +/- lines.
        var withLines = CodeDifferTools.ListFiles(id, path_glob: "src/content.txt", lines: true);
        Assert.Contains("+1 -1 (1 hunk(s))", withLines);
    }

    [Fact]
    public async Task ListFiles_Pages_AndCapsThePageSize()
    {
        for (int i = 0; i < 620; i++) _t.Put($"R/add/n{i:D4}.txt", "x\n");
        _t.Dir("L");
        var id = Id(await CodeDifferTools.StartCompare(_t.P("L"), _t.P("R"), cache: "off", wait_seconds: 60));

        var p1 = CodeDifferTools.ListFiles(id, page_size: 7);
        Assert.Contains("· 620 file(s) match · page 1/89", p1);
        Assert.Equal(7, Rows(p1).Count);
        Assert.Contains("next page: page=2", p1);
        var p2 = CodeDifferTools.ListFiles(id, page: 2, page_size: 7);
        Assert.Contains("page 2/89", p2);
        Assert.Empty(Rows(p1).Intersect(Rows(p2)));
        var last = CodeDifferTools.ListFiles(id, page: 89, page_size: 7);
        Assert.Equal(620 - 88 * 7, Rows(last).Count);
        Assert.DoesNotContain("next page", last);
        var past = CodeDifferTools.ListFiles(id, page: 1000, page_size: 7); // past the end: empty, and says where the end is
        Assert.Contains("page 1000/89 — past the last page (page=89 is the last)", past);
        Assert.Empty(Rows(past));
        Assert.Contains("page 1/89", CodeDifferTools.ListFiles(id, page: -5, page_size: 7));

        // The page size is capped at 500, however large the ask.
        var big = CodeDifferTools.ListFiles(id, page_size: 100_000);
        Assert.Contains("page 1/2", big);
        Assert.Equal(AgentViews.MaxPageSize, Rows(big).Count);
        Assert.Equal(AgentViews.DefaultPageSize, Rows(CodeDifferTools.ListFiles(id)).Count);
        Assert.Single(Rows(CodeDifferTools.ListFiles(id, page_size: 0)));
    }

    [Fact]
    public async Task ListFiles_ThreeWay_ByOutcome()
    {
        var id = await Compare3();
        var both = CodeDifferTools.ListFiles(id);
        Assert.StartsWith($"compare3 {id} · ", both);
        var conflicts = Rows(CodeDifferTools.ListFiles(id, status: "conflict"));
        Assert.Equal(3, conflicts.Count);
        Assert.All(conflicts, r => Assert.StartsWith("CF ", r));
        Assert.Contains(Rows(CodeDifferTools.ListFiles(id, status: "v2")), r => r.StartsWith("v2 only2.txt"));
        Assert.Single(Rows(CodeDifferTools.ListFiles(id, status: "merged")));
        Assert.Single(Rows(CodeDifferTools.ListFiles(id, status: "agreed")));
        Assert.Single(Rows(CodeDifferTools.ListFiles(id, status: "conflict", path_glob: "add*")));
        Assert.StartsWith("error: reason is 2-way only", CodeDifferTools.ListFiles(id, reason: "eol"));
        Assert.StartsWith("error: lines is 2-way only", CodeDifferTools.ListFiles(id, lines: true));
        Assert.StartsWith("error:", CodeDifferTools.ListFiles(id, status: "added"));
    }

    // ---------------------------------------------------------------- get_file_diff

    [Fact]
    public async Task GetFileDiff_ModifiedAddedRemovedBinaryRenamedIdentical()
    {
        var id = await Compare2();
        var mod = CodeDifferTools.GetFileDiff("src/content.txt", id);
        Assert.StartsWith("M src/content.txt  [content]  +1 -1 in 1 hunk(s)", mod);
        Assert.Contains("diff --git a/src/content.txt b/src/content.txt\n", mod);
        Assert.Contains("-two\n+TWO\n", mod);
        Assert.Contains("@@ -2 +2 @@\n-two\n+TWO\n", CodeDifferTools.GetFileDiff("src/content.txt", id, context: 0));
        Assert.Contains("-two", CodeDifferTools.GetFileDiff("src\\content.txt", id)); // a Windows separator finds it too

        var added = CodeDifferTools.GetFileDiff("src/fresh.txt", id);
        Assert.Contains("new file mode", added);
        Assert.Contains("+fresh\n", added);
        Assert.Contains("deleted file mode", CodeDifferTools.GetFileDiff("gone.txt", id));

        var bin = CodeDifferTools.GetFileDiff("blob.bin", id);
        Assert.Contains("binary", bin, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@@", bin);

        var byOld = CodeDifferTools.GetFileDiff("sub/old.txt", id);
        Assert.Contains("rename from sub/old.txt", byOld);
        Assert.Equal(byOld, CodeDifferTools.GetFileDiff("sub/moved.txt", id));

        Assert.Equal("same.txt: identical\n", CodeDifferTools.GetFileDiff("same.txt", id));
        var missing = CodeDifferTools.GetFileDiff("not/here.txt", id);
        Assert.StartsWith($"not in compare {id}: not/here.txt", missing);
        var near = CodeDifferTools.GetFileDiff("content.txt", id);
        Assert.Contains("did you mean: src/content.txt", near);
    }

    [Fact]
    public async Task GetFileDiff_LargeDiff_IsCapped_WithTheRestInAFile()
    {
        var id = await Compare2();
        var whole = CodeDifferTools.GetFileDiff("big.txt", id, max_lines: 100_000);
        Assert.DoesNotContain("capped:", whole);
        int total = int.Parse(Regex.Match(whole, @"· ([\d,]+) patch line\(s\)").Groups[1].Value.Replace(",", ""));
        Assert.True(total > 1000, whole[..200]);

        var capped = CodeDifferTools.GetFileDiff("big.txt", id, max_lines: 100);
        var m = Regex.Match(capped, @"capped: showing lines 1-100 of ([\d,]+); full patch: (.+)\n");
        Assert.True(m.Success, capped[..Math.Min(600, capped.Length)]);
        var file = m.Groups[2].Value;
        Assert.True(File.Exists(file));
        Assert.StartsWith(ResultsIsolation.Root, file); // the overflow goes into the compare's saved result
        var body = File.ReadAllText(file);
        Assert.StartsWith("diff --git a/big.txt b/big.txt", body);
        Assert.Contains("+edited 3000", body);
        Assert.Contains("hunks (", capped);
        Assert.Contains("next: start_line=101", capped);
        Assert.True(capped.Split('\n').Length < 100 + 60, "a capped answer is about max_lines long");

        var page2 = CodeDifferTools.GetFileDiff("big.txt", id, max_lines: 100, start_line: 101);
        Assert.Contains("capped: showing lines 101-200 of", page2);
        Assert.Contains("next: start_line=201", page2);
        Assert.DoesNotContain("hunks (", page2);
        // The pages are the file's lines.
        var lines = body.Split('\n');
        Assert.Contains(lines[100] + "\n" + lines[101] + "\n", page2);
    }

    [Fact]
    public async Task GetFileDiff_LongLinesAndHugeLines_AreCutByCharacters()
    {
        _t.Put("L/min.js", new string('a', 10_000) + "\n");
        _t.Put("R/min.js", new string('b', 10_000) + "\n");
        var id = Id(await CodeDifferTools.StartCompare(_t.P("L"), _t.P("R"), cache: "off", wait_seconds: 60));
        var text = CodeDifferTools.GetFileDiff("min.js", id);
        Assert.Contains("lines over 2,000 characters cut", text);
        Assert.Contains("[line cut: 10,001 characters; whole line in the file]", text);
        Assert.True(text.Length < 10_000, $"{text.Length} characters");
    }

    [Fact]
    public async Task GetFileDiff_ThreeWayConflict_ShowsDiff3Markers()
    {
        var id = await Compare3();
        var conf = CodeDifferTools.GetFileDiff("conf.txt", id);
        Assert.StartsWith("CF conf.txt", conf);
        Assert.Contains("<<<<<<< v1", conf);
        Assert.Contains("\nV1\n||||||| base", conf);
        Assert.Contains("\nc2\n=======\nV2\n>>>>>>> v2", conf);
        var addadd = CodeDifferTools.GetFileDiff("addadd.txt", id);
        Assert.Contains("<<<<<<< v1", addadd);
        Assert.Contains("aa1", addadd);
        Assert.Contains("aa2", addadd);
        var merged = CodeDifferTools.GetFileDiff("merge.txt", id);
        Assert.DoesNotContain("<<<<<<<", merged);
        Assert.Contains("line 2 v1", merged);
        Assert.Contains("line 18 v2", merged);
        Assert.Contains("o2", CodeDifferTools.GetFileDiff("only2.txt", id));
        Assert.StartsWith($"not touched by either side in compare3 {id}: nothing.txt", CodeDifferTools.GetFileDiff("nothing.txt", id));
        Assert.StartsWith("error: context is 2-way only", CodeDifferTools.GetFileDiff("conf.txt", id, context: 5));

        var capped = CodeDifferTools.GetFileDiff("merge.txt", id, max_lines: 5);
        var m = Regex.Match(capped, @"capped: showing lines 1-5 of [\d,]+; whole file: (.+)\n");
        Assert.True(m.Success, capped);
        Assert.Contains("line 18 v2", File.ReadAllText(m.Groups[1].Value));
    }

    [Fact]
    public async Task GetFileDiff_ACancelledToken_Stops()
    {
        var id = await Compare2();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        // Stopped: an OperationCanceledException (the server turns it into a cancelled request), never a wrong diff.
        string? text = null;
        var ex = Record.Exception(() => text = CodeDifferTools.GetFileDiff("big.txt", id, cancellationToken: stop.Token));
        if (ex is null) Assert.DoesNotContain("+edited 3000", text!);
        else Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    // ---------------------------------------------------------------- get_stats

    [Fact]
    public async Task GetStats_TwoWayAndThreeWay()
    {
        var id = await Compare2();
        var stats = CodeDifferTools.GetStats(id);
        Assert.StartsWith($"compare {id} ", stats);
        Assert.Contains("status      files        left bytes      right bytes", stats);
        Assert.Contains("reasons ", stats);
        Assert.Contains("big.txt", stats);
        Assert.Equal(stats, CodeDifferTools.GetStats());

        var id3 = await Compare3();
        var stats3 = CodeDifferTools.GetStats(id3);
        Assert.StartsWith($"compare3 {id3} ", stats3);
        Assert.Contains("most conflict regions:", stats3);
        Assert.Contains("conf.txt", stats3);
    }

    // ---------------------------------------------------------------- export_changeset

    [Fact]
    public async Task ExportChangeset_WritesAPatchFile_AndReturnsItsPath()
    {
        var id = await Compare2();
        var before = TempDir.Snapshot(_t.P("L"));
        var text = CodeDifferTools.ExportChangeset(id);
        var m = Regex.Match(text, $@"^changeset {id}: (.+?)  \(");
        Assert.True(m.Success, text);
        var file = m.Groups[1].Value;
        Assert.Equal("changeset.patch", Path.GetFileName(file));
        Assert.StartsWith(ResultsIsolation.Root, file);
        var patch = File.ReadAllText(file);
        Assert.Contains("diff --git a/src/content.txt b/src/content.txt", patch);
        Assert.Contains("+edited 3000", patch);
        Assert.DoesNotContain("+edited 3000", text); // the path and counts, never the patch
        Assert.Contains("# Binary files", patch);

        var mine = _t.P("out/my.patch");
        var custom = CodeDifferTools.ExportChangeset(id, out_path: mine, context: 0, literal: true);
        Assert.Contains(mine, custom);
        var literal = File.ReadAllText(mine);
        Assert.Contains("diff --git a/eol.txt b/eol.txt", literal);
        Assert.Contains("@@ -1 +1 @@", literal);
        Assert.Equal(before, TempDir.Snapshot(_t.P("L")));

        Assert.StartsWith("error:", CodeDifferTools.ExportChangeset(id, out_path: _t.P("L/inside.patch")));
        Assert.False(File.Exists(_t.P("L/inside.patch")));
        Assert.StartsWith("error:", CodeDifferTools.ExportChangeset(id, out_path: _t.P("out")));
        Assert.StartsWith("error: context must be 0 or more", CodeDifferTools.ExportChangeset(id, out_path: _t.P("x.patch"), context: -1));

        var id3 = await Compare3();
        Assert.Equal($"error: compare {id3} is 3-way; export_changeset needs a 2-way compare", CodeDifferTools.ExportChangeset(id3));
    }

    // ---------------------------------------------------------------- apply_changeset

    [Fact]
    public async Task ApplyChangeset_DryRunChangesNothing_WriteApplies()
    {
        var f = TempDir.Lines(1, 10, "l");
        _t.Put("L/f.txt", f);
        _t.Put("R/f.txt", f.Replace("l 3\n", "L3 changed\n"));
        _t.Put("T/f.txt", f.Replace("l 9\n", "target 9\n"));
        _t.Put("L/c.txt", "q1\nq2\n");
        _t.Put("R/c.txt", "q1\nR\n");
        _t.Put("T/c.txt", "q1\nT\n");
        var id = Id(await CodeDifferTools.StartCompare(_t.P("L"), _t.P("R"), cache: "off", wait_seconds: 60));
        var target = TempDir.Snapshot(_t.P("T"));

        var dry = CodeDifferTools.ApplyChangeset(_t.P("T"), id);
        Assert.Contains("dry run", dry);
        Assert.Contains("1 would apply", dry);
        Assert.Contains("c.txt", dry);
        Assert.Contains("write=true", dry);
        Assert.Equal(target, TempDir.Snapshot(_t.P("T")));

        var written = CodeDifferTools.ApplyChangeset(_t.P("T"), write: true);
        Assert.Contains("APPLIED", written);
        Assert.Equal(f.Replace("l 3\n", "L3 changed\n").Replace("l 9\n", "target 9\n"), _t.Read("T/f.txt"));
        Assert.Equal("q1\nT\n", _t.Read("T/c.txt")); // conflicted: untouched

        Assert.StartsWith("error:", CodeDifferTools.ApplyChangeset(_t.P("nowhere"), id));
        var id3 = await Compare3();
        Assert.Equal($"error: compare {id3} is 3-way; apply_changeset needs a 2-way compare", CodeDifferTools.ApplyChangeset(_t.P("T"), id3));
    }

    [Fact]
    public async Task ApplyChangeset_ACancelledToken_WritesNothing()
    {
        var f = TempDir.Lines(1, 10, "l");
        _t.Put("L/f.txt", f);
        _t.Put("R/f.txt", f.Replace("l 3\n", "L3\n"));
        _t.Put("T/f.txt", f);
        var id = Id(await CodeDifferTools.StartCompare(_t.P("L"), _t.P("R"), cache: "off", wait_seconds: 60));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        string? text = null;
        var ex = Record.Exception(() => text = CodeDifferTools.ApplyChangeset(_t.P("T"), id, write: true, cancellationToken: stop.Token));
        if (ex is not null) Assert.IsAssignableFrom<OperationCanceledException>(ex);
        else Assert.StartsWith("CANCELLED ", text);
        Assert.Equal(f, _t.Read("T/f.txt"));
    }

    // ---------------------------------------------------------------- write_merge, write_report

    [Fact]
    public async Task WriteMerge_WritesTheOverlay_IntoTheResultByDefault()
    {
        var id = await Compare3();
        var trees = new[] { "B", "V1", "V2" }.ToDictionary(t => t, t => TempDir.Snapshot(_t.P(t)));
        var text = CodeDifferTools.WriteMerge(id);
        var m = Regex.Match(text, @"merge overlay: (.+)\n");
        Assert.True(m.Success, text);
        var dir = m.Groups[1].Value.Trim();
        Assert.StartsWith(ResultsIsolation.Root, dir);
        Assert.Equal("merge", Path.GetFileName(dir));
        Assert.True(File.Exists(Path.Combine(dir, "OVERLAY.txt")));
        Assert.Equal("del2.txt", File.ReadAllText(Path.Combine(dir, "deletes.txt")).Trim());
        Assert.Contains("<<<<<<< v1", File.ReadAllText(Path.Combine(dir, "files", "conf.txt")));
        Assert.Equal("o2\n", File.ReadAllText(Path.Combine(dir, "files", "only2.txt")));
        Assert.Contains("delmod.txt", File.ReadAllText(Path.Combine(dir, "conflicts.txt")));

        // Again: a fresh directory beside it, never over the first.
        var again = CodeDifferTools.WriteMerge();
        Assert.Contains(Path.Combine(Path.GetDirectoryName(dir)!, "merge-2"), again);

        var mine = _t.P("ov");
        Assert.Contains(mine, CodeDifferTools.WriteMerge(id, out_dir: mine));
        Assert.True(File.Exists(Path.Combine(mine, "OVERLAY.txt")));
        Assert.StartsWith("error:", CodeDifferTools.WriteMerge(id, out_dir: mine)); // not empty now
        Assert.StartsWith("error:", CodeDifferTools.WriteMerge(id, out_dir: _t.P("V1/ov")));
        foreach (var (t, snap) in trees) Assert.Equal(snap, TempDir.Snapshot(_t.P(t)));

        var id2 = Id(await CodeDifferTools.StartCompare(_t.P("B"), _t.P("V1"), cache: "off"));
        Assert.StartsWith($"compare {id2} is a 2-way compare; write_merge needs a compare3", CodeDifferTools.WriteMerge(id2));
    }

    [Fact]
    public async Task WriteMerge_ACancelledToken_LeavesAnUnfinishedOverlay()
    {
        var id = await Compare3();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var dir = _t.P("ov");
        var text = CodeDifferTools.WriteMerge(id, out_dir: dir, cancellationToken: stop.Token);
        Assert.StartsWith("stopped:", text);
        Assert.Contains("INCOMPLETE.txt", text);
        Assert.False(File.Exists(Path.Combine(dir, "OVERLAY.txt")) && !File.Exists(Path.Combine(dir, "INCOMPLETE.txt")));
    }

    [Fact]
    public async Task WriteReport_WritesIndexHtml_ForTwoAndThreeWay()
    {
        var id = await Compare2();
        var text = CodeDifferTools.WriteReport(id, include_identical: true, max_diffs: 2);
        var m = Regex.Match(text, @"^report: (.+)\n");
        Assert.True(m.Success, text);
        var index = m.Groups[1].Value;
        Assert.Equal("index.html", Path.GetFileName(index));
        Assert.StartsWith(ResultsIsolation.Root, index);
        Assert.True(File.Exists(index));
        Assert.Contains("past max_diffs", text);
        Assert.Contains("open index.html in a browser", text);

        var id3 = await Compare3();
        var text3 = CodeDifferTools.WriteReport(id3);
        var index3 = Regex.Match(text3, @"^report: (.+)\n").Groups[1].Value;
        Assert.True(File.Exists(index3), text3);
        Assert.NotEqual(index, index3);
    }

    // ---------------------------------------------------------------- list_compares, reopen

    [Fact]
    public async Task ListCompares_ListsSaved_AndAnIdReopensAfterARestart()
    {
        var id = await Compare2();
        var id3 = await Compare3();
        var list = CodeDifferTools.ListCompares();
        Assert.Contains("saved compare(s) in " + ResultsIsolation.Root, list);
        Assert.Contains(id + "  ", list);
        Assert.Contains(id3 + "  ", list);
        Assert.Contains("left=" + _t.P("L"), list);
        Assert.Contains("base=" + _t.P("B"), list);
        Assert.Equal(2, CodeDifferTools.ListCompares(max: 1).Split('\n').Count(l => l.Length > 0)); // the header and one
        Assert.Equal(CodeDifferTools.ListCompares(max: 1), CodeDifferTools.ListCompares(max: -4));

        ClearSessions();
        var reopened = await CodeDifferTools.GetSummary(id);
        Assert.StartsWith($"compare {id} ", reopened);
        Assert.Contains("saved compare from", reopened);
        Assert.Contains("-two\n+TWO\n", CodeDifferTools.GetFileDiff("src/content.txt", id));
        Assert.Contains("· 8 file(s) match", CodeDifferTools.ListFiles(id));
        Assert.Single(Rows(CodeDifferTools.ListFiles(id, reason: "eol")));
        var reopened3 = await CodeDifferTools.GetSummary(id3);
        Assert.StartsWith($"compare3 {id3} ", reopened3);
        Assert.Contains("<<<<<<< v1", CodeDifferTools.GetFileDiff("conf.txt", id3));
        var merge = CodeDifferTools.WriteMerge(id3, out_dir: _t.P("ov"));
        Assert.StartsWith("note: reopened compare", merge);
    }

    // ---------------------------------------------------------------- cancel_compare, a running compare

    [Fact]
    public async Task CancelCompare_UnknownEmptyAndFinished()
    {
        Assert.StartsWith("error: compare_id is required", await CodeDifferTools.CancelCompare(""));
        Assert.StartsWith("error: compare_id is required", await CodeDifferTools.CancelCompare("  "));
        Assert.StartsWith("unknown compare_id 'zzzz'", await CodeDifferTools.CancelCompare("zzzz"));
        var id = await Compare2();
        Assert.Equal($"compare {id} is not running (finished); nothing to cancel", await CodeDifferTools.CancelCompare(id));
        Assert.Contains("finished in", await CodeDifferTools.GetSummary(id));
    }

    [Fact]
    public async Task CancelCompare_StopsARunningCompare_AndTheOtherToolsSayItIsRunning()
    {
        for (int i = 0; i < 40; i++)
        {
            _t.Put($"L/f{i:D3}.txt", $"file {i}\n");
            _t.Put($"R/f{i:D3}.txt", i == 3 ? $"FILE {i}\n" : $"file {i}\n");
        }
        // Held at its 5th pair until the cancel has been asked for: always cancelled mid-run.
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var options = new CompareOptions
        {
            Cache = CacheMode.Off,
            Parallelism = 1,
            AfterPairChecked = done => { if (done == 5) { reached.Set(); release.Wait(30_000); } },
        };
        var s = Store.Start(_t.P("L"), _t.P("R"), options);
        try
        {
            Assert.True(reached.Wait(30_000));
            var running = await CodeDifferTools.GetSummary();
            Assert.StartsWith($"compare {s.Id} ", running);
            Assert.Contains("running ·", running);
            Assert.Contains("still running", CodeDifferTools.WriteReport(s.Id));
            Assert.Contains("RUNNING, partial", CodeDifferTools.ListFiles(s.Id));

            var cancel = CodeDifferTools.CancelCompare(s.Id); // asks at once, then waits for it to stop
            release.Set();
            var text = await cancel;
            Assert.StartsWith($"compare {s.Id}: CANCELLED after ", text);
            Assert.Contains("cache=off", text);
        }
        finally { release.Set(); }

        Assert.True(s.Cancelled);
        Assert.Contains("CANCELLED", await CodeDifferTools.GetSummary(s.Id));
        Assert.Contains("is not running (cancelled)", await CodeDifferTools.CancelCompare(s.Id));
        Assert.Contains("was cancelled", CodeDifferTools.WriteReport(s.Id));
    }
}
