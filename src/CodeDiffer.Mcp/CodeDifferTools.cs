using System.ComponentModel;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Sessions;
using ModelContextProtocol.Server;

namespace CodeDiffer.Mcp;

/// <summary>
/// The tools exposed to the agent. A compare runs in the background and is queried by id; every answer is
/// bounded (constant-size summary, paged lists, capped per-file diffs with a .patch file for the rest), so
/// a 90 GB tree never floods the context. Deliberately a small surface to keep the standing token cost low.
/// compare_id may be omitted everywhere: it then means the most recent compare.
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
        var mode = cache?.Trim().ToLowerInvariant() switch
        {
            null or "" or "on" => (CacheMode?)CacheMode.On,
            "off" => CacheMode.Off,
            "rehash" => CacheMode.Rehash,
            _ => null,
        };
        if (mode is null) return $"error: cache must be on, off or rehash (got '{cache}')";
        var options = new CompareOptions { Cache = mode.Value };
        if (threads > 0) options = new CompareOptions { Cache = mode.Value, Parallelism = Math.Min(threads, 64) }; // init-only

        CompareSession s;
        try { s = Sessions.Start(left, right, options); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return $"error: {ex.Message}";
        }
        await WaitAsync(s, wait_seconds);
        return s.IsDone ? AgentViews.Summary(s) : $"started compare {s.Id}: {s.Left}  vs  {s.Right}\n" + AgentViews.Summary(s).Split('\n', 2)[1];
    }

    [McpServerTool(Name = "get_summary")]
    [Description("Constant-size summary of a compare: counts by status and reason, changed bytes, largest changes, " +
                 "warnings. While it is still running, reports progress; wait_seconds waits for it.")]
    public static async Task<string> GetSummary(
        [Description("Compare id (default: the most recent).")] string? compare_id = null,
        [Description("Seconds to wait for a running compare to finish (default 0).")] int wait_seconds = 0)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        await WaitAsync(s!, wait_seconds);
        return AgentViews.Summary(s!);
    }

    [McpServerTool(Name = "list_files")]
    [Description("Paged, filtered list of files in a compare (no diffs): tag (A/D/M/R), path, reason, sizes. " +
                 "Identical files are hidden unless asked for. lines=true adds hunk count and +/- lines for the page.")]
    public static string ListFiles(
        [Description("Compare id (default: the most recent).")] string? compare_id = null,
        [Description("changed (default), all, or a comma list of added, removed, modified, renamed, identical.")] string? status = null,
        [Description("Glob over relative paths, e.g. src/**/*.c; a pattern without '/' matches file names anywhere (*.h).")] string? path_glob = null,
        [Description("Only modified files with this reason: content, eol, whitespace, encoding, binary.")] string? reason = null,
        [Description("1-based page number.")] int page = 1,
        [Description("Files per page (default 50, max 500).")] int page_size = AgentViews.DefaultPageSize,
        [Description("Also count hunks and +/- lines for this page (reads those files).")] bool lines = false)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        return Safe(() => AgentViews.ListFiles(s!, status, path_glob, reason, page, page_size, lines));
    }

    [McpServerTool(Name = "get_file_diff")]
    [Description("One file's unified diff (git form, `git apply`-able). Hard-capped at max_lines: a longer diff " +
                 "returns its hunk map, the first page, and the path of a .patch file with all of it; page with start_line.")]
    public static string GetFileDiff(
        [Description("Relative path of the file (for a rename, either the old or the new path).")] string path,
        [Description("Compare id (default: the most recent).")] string? compare_id = null,
        [Description("Context lines around each change (default 3).")] int context = PatchOptions.DefaultContextLines,
        [Description("Most patch lines to return (default 2000).")] int max_lines = AgentViews.DefaultMaxLines,
        [Description("1-based patch line to start from (for paging a capped diff).")] int start_line = 1)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        return Safe(() => AgentViews.FileDiff(s!, path, context, max_lines, start_line));
    }

    [McpServerTool(Name = "get_stats")]
    [Description("Detailed totals for a compare: files and bytes per status, reasons, biggest changed files, " +
                 "largest size changes, most-changed lines among diffs rendered so far, read cost and phase timings.")]
    public static string GetStats([Description("Compare id (default: the most recent).")] string? compare_id = null)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        return Safe(() => AgentViews.Stats(s!));
    }

    [McpServerTool(Name = "export_changeset")]
    [Description("Write the whole left->right change set as one git-style patch file and return its path and counts " +
                 "(never the patch itself). literal=true includes eol/encoding-only changes as real hunks, so `git apply` " +
                 "reproduces the right tree's text files exactly; binary and large files are described, not carried.")]
    public static string ExportChangeset(
        [Description("Compare id (default: the most recent).")] string? compare_id = null,
        [Description("Output .patch path (default: a file in the temp dir).")] string? out_path = null,
        [Description("Context lines (default 3).")] int context = PatchOptions.DefaultContextLines,
        [Description("Carry eol/encoding-only changes as hunks instead of notes.")] bool literal = false)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        return Safe(() => AgentViews.Export(s!, out_path, context, literal));
    }

    private static string? Find(string? id, out CompareSession? s)
    {
        s = Sessions.Get(id);
        if (s is not null) return null;
        var known = Sessions.All();
        return known.Count == 0
            ? "no compare yet — call start_compare(left, right)"
            : $"unknown compare_id '{id}' (known: {string.Join(", ", known.Select(k => k.Id))})";
    }

    private static async Task WaitAsync(CompareSession s, int seconds)
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
