using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;

namespace CodeDiffer.Core.Report;

public sealed class HtmlReportOptions
{
    /// <summary>Most lines of one file's diff carried in the report; past it the whole diff goes to a file the
    /// page links to.</summary>
    public int MaxLines { get; init; } = 3000;

    /// <summary>Most file diffs rendered (in path order); the rest are listed with a note.</summary>
    public int MaxDiffs { get; init; } = 5000;

    /// <summary>Also block-diff files over <see cref="PatchOptions.MaxTextBytes"/> (reads them whole — over SMB
    /// a 1 GB header costs ~30 s a side). Off: they are listed as "large" with how to get the diff.</summary>
    public bool IncludeLarge { get; init; }

    /// <summary>List identical files too (2-way). Off: only their count is shown.</summary>
    public bool IncludeIdentical { get; init; }

    public int Context { get; init; } = PatchOptions.DefaultContextLines;

    /// <summary>Where to write; default &lt;result dir&gt;\report.</summary>
    public string? OutDir { get; init; }
}

/// <param name="Stopped">Diffs not rendered because the report was stopped (Ctrl+C): the page lists those files without one.</param>
public sealed record HtmlReportResult(string IndexPath, int Files, int Rendered, int Capped, int Large, int NotRendered, int Unreadable, int Stopped = 0);

/// <summary>
/// The human report (docs/OUTPUT.md §5): a small static shell (<c>index.html</c>, no external resources) plus
/// <c>data/index.js</c> (the file list) and one <c>data/d/N.js</c> per file diff, loaded only when that file is
/// expanded — script tags, so it works straight from disk with no server. Every path and line reaches the page
/// as JSON data and is inserted as text, never parsed as HTML. Diffs over <see cref="HtmlReportOptions.MaxLines"/>
/// are cut, with the whole diff written alongside and linked.
/// </summary>
public static class HtmlReport
{
    private static readonly JsonSerializerOptions Json = new(); // default encoder: escapes < > & ' and non-ASCII

    /// <summary>Most characters of one file's diff carried in the page (past it, as past MaxLines, the whole diff is
    /// linked): a few thousand minified lines would otherwise make one data file tens of MB.</summary>
    internal const int MaxChunkChars = 1024 * 1024;

    /// <param name="ct">Stops rendering diffs: the files not reached are listed without one, and the report is still
    /// written whole, so a stopped report opens.</param>
    public static HtmlReportResult Write(Session s, HtmlReportOptions? options = null, Action<int, int>? progress = null,
        CancellationToken ct = default)
    {
        var opt = options ?? new HtmlReportOptions();
        if (!s.IsDone || s.Error is not null) throw new InvalidOperationException($"compare {s.Id} has not finished successfully");
        using var held = ResultStore.Hold(s); // no prune takes the result away under the report
        var outDir = Path.GetFullPath(opt.OutDir ?? Path.Combine(AgentViews.OutDir(s), "report"));
        // One writer a directory at a time: two at once each deleted what the other was writing (all but one failed). In
        // this process by a lock, and across processes (the CLI and every MCP server) by a mutex named for the directory.
        HtmlReportResult result;
        // Keyed by the directory as the file system resolves it, not as it was spelled: C:\x, \\?\C:\x, an 8.3 name, a subst
        // or mapped drive and its share are one directory, one lock.
        var key = Walk.RealPath.Of(outDir);
        lock (Gates.GetOrAdd(key, _ => new object()))
        {
            using var across = AcrossProcesses(key);
            try { result = WriteTo(s, opt, outDir, progress, ct); }
            finally { across?.ReleaseMutex(); }
        }
        return result;
    }

    /// <summary>The machine's mutex for one report directory, held: named from the path's hash, case-insensitive as
    /// Windows names are. One a writer that died held is ours. Null where named mutexes aren't to be had.</summary>
    private static Mutex? AcrossProcesses(string dir)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dir.ToUpperInvariant())))[..32];
        Mutex m;
        try { m = new Mutex(false, @"Local\codediffer-report-" + hash); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or PlatformNotSupportedException)
        {
            return null;
        }
        try { m.WaitOne(); }
        catch (AbandonedMutexException) { } // its writer died writing: the directory is ours to write again
        return m;
    }

    /// <summary>A lock per report directory (case-insensitive, as Windows names are), held while a report is written.</summary>
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);

    private static HtmlReportResult WriteTo(Session s, HtmlReportOptions opt, string outDir, Action<int, int>? progress, CancellationToken ct)
    {
        Prepare(outDir, s switch
        {
            CompareSession c => new[] { c.Left, c.Right },
            Compare3Session t => new[] { t.Base, t.V1, t.V2 },
            _ => Array.Empty<string>(),
        });

        var items = s switch
        {
            CompareSession c => Items2(c, opt, ct),
            Compare3Session t => Items3(t, ct),
            _ => throw new ArgumentException("unknown compare kind"),
        };

        var rows = new Row[items.Count];
        int rendered = 0, capped = 0, large = 0, skipped = 0, unreadable = 0, stopped = 0, done = 0;
        int budget = opt.MaxDiffs;
        var renderable = new bool[items.Count];
        for (int i = 0; i < items.Count; i++)
            if (items[i].Wants && budget > 0) { renderable[i] = true; budget--; }

        var notIncluded = $"diff not included (report limit {opt.MaxDiffs:N0} files) — use get_file_diff or `codediffer report --max-diffs`";
        Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = s.Options.Parallelism }, i =>
        {
            var item = items[i];
            var row = item.Row;
            rows[i] = row;
            try
            {
                if (!item.Wants) return;
                if (ct.IsCancellationRequested)
                {
                    row["n"] = Join(row, "diff not rendered: the report was stopped — write it again for the rest");
                    Interlocked.Increment(ref stopped);
                    return;
                }
                if (!renderable[i])
                {
                    row["n"] = Join(row, notIncluded);
                    Interlocked.Increment(ref skipped);
                    return;
                }
                if (item.Large && !opt.IncludeLarge)
                {
                    row["k"] ??= "large";
                    row["n"] = Join(row, "large file: block diff not rendered (reads the whole file) — get_file_diff, or `codediffer report --large`");
                    Interlocked.Increment(ref large);
                    return;
                }
                var (text, mode, warn) = item.Render!();
                int total = LineCount(text);
                if (warn is not null) row["w"] = warn;
                var chunk = new Dictionary<string, object?> { ["mode"] = mode, ["total"] = total };
                if (mode == "merge")
                {
                    // A merged file can be 70k lines with the conflicts anywhere: carry the conflict blocks with
                    // context, not the head of the file. The whole file is written only if blocks don't fit.
                    var c = Condense(text.Split('\n'), total, opt.Context, opt.MaxLines);
                    chunk["segs"] = c.Segments.Select(s => new object[] { s.Start, s.Text }).ToList();
                    chunk["shown"] = c.Kept;
                    chunk["blocks"] = c.Blocks;
                    chunk["shownBlocks"] = c.ShownBlocks;
                    if (c.Cut)
                    {
                        chunk["full"] = $"full/{i}.txt";
                        File.WriteAllText(Path.Combine(outDir, $"full/{i}.txt"), text, new UTF8Encoding(false));
                        Interlocked.Increment(ref capped);
                    }
                }
                else
                {
                    int shown = total;
                    if (!Fits(text, total, opt.MaxLines, MaxChunkChars))
                    {
                        chunk["full"] = $"full/{i}.patch";
                        File.WriteAllText(Path.Combine(outDir, $"full/{i}.patch"), text, new UTF8Encoding(false));
                        // Only the head can be shown: split that, not the whole patch.
                        var lines = HeadLines(text, Math.Min(total, opt.MaxLines));
                        shown = AgentViews.WindowEnd(lines, lines.Length, 1, opt.MaxLines, MaxChunkChars);
                        var cut = new StringBuilder();
                        AgentViews.AppendWindow(cut, lines, 1, shown);
                        text = cut.ToString();
                        Interlocked.Increment(ref capped);
                    }
                    chunk["text"] = text;
                    chunk["shown"] = shown;
                }
                File.WriteAllText(Path.Combine(outDir, "data", "d", $"{i}.js"),
                    $"CD.diff({i},{JsonSerializer.Serialize(chunk, Json)});\n", new UTF8Encoding(false));
                row["d"] = 1;
                Interlocked.Increment(ref rendered);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Stopped mid-file (a giant block diff reads both files whole).
                row["n"] = Join(row, "diff not rendered: the report was stopped — write it again for the rest");
                Interlocked.Increment(ref stopped);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                row["n"] = Join(row, $"unreadable: {ex.Message}");
                Interlocked.Increment(ref unreadable);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One file must not cost the report (an unhandled one would end Parallel.For as an AggregateException).
                row["n"] = Join(row, $"diff could not be rendered: {ex.GetType().Name}: {ex.Message}");
                Interlocked.Increment(ref unreadable);
            }
            finally
            {
                progress?.Invoke(Interlocked.Increment(ref done), items.Count);
            }
        });

        int files = items.Count;
        var meta = Meta(s, opt);
        if (stopped > 0)
            ((List<string>)meta["notes"]!).Add($"This report was stopped before it was finished: {stopped:N0} file(s) have no diff. Write it again for the rest.");
        meta["report"] = new Dictionary<string, object?>
        {
            ["rendered"] = rendered, ["capped"] = capped, ["large"] = large, ["notRendered"] = skipped,
            ["unreadable"] = unreadable, ["stopped"] = stopped, ["maxLines"] = opt.MaxLines, ["maxDiffs"] = opt.MaxDiffs,
        };
        using (var data = new FileStream(Path.Combine(outDir, "data", "index.js"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            // The list as one JSON string the page JSON.parse's: a JS object literal of a million rows costs the
            // browser several times the time and memory to compile.
            data.Write("CD.index("u8);
            data.Write(JsonSerializer.SerializeToUtf8Bytes(meta, Json));
            data.Write(","u8);
            var list = EncodeFilesUtf8(rows);
            rows = null!; // the encoded list is all that is left of them
            items = null!;
            WriteJsonString(data, list.GetBuffer().AsSpan(0, (int)list.Length));
            data.Write(");\n"u8);
        }
        var index = Path.Combine(outDir, "index.html");
        File.WriteAllText(index, Template(), new UTF8Encoding(false));
        return new HtmlReportResult(index, files, rendered, capped, large, skipped, unreadable, stopped);
    }

    // ---- items ----

    /// <summary>The lines of a patch as <c>Split('\n')</c> counts them, less the empty one after a final newline.</summary>
    private static int LineCount(string text)
    {
        int n = text.AsSpan().Count('\n') + 1;
        return text.EndsWith('\n') ? n - 1 : n;
    }

    /// <summary><see cref="AgentViews.Fits"/> over the patch's text in place: no line strings.</summary>
    private static bool Fits(string text, int total, int maxLines, int maxChars)
    {
        if (total > maxLines) return false;
        long chars = 0;
        int pos = 0;
        for (int i = 0; i < total; i++)
        {
            int nl = text.IndexOf('\n', pos);
            int len = (nl < 0 ? text.Length : nl) - pos;
            if (len > AgentViews.MaxLineChars) return false;
            chars += len + 1;
            pos = nl + 1;
        }
        return chars <= maxChars;
    }

    /// <summary>The first <paramref name="n"/> lines, as <c>Split('\n')</c> gives them.</summary>
    private static string[] HeadLines(string text, int n)
    {
        var head = new string[n];
        int pos = 0;
        for (int i = 0; i < n; i++)
        {
            int nl = text.IndexOf('\n', pos);
            head[i] = text[pos..(nl < 0 ? text.Length : nl)];
            pos = nl < 0 ? text.Length : nl + 1;
        }
        return head;
    }

    private sealed record Item(Row Row, bool Wants, bool Large, Func<(string Text, string Mode, string? Warn)>? Render);

    private static List<Item> Items2(CompareSession s, HtmlReportOptions opt, CancellationToken ct)
    {
        var r = s.Report!;
        long maxText = new PatchOptions().MaxTextBytes;
        var list = new List<Item>();
        foreach (var c in r.Changes)
        {
            if (c.Status == ChangeStatus.Identical && !opt.IncludeIdentical) continue;
            var row = new Row
            {
                ["p"] = c.RelativePath,
                ["t"] = c.Status switch { ChangeStatus.Added => "A", ChangeStatus.Removed => "D", ChangeStatus.Modified => "M", ChangeStatus.Renamed => "R", _ => "=" },
                ["ls"] = c.LeftSize,
                ["rs"] = c.RightSize,
            };
            if (c.ReasonLabel is { } rr) row["r"] = rr;
            if (c.Unreadable is { } why) row["n"] = $"could not be read during the compare: {why}";
            if (c.RenamedFrom is { } f) row["f"] = f;
            if (c.SimilarityMilli is { } sim) row["sim"] = sim;
            if (c.EditedRename) row["ed"] = 1; // its bytes differ, even at "100% similar"
            if (c.Status == ChangeStatus.Identical)
            {
                list.Add(new Item(row, false, false, null));
                continue;
            }
            list.Add(new Item(row, true, Math.Max(c.LeftSize, c.RightSize) > maxText, () =>
            {
                var w = new StringWriter { NewLine = "\n" };
                var stats = new PatchStats();
                PatchWriter.WriteChange(w, c, s.Left, s.Right, new PatchOptions { Context = opt.Context, Cancellation = ct }, stats);
                var text = w.ToString();
                var info = AgentViews.Count(text, stats);
                row["k"] = info.Kind;
                if (info.Kind == "text") { row["a"] = info.AddedLines; row["x"] = info.RemovedLines; row["h"] = info.Hunks; }
                return (text, "patch", AgentViews.Stale(s, c));
            }));
        }
        return list;
    }

    private static List<Item> Items3(Compare3Session s, CancellationToken ct)
    {
        long maxText = new PatchOptions().MaxTextBytes;
        var list = new List<Item>();
        foreach (var e in s.Report!.Entries)
        {
            var row = new Row
            {
                ["p"] = e.MergedPath ?? e.Path,
                ["t"] = e.Outcome switch
                {
                    Merge3Outcome.V1Only => "v1", Merge3Outcome.V2Only => "v2", Merge3Outcome.Agreed => "AG",
                    Merge3Outcome.Merged => "MG", _ => "CF",
                },
                ["s1"] = Side(e.V1),
                ["s2"] = Side(e.V2),
            };
            if (e.MergedPath is { } m && m != e.Path) row["f"] = e.Path;
            if (e.ConflictKind is { } k) row["ck"] = k;
            if (e.ConflictRegions > 0 || e.Outcome == Merge3Outcome.Merged) { row["c"] = e.ConflictRegions; row["cl"] = e.CleanRegions; }
            if (e.Note is { } n) row["n"] = n;
            long size = Math.Max(Size(e.V1), Size(e.V2));
            list.Add(new Item(row, true, size > maxText || e.ConflictKind == "large", () =>
            {
                // A clean merge reads best as what it does to the base; a conflicted one as the merged file
                // with its marker blocks; anything else as the side patch(es).
                if (e.Outcome == Merge3Outcome.Merged && TreeMerger.MergeDiff(e, s.Base, s.V1, s.V2, ct: ct) is { } diff)
                {
                    row["k"] = "base → clean merge result";
                    return (diff, "patch", ThreeWayViews.Stale(s, e));
                }
                var text = ThreeWayViews.Body(s, e, out var kind, out var merge, new PatchOptions { Cancellation = ct });
                row["k"] = kind;
                return (text, merge ? "merge" : "patch", ThreeWayViews.Stale(s, e));
            }));
        }
        return list;
    }

    /// <param name="Cut">Something was left out (blocks, the head of a too-big block, long lines' tails): link the whole file.</param>
    internal sealed record Condensed(List<(int Start, string Text)> Segments, int Kept, int Blocks, int ShownBlocks, bool Cut);

    /// <summary>
    /// The conflict blocks of a merged file (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; v1</c> … <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; v2</c>) with
    /// <paramref name="context"/> lines around each, overlapping ranges joined, in file order until
    /// <paramref name="maxLines"/> lines or <paramref name="maxChars"/> are kept. Blocks are kept whole, except a first
    /// block that alone is over the cap: its head is shown. Segment starts are 1-based line numbers.
    /// </summary>
    internal static Condensed Condense(string[] lines, int total, int context, int maxLines, int maxChars = MaxChunkChars)
    {
        var blocks = new List<(int From, int To)>();
        for (int i = 0; i < total; i++)
        {
            if (!lines[i].StartsWith("<<<<<<< v1", StringComparison.Ordinal)) continue;
            int end = i;
            while (end < total - 1 && !lines[end].StartsWith(">>>>>>> v2", StringComparison.Ordinal)) end++;
            blocks.Add((i, end));
            i = end;
        }
        if (blocks.Count == 0) // no markers: the file itself, capped (and the whole file linked when it is)
        {
            int end = AgentViews.WindowEnd(lines, total, 1, maxLines, maxChars);
            return new([(1, Window(lines, 1, end))], end, 0, 0, !AgentViews.Fits(lines, total, maxLines, maxChars));
        }

        var ranges = new List<(int From, int To)>();
        int kept = 0, shown = 0;
        long chars = 0;
        bool cut = false;
        foreach (var (from, to) in blocks)
        {
            int a = Math.Max(0, from - context), b = Math.Min(total - 1, to + context);
            bool joins = ranges.Count > 0 && a <= ranges[^1].To + 1;
            int first = joins ? ranges[^1].To + 1 : a;
            int add = b - first + 1;
            long addChars = 0;
            for (int k = first; k <= b; k++) addChars += Math.Min(lines[k].Length, AgentViews.MaxLineChars) + 1;
            if (shown > 0 && (kept + add > maxLines || chars + addChars > maxChars)) break;
            if (shown == 0 && (add > maxLines || addChars > maxChars))
            {
                // One block alone is over the cap: show its head (the whole file is linked), never all of it.
                int end = AgentViews.WindowEnd(lines, total, a + 1, maxLines, maxChars); // 1-based, inclusive
                ranges.Add((a, end - 1));
                kept = end - a;
                shown = 1;
                cut = true;
                break;
            }
            if (joins) ranges[^1] = (ranges[^1].From, b);
            else ranges.Add((a, b));
            kept += add;
            chars += addChars;
            shown++;
        }
        var segs = ranges.Select(r => (r.From + 1, Window(lines, r.From + 1, r.To + 1))).ToList();
        cut |= shown < blocks.Count || ranges.Any(r => Enumerable.Range(r.From, r.To - r.From + 1).Any(k => lines[k].Length > AgentViews.MaxLineChars));
        return new(segs, kept, blocks.Count, shown, cut);
    }

    private static string Window(string[] lines, int startLine, int end)
    {
        var o = new StringBuilder();
        AgentViews.AppendWindow(o, lines, startLine, end); // lines over the per-line cap are cut, as in get_file_diff
        return o.ToString();
    }

    private static long Size(FileChange? c) => c is null ? 0 : Math.Max(c.LeftSize, c.RightSize);

    private static string Side(FileChange? c) => c is null ? "unchanged" : c.Status switch
    {
        ChangeStatus.Added => "added",
        ChangeStatus.Removed => "deleted",
        ChangeStatus.Renamed => $"renamed to {c.RelativePath}",
        _ => c.ReasonLabel ?? "modified",
    };

    private static string Join(Row row, string note) => row["n"] is string s && s.Length > 0 ? s + " · " + note : note;

    // ---- the file list ----

    /// <summary>
    /// The rows (one dictionary per listed file, keys as the page reads them) as compact JSON: each path as a folder
    /// number and a file name over one table of folders (parents before children), every other key as a column, and
    /// text that repeats (statuses, reasons, the same note on a million rows) stored once. A key on most rows is a
    /// dense column (null where a row lacks it); a rare one is sparse, <c>[row, value, row, value, …]</c>.
    /// <code>{"v":1,"n":rows,"fd":[folder…],"fn":[name…],"c":{key:[value…]},"s":{key:[row,value…]},
    ///  "dp":[parent folder…],"dn":[folder name…],"S":[text…],"str":[keys whose values index S]}</code>
    /// </summary>
    internal static string EncodeFiles(IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var typed = rows.Select(d =>
        {
            var r = new Row();
            foreach (var (k, v) in d) r[k] = v;
            return r;
        }).ToArray();
        var b = EncodeFilesUtf8(typed);
        return Encoding.UTF8.GetString(b.GetBuffer(), 0, (int)b.Length);
    }

    private static MemoryStream EncodeFilesUtf8(IReadOnlyList<Row> rows)
    {
        var dirs = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = 0 };
        var parent = new List<int> { -1 };
        var dirName = new List<string> { "" };
        int Dir(string path)
        {
            if (dirs.TryGetValue(path, out var i)) return i;
            int cut = path.LastIndexOf('/');
            int up = Dir(cut < 0 ? "" : path[..cut]);
            i = parent.Count;
            parent.Add(up);
            dirName.Add(cut < 0 ? path : path[(cut + 1)..]);
            dirs[path] = i;
            return i;
        }

        var text = new Dictionary<string, int>(StringComparer.Ordinal);
        var texts = new List<string>();
        int Text(string t)
        {
            if (!text.TryGetValue(t, out var i)) { i = texts.Count; texts.Add(t); text[t] = i; }
            return i;
        }

        var folder = new int[rows.Count];
        var name = new string[rows.Count];
        var columns = new Dictionary<string, List<(int Row, object Value)>>(StringComparer.Ordinal);
        var stringKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int r = 0; r < rows.Count; r++)
        {
            foreach (var (key, value) in rows[r])
            {
                if (key == "p")
                {
                    var p = (string)value!;
                    int cut = p.LastIndexOf('/');
                    folder[r] = Dir(cut < 0 ? "" : p[..cut]);
                    name[r] = cut < 0 ? p : p[(cut + 1)..];
                    continue;
                }
                if (value is null) continue;
                if (!columns.TryGetValue(key, out var col)) columns[key] = col = new();
                if (value is string) stringKeys.Add(key);
                col.Add((r, value));
            }
        }

        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            void Value(string key, object v)
            {
                switch (v)
                {
                    case string sv: w.WriteNumberValue(Text(sv)); break; // a text key holds only texts
                    case int iv: w.WriteNumberValue(iv); break;
                    case long lv: w.WriteNumberValue(lv); break;
                    case bool bv: w.WriteNumberValue(bv ? 1 : 0); break;
                    default: JsonSerializer.Serialize(w, v); break;
                }
            }
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteNumber("n", rows.Count);
            w.WriteStartArray("fd"); foreach (var f in folder) w.WriteNumberValue(f); w.WriteEndArray();
            w.WriteStartArray("fn"); foreach (var n in name) w.WriteStringValue(n ?? ""); w.WriteEndArray();
            var sparse = new List<string>();
            w.WriteStartObject("c");
            foreach (var (key, col) in columns)
            {
                if (col.Count * 4 < rows.Count) { sparse.Add(key); continue; }
                w.WriteStartArray(key);
                int next = 0;
                foreach (var (row, v) in col)
                {
                    for (; next < row; next++) w.WriteNullValue();
                    Value(key, v);
                    next = row + 1;
                }
                for (; next < rows.Count; next++) w.WriteNullValue();
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteStartObject("s");
            foreach (var key in sparse)
            {
                w.WriteStartArray(key);
                foreach (var (row, v) in columns[key]) { w.WriteNumberValue(row); Value(key, v); }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            // Last: the folders and texts are all known only once every row is written.
            w.WriteStartArray("dp"); foreach (var up in parent) w.WriteNumberValue(up); w.WriteEndArray();
            w.WriteStartArray("dn"); foreach (var d in dirName) w.WriteStringValue(d); w.WriteEndArray();
            w.WriteStartArray("S"); foreach (var t in texts) w.WriteStringValue(t); w.WriteEndArray();
            w.WriteStartArray("str"); foreach (var k in stringKeys) w.WriteStringValue(k); w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer;
    }

    /// <summary>
    /// <paramref name="utf8"/> as a JSON string, escaped as <see cref="JsonSerializer"/> escapes a string with the
    /// default encoder, a piece at a time: the whole list escaped at once (then as a .NET string) cost several
    /// copies of it. Pieces are cut before an ASCII byte, never inside a character, and escaping is per character,
    /// so the pieces join to what the whole would give.
    /// </summary>
    internal static void WriteJsonString(Stream to, ReadOnlySpan<byte> utf8, int piece = 1 << 20)
    {
        var buf = new ArrayBufferWriter<byte>(piece + 16);
        using var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Encoder = Json.Encoder });
        to.Write("\""u8);
        for (int at = 0; at < utf8.Length;)
        {
            int end = Math.Min(utf8.Length, at + piece);
            while (end < utf8.Length && utf8[end] >= 0x80) end++;
            buf.ResetWrittenCount();
            w.Reset();
            w.WriteStringValue(utf8[at..end]);
            w.Flush();
            to.Write(buf.WrittenSpan[1..^1]);
            at = end;
        }
        to.Write("\""u8);
    }

    /// <summary>A report's <c>data/index.js</c> read back: its meta, and its rows as the page sees them (for tests;
    /// the page has its own reader of the same format).</summary>
    internal static (JsonElement Meta, List<JsonElement> Files) ReadIndex(string indexJs)
    {
        var all = File.ReadAllText(indexJs);
        const string head = "CD.index(";
        if (!all.StartsWith(head, StringComparison.Ordinal)) throw new FormatException("not a CodeDiffer report index");
        using var doc = JsonDocument.Parse("[" + all[head.Length..all.LastIndexOf(')')] + "]");
        var meta = doc.RootElement[0].Clone();
        using var list = JsonDocument.Parse(doc.RootElement[1].GetString()!);
        var l = list.RootElement;
        int n = l.GetProperty("n").GetInt32();
        var dp = l.GetProperty("dp").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var dn = l.GetProperty("dn").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var dirPath = new string[dp.Length];
        for (int d = 0; d < dp.Length; d++) // parents come before their children
            dirPath[d] = d == 0 ? "" : dp[d] == 0 ? dn[d] : dirPath[dp[d]] + "/" + dn[d];
        var texts = l.GetProperty("S").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var stringKeys = l.GetProperty("str").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        var fd = l.GetProperty("fd");
        var fn = l.GetProperty("fn");
        var rows = new List<Dictionary<string, object?>>(n);
        for (int r = 0; r < n; r++)
        {
            int d = fd[r].GetInt32();
            rows.Add(new Dictionary<string, object?> { ["p"] = d == 0 ? fn[r].GetString() : dirPath[d] + "/" + fn[r].GetString() });
        }
        object? Decode(string key, JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when stringKeys.Contains(key) => texts[v.GetInt32()],
            JsonValueKind.Number => v.GetInt64(),
            _ => v.Clone(),
        };
        foreach (var col in l.GetProperty("c").EnumerateObject())
        {
            int r = 0;
            foreach (var v in col.Value.EnumerateArray())
            {
                if (Decode(col.Name, v) is { } x) rows[r][col.Name] = x;
                r++;
            }
        }
        foreach (var col in l.GetProperty("s").EnumerateObject())
        {
            var a = col.Value;
            for (int k = 0; k + 1 < a.GetArrayLength(); k += 2)
                if (Decode(col.Name, a[k + 1]) is { } x) rows[a[k].GetInt32()][col.Name] = x;
        }
        return (meta, rows.Select(r => JsonSerializer.SerializeToElement(r)).ToList());
    }

    /// <summary>One listed file's fields, keys as the page reads them, in the order they were first set (which
    /// orders the list's columns, as a dictionary's insertion order did). A missing key reads as null. A dictionary
    /// a row cost about twice this for the handful of keys a row has.</summary>
    private sealed class Row : IEnumerable<KeyValuePair<string, object?>>
    {
        private object?[] _kv = new object?[24]; // key, value, key, value, … (a 2-way row has up to ~12 keys)
        private int _n;
        private static readonly object[] SmallInts = Enumerable.Range(0, 1024).Select(i => (object)i).ToArray();

        public object? this[string key]
        {
            get
            {
                for (int i = 0; i < _n; i += 2)
                    if ((string)_kv[i]! == key) return _kv[i + 1];
                return null;
            }
            set
            {
                if (value is int v && (uint)v < (uint)SmallInts.Length) value = SmallInts[v]; // counts, flags: one box each
                for (int i = 0; i < _n; i += 2)
                    if ((string)_kv[i]! == key) { _kv[i + 1] = value; return; }
                if (_n == _kv.Length) Array.Resize(ref _kv, _n * 2);
                _kv[_n++] = key;
                _kv[_n++] = value;
            }
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (int i = 0; i < _n; i += 2) yield return new((string)_kv[i]!, _kv[i + 1]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ---- meta ----

    private static Dictionary<string, object?> Meta(Session s, HtmlReportOptions opt)
    {
        var m = new Dictionary<string, object?>
        {
            ["id"] = s.Id,
            ["started"] = s.StartedUtc.ToString("O"),
            ["elapsedSeconds"] = Math.Round(s.Elapsed.TotalSeconds, 1),
            ["tool"] = ResultStore.Version,
            ["resultDir"] = s.ResultDir,
        };
        var notes = new List<string>();
        switch (s)
        {
            case CompareSession c:
            {
                var r = c.Report!;
                m["kind"] = "compare";
                m["roots"] = new Dictionary<string, string> { ["left"] = c.Left, ["right"] = c.Right };
                m["counts"] = Enum.GetValues<ChangeStatus>().ToDictionary(ResultStore.Token, st => r.Count(st));
                m["reasons"] = Enum.GetValues<ChangeReason>().Select(x => (k: CanonicalTokens.Token(x), n: r.ReasonCount(x)))
                    .Where(t => t.n > 0).ToDictionary(t => t.k, t => t.n);
                m["identicalListed"] = opt.IncludeIdentical;
                if (r.LeftDroppedDirectories + r.RightDroppedDirectories > 0)
                    notes.Add($"Incomplete walk: {r.LeftDroppedDirectories} left / {r.RightDroppedDirectories} right director(ies) could not be listed{AgentViews.Unlisted(r)} — what the other tree has under them is unknown, listed as [unreadable].");
                notes.AddRange(AgentViews.Notes(r));
                if (r.UnreadableFiles > 0) notes.Add($"{r.UnreadableFiles} file(s) could not be read (locked, vanished or denied) — their verdict is unknown, shown as 'unreadable'.");
                break;
            }
            case Compare3Session t:
            {
                var r = t.Report!;
                m["kind"] = "compare3";
                m["roots"] = new Dictionary<string, string> { ["base"] = t.Base, ["v1"] = t.V1, ["v2"] = t.V2 };
                m["counts"] = Enum.GetValues<Merge3Outcome>().ToDictionary(ResultStore.Token, o => r.Count(o));
                m["conflictRegions"] = r.Entries.Sum(e => e.ConflictRegions);
                m["baseFiles"] = r.V1Report.Total - r.V1Report.Count(ChangeStatus.Added);
                if (r.DroppedDirectories > 0)
                    notes.Add($"Incomplete walk: {r.DroppedDirectories} director(ies) could not be listed — the verdicts under them may be wrong.");
                if (r.UnreadableFiles > 0)
                    notes.Add($"{r.UnreadableFiles} file(s) could not be read (locked, vanished or denied) — their verdict is unknown, shown as 'unreadable'.");
                notes.AddRange(AgentViews.Notes(r.V1Report, "base->v1"));
                notes.AddRange(AgentViews.Notes(r.V2Report, "base->v2"));
                break;
            }
        }
        if (s.Reopened)
            notes.Add($"Saved compare from {s.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}: the verdicts are as of then; diffs were rendered from the trees when this report was written.");
        m["notes"] = notes;
        return m;
    }

    // ---- files ----

    /// <summary>Written first into every report directory: a directory holding it is ours to replace, even one a
    /// stopped writer left half done.</summary>
    internal const string Marker = "CODEDIFFER-REPORT.txt";

    /// <summary>A fresh report directory. An old report is ours and derived, so it is replaced — but only a
    /// directory that is one of our reports (or is empty) is ever deleted, and never one inside a compared tree or
    /// holding one.</summary>
    private static void Prepare(string outDir, IEnumerable<string> trees)
    {
        if (OutDirProblem(outDir, trees) is { } problem) throw new IOException(problem);
        if (Directory.Exists(outDir))
        {
            // Marked first (a report from before the marker too), then everything but the marker deleted (read-only files
            // too, a link removed, never followed): a delete that fails part way leaves a directory still marked ours,
            // replaced next time, never one refused forever.
            if (new DirectoryInfo(outDir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(outDir); // the link itself, never what it points at
            else
            {
                WriteMarker(outDir);
                ResultStore.DeleteContents(new DirectoryInfo(outDir), keep: Marker);
            }
        }
        Directory.CreateDirectory(outDir);
        WriteMarker(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "data", "d"));
        Directory.CreateDirectory(Path.Combine(outDir, "full"));
    }

    private static void WriteMarker(string outDir)
    {
        var marker = new FileInfo(Path.Combine(outDir, Marker));
        if (marker.Exists && marker.Attributes.HasFlag(FileAttributes.ReadOnly)) marker.Attributes &= ~FileAttributes.ReadOnly;
        File.WriteAllText(marker.FullName, "A CodeDiffer HTML report (open index.html). Writing a new report here replaces this directory.\n",
            new UTF8Encoding(false));
    }

    /// <summary>Why a report can't be written to <paramref name="outDir"/> (it overlaps a compared tree, is a file, or
    /// is a directory that isn't one of our reports), or null — checked before a compare that may take an hour as well
    /// as when the report is written.</summary>
    public static string? OutDirProblem(string outDir, IEnumerable<string> trees)
    {
        if (trees.FirstOrDefault(t => ResultStore.Overlaps(outDir, t)) is { } tree)
            return $"the report directory {outDir} and the compared tree {tree} overlap; write the report somewhere else";
        if (ResultStore.HoldsResults(outDir) is { } holds) return $"the report directory {holds}";
        if (File.Exists(outDir))
            return $"{outDir} is a file, not a directory; write the report somewhere else";
        if (Directory.Exists(outDir) && !IsOurs(outDir))
            return $"{outDir} exists and is not a CodeDiffer report — refusing to overwrite it";
        return ResultStore.CannotMake(outDir, "report directory");
    }

    /// <summary>Empty, marked as ours, or a report from before the marker: nothing but index.html, data\ and full\,
    /// with data\index.js as this writes it.</summary>
    private static bool IsOurs(string dir)
    {
        var entries = Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).ToList();
        if (entries.Count == 0 || entries.Contains(Marker, StringComparer.OrdinalIgnoreCase)) return true;
        if (entries.Any(e => !(e is "index.html" or "data" or "full"))) return false;
        var index = Path.Combine(dir, "data", "index.js");
        if (!File.Exists(index)) return false;
        using var reader = new StreamReader(index, Encoding.UTF8);
        var head = new char[9];
        return reader.ReadBlock(head) == head.Length && new string(head) == "CD.index(";
    }

    private static string Template()
    {
        using var stream = typeof(HtmlReport).Assembly.GetManifestResourceStream("CodeDiffer.Core.Report.report.html")
            ?? throw new InvalidOperationException("report template missing from the build");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
