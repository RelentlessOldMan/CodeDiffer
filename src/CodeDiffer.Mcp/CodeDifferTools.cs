using System.ComponentModel;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Sessions;
using ModelContextProtocol.Server;

namespace CodeDiffer.Mcp;

/// <summary>
/// The tools exposed to the agent. A compare (2-way or 3-way) runs in the background and is queried by id;
/// every answer is bounded (constant-size summary, paged lists, capped per-file views with a file for the
/// rest), so a 90 GB tree never floods the context. Deliberately a small surface to keep the standing
/// token cost low. compare_id may be omitted everywhere: it then means the most recent compare.
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
                 "merge outcome and conflict kind). While it is still running, reports progress; wait_seconds waits for it.")]
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
                 "at max_lines: longer output returns a map, the first page, and a file path with all of it; page with start_line.")]
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
        s = Sessions.Get(id);
        if (s is not null) return null;
        var known = Sessions.All();
        return known.Count == 0
            ? "no compare yet — call start_compare(left, right) or start_compare3(base, v1, v2)"
            : $"unknown compare_id '{id}' (known: {string.Join(", ", known.Select(k => k.Id))})";
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
    }
}
