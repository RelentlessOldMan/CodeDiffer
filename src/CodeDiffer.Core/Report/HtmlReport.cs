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
        var outDir = Path.GetFullPath(opt.OutDir ?? Path.Combine(AgentViews.OutDir(s), "report"));
        Prepare(outDir, s switch
        {
            CompareSession c => new[] { c.Left, c.Right },
            Compare3Session t => new[] { t.Base, t.V1, t.V2 },
            _ => Array.Empty<string>(),
        });

        var items = s switch
        {
            CompareSession c => Items2(c, opt),
            Compare3Session t => Items3(t),
            _ => throw new ArgumentException("unknown compare kind"),
        };

        var rows = new Dictionary<string, object?>[items.Count];
        int rendered = 0, capped = 0, large = 0, skipped = 0, unreadable = 0, stopped = 0, done = 0;
        int budget = opt.MaxDiffs;
        var renderable = new bool[items.Count];
        for (int i = 0; i < items.Count; i++)
            if (items[i].Wants && budget > 0) { renderable[i] = true; budget--; }

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
                    row["n"] = Join(row, $"diff not included (report limit {opt.MaxDiffs:N0} files) — use get_file_diff or `codediffer report --max-diffs`");
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
                var lines = text.Split('\n');
                int total = lines.Length - (text.EndsWith('\n') ? 1 : 0);
                if (warn is not null) row["w"] = warn;
                var chunk = new Dictionary<string, object?> { ["mode"] = mode, ["total"] = total };
                if (mode == "merge")
                {
                    // A merged file can be 70k lines with the conflicts anywhere: carry the conflict blocks with
                    // context, not the head of the file. The whole file is written only if blocks don't fit.
                    var c = Condense(lines, total, opt.Context, opt.MaxLines);
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
                    if (!AgentViews.Fits(lines, total, opt.MaxLines, MaxChunkChars))
                    {
                        chunk["full"] = $"full/{i}.patch";
                        File.WriteAllText(Path.Combine(outDir, $"full/{i}.patch"), text, new UTF8Encoding(false));
                        shown = AgentViews.WindowEnd(lines, total, 1, opt.MaxLines, MaxChunkChars);
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

        var meta = Meta(s, opt);
        if (stopped > 0)
            ((List<string>)meta["notes"]!).Add($"This report was stopped before it was finished: {stopped:N0} file(s) have no diff. Write it again for the rest.");
        meta["files"] = rows;
        meta["report"] = new Dictionary<string, object?>
        {
            ["rendered"] = rendered, ["capped"] = capped, ["large"] = large, ["notRendered"] = skipped,
            ["unreadable"] = unreadable, ["stopped"] = stopped, ["maxLines"] = opt.MaxLines, ["maxDiffs"] = opt.MaxDiffs,
        };
        File.WriteAllText(Path.Combine(outDir, "data", "index.js"), $"CD.index({JsonSerializer.Serialize(meta, Json)});\n", new UTF8Encoding(false));
        var index = Path.Combine(outDir, "index.html");
        File.WriteAllText(index, Template(), new UTF8Encoding(false));
        return new HtmlReportResult(index, items.Count, rendered, capped, large, skipped, unreadable, stopped);
    }

    // ---- items ----

    private sealed record Item(Dictionary<string, object?> Row, bool Wants, bool Large, Func<(string Text, string Mode, string? Warn)>? Render);

    private static List<Item> Items2(CompareSession s, HtmlReportOptions opt)
    {
        var r = s.Report!;
        long maxText = new PatchOptions().MaxTextBytes;
        var list = new List<Item>();
        foreach (var c in r.Changes)
        {
            if (c.Status == ChangeStatus.Identical && !opt.IncludeIdentical) continue;
            var row = new Dictionary<string, object?>
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
                PatchWriter.WriteChange(w, c, s.Left, s.Right, new PatchOptions { Context = opt.Context }, stats);
                var text = w.ToString();
                var info = AgentViews.Count(text, stats);
                row["k"] = info.Kind;
                if (info.Kind == "text") { row["a"] = info.AddedLines; row["x"] = info.RemovedLines; row["h"] = info.Hunks; }
                return (text, "patch", AgentViews.Stale(s, c));
            }));
        }
        return list;
    }

    private static List<Item> Items3(Compare3Session s)
    {
        long maxText = new PatchOptions().MaxTextBytes;
        var list = new List<Item>();
        foreach (var e in s.Report!.Entries)
        {
            var row = new Dictionary<string, object?>
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
                if (e.Outcome == Merge3Outcome.Merged && TreeMerger.MergeDiff(e, s.Base, s.V1, s.V2) is { } diff)
                {
                    row["k"] = "base → clean merge result";
                    return (diff, "patch", ThreeWayViews.Stale(s, e));
                }
                var text = ThreeWayViews.Body(s, e, out var kind, out var merge);
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

    private static string Join(Dictionary<string, object?> row, string note)
        => row.TryGetValue("n", out var old) && old is string s && s.Length > 0 ? s + " · " + note : note;

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
                    notes.Add($"Incomplete walk: {r.LeftDroppedDirectories} left / {r.RightDroppedDirectories} right director(ies) could not be listed — adds/removes under them may be listing failures.");
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
                m["baseFiles"] = r.V1Report.Changes.Count(c => c.Status != ChangeStatus.Added);
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
        foreach (var tree in trees)
            if (ResultStore.IsUnder(outDir, tree) || ResultStore.IsUnder(tree, outDir))
                throw new IOException($"the report directory {outDir} and the compared tree {tree} overlap; write the report somewhere else");
        if (Directory.Exists(outDir))
        {
            if (!IsOurs(outDir)) throw new IOException($"{outDir} exists and is not a CodeDiffer report — refusing to overwrite it");
            Directory.Delete(outDir, recursive: true);
        }
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, Marker), "A CodeDiffer HTML report (open index.html). Writing a new report here replaces this directory.\n",
            new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(outDir, "data", "d"));
        Directory.CreateDirectory(Path.Combine(outDir, "full"));
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
