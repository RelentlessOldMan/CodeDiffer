using System.ComponentModel;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Report;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using ModelContextProtocol.Server;

namespace CodeDiffer.Mcp;

/// <summary>
/// The tools exposed to the agent. A compare (2-way or 3-way) runs in the background and is queried by id;
/// every answer is bounded (constant-size summary, paged lists, capped per-file views with a file for the
/// rest), so a 90 GB tree never floods the context. Deliberately a small surface to keep the standing
/// token cost low. compare_id may be omitted everywhere: it then means the most recent compare. Finished
/// compares are saved (ResultStore), so an id from an earlier session reopens without re-comparing.
/// </summary>
[McpServerToolType]
public static class CodeDifferTools
{
    internal static readonly SessionStore Sessions = new();

    [McpServerTool(Name = "start_compare")]
    [Description("Compare two directory trees (local or UNC/SMB). Runs in the background and returns a compare_id; " +
                 "waits up to wait_seconds and returns the summary if it finishes by then. Unchanged files are " +
                 "answered from a hash cache when name+size+timestamps match, so repeat compares are fast.")]
    public static async Task<string> StartCompare(
        [Description("Left (old/base) directory.")] string left,
        [Description("Right (new) directory.")] string right,
        [Description("Hash cache: on (default), off (byte-compare everything), rehash (re-read and refresh it).")] string cache = "on",
        [Description("Concurrent file reads (default min(cores, 8)).")] int threads = 0,
        [Description("Seconds to wait for completion before returning (default 30; 0 = return at once).")] int wait_seconds = 30)
    {
        if (Options(cache, threads, out var options) is { } bad) return bad;
        CompareSession s;
        try { s = Sessions.Start(left, right, options); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return $"error: {ex.Message}";
        }
        await WaitAsync(s, wait_seconds);
        return Started(s, AgentViews.Summary(s));
    }

    [McpServerTool(Name = "start_compare3")]
    [Description("3-way compare: two variants (v1, v2) of a common base. Classifies every path either side touched: " +
                 "v1 only, v2 only, agreed (identical change), merged (both changed, merges cleanly) or conflict " +
                 "(content, modify/delete, add/add, rename/rename...). Background, returns a compare_id like start_compare.")]
    public static async Task<string> StartCompare3(
        [Description("The common base directory.")] string @base,
        [Description("First variant directory.")] string v1,
        [Description("Second variant directory.")] string v2,
        [Description("Hash cache: on (default), off, rehash.")] string cache = "on",
        [Description("Concurrent file reads (default min(cores, 8)).")] int threads = 0,
        [Description("Seconds to wait for completion before returning (default 30; 0 = return at once).")] int wait_seconds = 30)
    {
        if (Options(cache, threads, out var options) is { } bad) return bad;
        Compare3Session s;
        try { s = Sessions.Start3(@base, v1, v2, options); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return $"error: {ex.Message}";
        }
        await WaitAsync(s, wait_seconds);
        return Started(s, ThreeWayViews.Summary(s));
    }

    [McpServerTool(Name = "get_summary")]
    [Description("Constant-size summary of a compare (2-way: counts by status/reason, changed bytes; 3-way: counts by " +
                 "merge outcome and conflict kind). While it is still running: progress, time left, and the differences found so " +
                 "far (list_files / get_file_diff already work on those); wait_seconds waits for it.")]
    public static async Task<string> GetSummary(
        [Description("Compare id (default: the most recent).")] string? compare_id = null,
        [Description("Seconds to wait for a running compare to finish (default 0).")] int wait_seconds = 0)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        await WaitAsync(s!, wait_seconds);
        return s switch
        {
            Compare3Session t => ThreeWayViews.Summary(t),
            CompareSession c => AgentViews.Summary(c),
            _ => "error: unknown compare kind",
        };
    }

    [McpServerTool(Name = "list_files")]
    [Description("Paged, filtered list of files in a compare (no diffs). 2-way: tag (A/D/M/R), path, reason, sizes; " +
                 "identical hidden unless asked for; lines=true adds +/- line counts for the page. 3-way: outcome tag " +
                 "(v1/v2/AG/MG/CF), what each side did, conflict kind and region counts.")]
    public static string ListFiles(
        [Description("Compare id (default: the most recent).")] string? compare_id = null,
        [Description("2-way: changed (default), all, or a comma list of added, removed, modified, renamed, identical. " +
                     "3-way: changed (default), both, or a comma list of conflict, merged, agreed, v1, v2.")] string? status = null,
        [Description("Glob over relative paths, e.g. src/**/*.c; a pattern without '/' matches file names anywhere (*.h).")] string? path_glob = null,
        [Description("2-way only: modified files with this reason: content, eol, whitespace, encoding, binary.")] string? reason = null,
        [Description("1-based page number.")] int page = 1,
        [Description("Files per page (default 50, max 500).")] int page_size = AgentViews.DefaultPageSize,
        [Description("2-way only: also count hunks and +/- lines for this page (reads those files).")] bool lines = false)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        return Safe(() => s switch
        {
            Compare3Session t => ThreeWayViews.ListFiles(t, status, path_glob, page, page_size),
            CompareSession c => AgentViews.ListFiles(c, status, path_glob, reason, page, page_size, lines),
            _ => "error: unknown compare kind",
        });
    }

    [McpServerTool(Name = "get_file_diff")]
    [Description("One file. 2-way: its unified diff (git form, `git apply`-able). 3-way: the merged file with diff3 " +
                 "conflict markers (<<<<<<< v1 / ||||||| base / ======= / >>>>>>> v2), or the one side's patch. Hard-capped " +
                 "at max_lines (and 256 KB; lines over 2,000 characters are cut): longer output returns a map, the first page, and a file path with all of it; page with start_line.")]
    public static string GetFileDiff(
        [Description("Relative path of the file (for a rename, either the old or the new path).")] string path,
        [Description("Compare id (default: the most recent).")] string? compare_id = null,
        [Description("2-way: context lines around each change (default 3).")] int context = PatchOptions.DefaultContextLines,
        [Description("Most lines to return (default 2000).")] int max_lines = AgentViews.DefaultMaxLines,
        [Description("1-based line to start from (for paging capped output).")] int start_line = 1)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        return Safe(() => s switch
        {
            Compare3Session t => ThreeWayViews.FileDiff(t, path, max_lines, start_line),
            CompareSession c => AgentViews.FileDiff(c, path, context, max_lines, start_line),
            _ => "error: unknown compare kind",
        });
    }

    [McpServerTool(Name = "get_stats")]
    [Description("Detailed totals for a compare: files and bytes per status, reasons, biggest changed files, largest " +
                 "size changes, most-changed lines among diffs rendered so far (3-way: most conflicted files), read cost and phase timings.")]
    public static string GetStats([Description("Compare id (default: the most recent).")] string? compare_id = null)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        return Safe(() => s switch
        {
            Compare3Session t => ThreeWayViews.Stats(t),
            CompareSession c => AgentViews.Stats(c),
            _ => "error: unknown compare kind",
        });
    }

    [McpServerTool(Name = "export_changeset")]
    [Description("2-way only. Write the whole left->right change set as one git-style patch file and return its path and counts " +
                 "(never the patch itself). literal=true includes eol/encoding-only changes as real hunks, so `git apply` " +
                 "reproduces the right tree's text files exactly; binary and large files are described, not carried.")]
    public static string ExportChangeset(
        [Description("Compare id (default: the most recent).")] string? compare_id = null,
        [Description("Output .patch path (default: a file in the temp dir).")] string? out_path = null,
        [Description("Context lines (default 3).")] int context = PatchOptions.DefaultContextLines,
        [Description("Carry eol/encoding-only changes as hunks instead of notes.")] bool literal = false)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        if (s is not CompareSession c) return $"error: compare {s!.Id} is 3-way; export_changeset needs a 2-way compare";
        return Safe(() => AgentViews.Export(c, out_path, context, literal));
    }

    [McpServerTool(Name = "apply_changeset")]
    [Description("2-way only. Port a compare's left->right changes onto a third tree (target) by 3-way merge: per hunk applied, " +
                 "fuzzy (applied at a shifted line), already (target has it) or conflict. DRY RUN unless write=true; " +
                 "with write=true only conflict-free files are written (atomically), conflicted files are left untouched.")]
    public static string ApplyChangeset(
        [Description("Directory to apply the changes onto.")] string target,
        [Description("Compare id (default: the most recent).")] string? compare_id = null,
        [Description("Actually modify the target (default false = dry run).")] bool write = false,
        [Description("Most conflicted files listed inline (default 50); the full per-hunk report goes to a file.")] int max_files = 50)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        if (s is not CompareSession c) return $"error: compare {s!.Id} is 3-way; apply_changeset needs a 2-way compare";
        try { return AgentViews.Apply(c, target, write, Math.Clamp(max_files, 1, 500)); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_compares")]
    [Description("Saved compares, newest first (every finished compare is saved; reopen any of them by passing its id as " +
                 "compare_id to the other tools — no re-compare). Shows id, when, kind, roots and headline counts.")]
    public static string ListCompares([Description("Most to list (default 20).")] int max = 20)
    {
        if (Sessions.ResultsRoot is not { } root) return "compares are not being saved in this server";
        var saved = ResultStore.List(root, Math.Clamp(max, 1, 200));
        if (saved.Count == 0) return $"no saved compares in {root}";
        var o = new System.Text.StringBuilder($"{saved.Count} saved compare(s) in {root} (newest first):\n");
        foreach (var c in saved)
        {
            o.Append($"{c.Id}  {c.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {c.Kind,-8} {c.State,-7} ");
            o.Append(string.Join("  ", c.Roots.Select(r => $"{r.Name}={r.Path}")));
            var counts = c.Counts.Where(x => x.Count > 0 && x.Name != "identical").Select(x => $"{x.Name} {x.Count:N0}").ToList();
            if (counts.Count > 0) o.Append("  · ").Append(string.Join(", ", counts));
            if (c.Error is { } e) o.Append($"  · error: {e}");
            o.Append('\n');
        }
        return o.ToString();
    }

    [McpServerTool(Name = "cancel_compare")]
    [Description("Stop a running compare (2- or 3-way). The hashes it already read are kept, so starting the same compare " +
                 "again only reads what this one didn't get to.")]
    public static async Task<string> CancelCompare([Description("Compare id (required: the one to stop).")] string compare_id)
    {
        if (string.IsNullOrWhiteSpace(compare_id)) return "error: compare_id is required (get_summary or list_compares shows ids)";
        if (Find(compare_id, out var s) is { } err) return err;
        if (!s!.Cancel()) return $"compare {s.Id} is not running ({(s.Error is { } e ? e : "finished")}); nothing to cancel";
        // It stops within moments (a chunk per file in flight) after saving the hashes it read.
        await WaitAsync(s, 30);
        return s.IsDone ? $"compare {s.Id}: {(s.Cancelled ? AgentViews.CancelledText(s) : s.Error is { } e2 ? $"failed before the cancel took effect: {e2}\n" : "finished before the cancel took effect\n")}"
                        : $"compare {s.Id}: cancelling — still saving what it read; get_summary shows when it has stopped\n";
    }

    [McpServerTool(Name = "write_merge")]
    [Description("Write a finished compare3's merge as an overlay on v1: only the files the merge changes in v1 (v2's " +
                 "one-sided changes, clean merges, text conflicts with diff3 markers) plus deletes.txt and conflicts.txt " +
                 "(binary/large/delete/rename conflicts with each side's file). Delete deletes.txt's paths from v1, remove the " +
                 "directories that left empty, then copy files\\ over v1, to get the merged tree (the CLI's apply-overlay " +
                 "does all three). Reads the changed files of the three trees.")]
    public static string WriteMerge(
        [Description("compare3 id (default: the most recent).")] string? compare_id = null,
        [Description("A new or empty directory outside the three trees (default: merge\\ in the compare's result directory).")] string? out_dir = null)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        if (s is not Compare3Session t) return $"compare {s!.Id} is a 2-way compare; write_merge needs a compare3 (apply_changeset ports a 2-way change set)";
        if (!t.IsDone) return $"compare {t.Id} is still running — wait for it (get_summary wait_seconds) first";
        if (t.Error is { } failed) return $"compare {t.Id} {(t.Cancelled ? "was cancelled" : $"failed: {failed}")}";
        var dir = out_dir ?? (t.ResultDir is { } rd ? Path.Combine(rd, "merge") : null);
        if (dir is null) return "error: out_dir is required (this compare has no result directory)";
        try
        {
            var o = MergeOverlay.Write(t.Report!, t.Base, t.V1, t.V2, dir, t.Options.Parallelism);
            return (t.Reopened ? "note: reopened compare — written from the trees as they are now\n" : "") + ThreeWayViews.OverlayText(o);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return $"error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "write_report")]
    [Description("Write a browsable HTML report of a compare (2- or 3-way) into its result directory and return the path to " +
                 "open — for a human. A folder tree with filters; each file's diff loads when expanded (inline or side by side; " +
                 "3-way shows merges with conflict markers). Reads the changed files to render their diffs.")]
    public static string WriteReport(
        [Description("Compare id (default: the most recent).")] string? compare_id = null,
        [Description("Also block-diff large files (over 16 MB; reads them whole).")] bool include_large = false,
        [Description("List identical files too (2-way).")] bool include_identical = false,
        [Description("Most file diffs to render (default 5000).")] int max_diffs = 5000)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        if (!s!.IsDone) return $"compare {s.Id} is still running — wait for it (get_summary wait_seconds) first";
        if (s.Error is { } failed) return $"compare {s.Id} {(s.Cancelled ? "was cancelled" : $"failed: {failed}")}";
        try
        {
            var r = HtmlReport.Write(s, new HtmlReportOptions { IncludeLarge = include_large, IncludeIdentical = include_identical, MaxDiffs = Math.Max(0, max_diffs) });
            return $"report: {r.IndexPath}\n" +
                   $"  {r.Files:N0} file(s) listed · {r.Rendered:N0} diff(s) rendered" +
                   (r.Capped > 0 ? $" · {r.Capped:N0} cut (whole diff linked)" : "") +
                   (r.Large > 0 ? $" · {r.Large:N0} large not block-diffed (include_large=true)" : "") +
                   (r.NotRendered > 0 ? $" · {r.NotRendered:N0} past max_diffs" : "") +
                   (r.Unreadable > 0 ? $" · {r.Unreadable:N0} unreadable or not renderable" : "") + "\n" +
                   "  open index.html in a browser (works from disk, no server)\n";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"error: {ex.Message}";
        }
    }

    private static string? Options(string? cache, int threads, out CompareOptions options)
    {
        options = new CompareOptions();
        var mode = cache?.Trim().ToLowerInvariant() switch
        {
            null or "" or "on" => (CacheMode?)CacheMode.On,
            "off" => CacheMode.Off,
            "rehash" => CacheMode.Rehash,
            _ => null,
        };
        if (mode is null) return $"error: cache must be on, off or rehash (got '{cache}')";
        options = threads > 0
            ? new CompareOptions { Cache = mode.Value, Parallelism = Math.Min(threads, 64) }
            : new CompareOptions { Cache = mode.Value };
        return null;
    }

    private static string Started(Session s, string summary)
        => s.IsDone ? summary : $"started compare {s.Id}: {s.Title}\n" + summary.Split('\n', 2)[1];

    private static string? Find(string? id, out Session? s)
    {
        try { s = Sessions.Get(id); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            s = null;
            return $"error: {ex.Message}";
        }
        if (s is not null) return null;
        var known = Sessions.All();
        return known.Count == 0 && string.IsNullOrWhiteSpace(id)
            ? "no compare in this session yet — call start_compare(left, right) or start_compare3(base, v1, v2), or list_compares for saved ones"
            : $"unknown compare_id '{id}' (this session: {(known.Count == 0 ? "none" : string.Join(", ", known.Select(k => k.Id)))}; list_compares shows saved ones)";
    }

    private static async Task WaitAsync(Session s, int seconds)
    {
        if (seconds <= 0 || s.IsDone) return;
        await Task.WhenAny(s.Task, Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, 600))));
    }

    private static string Safe(Func<string> view)
    {
        try { return view(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"error: {ex.Message}";
        }
        // A view that renders files in parallel wraps their failure.
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(e => e is IOException or UnauthorizedAccessException))
        {
            return $"error: {ex.Flatten().InnerExceptions[0].Message}";
        }
    }
}
