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
        [Description("Concurrent file reads (default: 64 if a tree is on a network share, where each file is mostly a wait on the server; else min(cores, 8)). Max 64.")] int threads = 0,
        [Description("Seconds to wait for completion before returning (default 30; 0 = return at once).")] int wait_seconds = 30)
    {
        if (Options(cache, threads, [left, right], out var options) is { } bad) return bad;
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
                 "(content, stamps, modify/delete, add/add, rename/rename...). Background, returns a compare_id like start_compare.")]
    public static async Task<string> StartCompare3(
        [Description("The common base directory.")] string @base,
        [Description("First variant directory.")] string v1,
        [Description("Second variant directory.")] string v2,
        [Description("Hash cache: on (default), off, rehash.")] string cache = "on",
        [Description("Concurrent file reads (default: 64 if a tree is on a network share, where each file is mostly a wait on the server; else min(cores, 8)). Max 64.")] int threads = 0,
        [Description("Seconds to wait for completion before returning (default 30; 0 = return at once).")] int wait_seconds = 30,
        [Description("Merge a file whose colliding lines differ only in keyword stamps ($Id: ... $), each stamp from the side " +
                     "that changed it, v2's where both did (default false: a \"stamps\" conflict).")] bool resolve_stamps = false)
    {
        if (Options(cache, threads, [@base, v1, v2], out var options) is { } bad) return bad;
        if (resolve_stamps) options = new CompareOptions { Cache = options.Cache, Parallelism = options.Parallelism, ResolveStamps = true };
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
        [Description("Paths to list: a glob over relative paths (src/**/*.c; one without '/' matches file names anywhere: *.h), or " +
                     "several split by ';' (*.c;*.cpp), '!' before one to leave it out (*.c;!test/**), /regex/ for a regex.")] string? path_glob = null,
        [Description("File types to list, a comma list: code, build (projects, makefiles, config), docs (other text), binary.")] string? type = null,
        [Description("Noise to leave out: all, or a comma list of stamps (version-control $Id$/$Date$ keywords), numbers " +
                     "(numbers, dates, IDs in a non-code file), comments, whitespace, eol, encoding. A file goes only when all its noise is " +
                     "left out; 3-way, a path goes when every side that touched it is (a conflict never, but a stamps one whose sides only stamped it). type=code with " +
                     "hide_noise=all lists the real code changes.")] string? hide_noise = null,
        [Description("2-way only: files with this reason (modified, or renamed and edited): content, eol, whitespace, encoding, " +
                     "binary, metadata; or unreadable: the files of any status that couldn't be read.")] string? reason = null,
        [Description("1-based page number.")] int page = 1,
        [Description("Files per page (default 50, max 500).")] int page_size = AgentViews.DefaultPageSize,
        [Description("2-way only: also count hunks and +/- lines for this page (reads those files).")] bool lines = false)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        if (s is Compare3Session && (reason is not null || lines))
            return $"error: {(reason is not null ? "reason" : "lines")} is 2-way only; a 3-way listing has outcomes (status=conflict, merged, ...)\n";
        return Safe(() => s switch
        {
            Compare3Session t => ThreeWayViews.ListFiles(t, status, path_glob, page, page_size, type, hide_noise),
            CompareSession c => AgentViews.ListFiles(c, status, path_glob, reason, page, page_size, lines, type, hide_noise),
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
        [Description("2-way only: context lines around each change (default 3).")] int? context = null,
        [Description("Most lines to return (default 2000).")] int max_lines = AgentViews.DefaultMaxLines,
        [Description("1-based line to start from (for paging capped output).")] int start_line = 1,
        CancellationToken cancellationToken = default)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        if (s is Compare3Session && context is not null)
            return "error: context is 2-way only; a 3-way file is the whole merged file with its conflict markers\n";
        return Safe(() => s switch
        {
            Compare3Session t => ThreeWayViews.FileDiff(t, path, max_lines, start_line, cancellationToken),
            CompareSession c => AgentViews.FileDiff(c, path, context ?? PatchOptions.DefaultContextLines, max_lines, start_line, cancellationToken),
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
                 "(never the patch itself). literal=true carries eol-only and UTF-8 BOM changes as real hunks; binary and large files, " +
                 "text that isn't UTF-8 (UTF-16, legacy code pages) and unreadable files are described in '#' lines, not carried, " +
                 "and the summary counts them. out_path must be outside both trees.")]
    public static string ExportChangeset(
        [Description("Compare id (default: the most recent).")] string? compare_id = null,
        [Description("Output .patch path, replaced if it exists (default: changeset.patch in the compare's saved result dir, replaced on each export).")] string? out_path = null,
        [Description("Context lines (default 3).")] int context = PatchOptions.DefaultContextLines,
        [Description("Carry eol-only and UTF-8 BOM changes as hunks instead of notes (UTF-16 and legacy text are never carried).")] bool literal = false)
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
        [Description("Most conflicted files listed inline (default 50); the full per-hunk report goes to a file.")] int max_files = 50,
        [Description("Merge lines that collide only in keyword stamps ($Id: ... $), keeping the target's stamp where it changed " +
                     "it, else the change's (default false: such a hunk is a conflict).")] bool resolve_stamps = false,
        CancellationToken cancellationToken = default)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        if (s is not CompareSession c) return $"error: compare {s!.Id} is 3-way; apply_changeset needs a 2-way compare";
        try { return AgentViews.Apply(c, target, write, Math.Clamp(max_files, 1, 500), cancellationToken, resolve_stamps); }
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
        return Safe(() => ListSaved(root, max));
    }

    private static string ListSaved(string root, int max)
    {
        var saved = ResultStore.List(root, Math.Clamp(max, 1, 200));
        if (saved.Count == 0) return $"no saved compares in {root}";
        var o = new System.Text.StringBuilder($"{saved.Count} saved compare(s) in {root} (newest first):\n");
        foreach (var c in saved)
        {
            o.Append($"{c.Id}  {c.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {c.Kind,-8} {c.Shown,-7} ");
            o.Append(string.Join("  ", c.Roots.Select(r => $"{r.Name}={r.Path}")));
            var counts = c.Counts.Where(x => x.Count > 0 && x.Name != "identical").Select(x => $"{x.Name} {x.Count:N0}").ToList();
            if (counts.Count > 0) o.Append("  · ").Append(string.Join(", ", counts));
            if (c.Error is { } e) o.Append($"  · error: {e}");
            o.Append('\n');
        }
        return o.ToString();
    }

    [McpServerTool(Name = "get_run_telemetry")]
    [Description("How recent compares ran, in numbers only (no paths or file names, so it can be shared as is): how each " +
                 "ended (done, cancelled, stopped by a shutdown, or killed outright, with how far it got), peak memory, CPU, " +
                 "time per phase, and in the content phase how much worker time went to opening files and waiting for bytes " +
                 "(the network) versus comparing and hashing. Newest first.")]
    public static string GetRunTelemetry([Description("How many recent compares (default 5).")] int max = 5)
    {
        if (Sessions.ResultsRoot is not { } root) return "compares are not being saved in this server";
        return Safe(() =>
        {
            var saved = ResultStore.List(root, Math.Clamp(max, 1, 50));
            return saved.Count == 0 ? "no saved compares" : string.Join("", saved.Select(RunSummary.Format));
        });
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
        [Description("A new or empty directory outside the three trees (default: merge\\ in the compare's result directory, or merge-2\\, merge-3\\ … once that holds an " +
                     "overlay).")] string? out_dir = null,
        CancellationToken cancellationToken = default)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        if (s is not Compare3Session t) return $"compare {s!.Id} is a 2-way compare; write_merge needs a compare3 (apply_changeset ports a 2-way change set)";
        if (!t.IsDone) return $"compare {t.Id} is still running — wait for it (get_summary wait_seconds) first";
        if (t.Error is { } failed) return $"compare {t.Id} {(t.Cancelled ? "was cancelled" : $"failed: {failed}")}";
        if (out_dir is null && t.ResultDir is null) return "error: out_dir is required (this compare has no result directory)";
        // Held: no prune takes the result away as the overlay is written into it.
        IDisposable? held;
        try { held = out_dir is null ? ResultStore.Hold(t) : null; }
        catch (IOException ex) { return $"error: {ex.Message}"; }
        using var _ = held;
        if (out_dir is null) ResultStore.Restore(t); // pruned by another process: saved again before the overlay goes into it
        // The default is a fresh directory each time (merge, merge-2, ...): an overlay written earlier may be in use, and
        // one another writer claims between the look and the write, or a file of that name, moves this one on to the next.
        // One a prune that doesn't hold results (a CodeDiffer before 1.0.62) takes files out of as it is written is written
        // again (a few times), into the next.
        const int maxDefault = 1000;
        int pruned = 0;
        for (int n = 1; ; n++)
        {
            if (n > maxDefault) return $"error: {t.ResultDir}: merge, merge-2 … merge-{maxDefault} are all taken; give out_dir or prune the old overlays";
            var dir = out_dir ?? Path.Combine(t.ResultDir!, n == 1 ? "merge" : $"merge-{n}");
            try
            {
                if (out_dir is null && (File.Exists(dir) || (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()))) continue;
                OverlayResult o;
                try { o = MergeOverlay.Write(t.Report!, t.Base, t.V1, t.V2, dir, t.Options.Parallelism, cancellationToken); }
                catch (IOException) when (out_dir is null && pruned < 3 && !Directory.Exists(dir))
                {
                    pruned++;
                    ResultStore.Restore(t);
                    continue;
                }
                if (out_dir is null)
                {
                    ResultStore.Restore(t); // pruned as it was written: saved again, so the overlay is in a listed result
                    if (!MergeOverlay.Intact(o))
                    {
                        try { Directory.Delete(o.Dir, recursive: true); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
                        if (++pruned <= 3) continue;
                        return $"error: {o.Dir}: the result was pruned while the overlay was written into it, {pruned} times; write_merge again or give out_dir";
                    }
                }
                return (t.Reopened ? "note: reopened compare — written from the trees as they are now\n" : "") + ThreeWayViews.OverlayText(o);
            }
            catch (OverlayDirTakenException) when (out_dir is null) { }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return $"stopped: {dir} is not finished (it holds INCOMPLETE.txt) — never apply it; write_merge again";
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return $"error: {ex.Message}";
            }
        }
    }

    [McpServerTool(Name = "list_conflicts")]
    [Description("A merge overlay's conflicts (write_merge's directory): how many are undecided and decided, the undecided " +
                 "ones grouped by kind, folder and file type, so whole groups can be settled with one resolve_conflicts; with " +
                 "list=true, the matching conflicts one per line (bounded). Reads only the overlay.")]
    public static string ListConflicts(
        [Description("The overlay directory write_merge (or compare3 --merge-out) wrote.")] string overlay_dir,
        [Description("Only conflicts matching any of these path patterns ('/'-separated; *, ?, **; one without '/' is a file " +
                     "or folder name at any depth, like *.h or generated; one with '/', or a leading '/', is from the top, like src/gen).")] string[]? patterns = null,
        [Description("Only these conflict kinds (content, add/add, stamps, binary, large, delete/modify, modify/delete, " +
                     "rename/rename, encoding, line endings, path collision, file/directory clash, add/add (binary), unreadable, " +
                     "behind a link, unlisted directory, unopenable name, not written).")] string[]? kinds = null,
        [Description("Folder depth to group by (default 1).")] int depth = 1,
        [Description("Also list the matching conflicts, one per line.")] bool list = false,
        [Description("Most to list of each, undecided and decided (default 50).")] int max = 50)
    {
        try { return OverlayConflicts.Load(overlay_dir).Text(patterns ?? [], kinds ?? [], Math.Max(1, depth), list, Math.Max(1, max)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return $"error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "resolve_conflicts")]
    [Description("Settle a merge overlay's conflicts in bulk before it is applied: every conflict the patterns and kinds pick " +
                 "is taken from v1, taken from v2 (only if v2's file is still what the overlay saw and lands on no other file), or kept as v1's to " +
                 "regenerate; undo takes choices back. Recorded in the overlay itself (files\\, deletes.txt, decisions.json), " +
                 "so applying the overlay applies the choices. A conflict already decided is left alone until undone, and a markers " +
                 "file edited by hand is never set aside. Dry run " +
                 "unless write=true. Pick with patterns and/or kinds, or all=true for every conflict.")]
    public static string ResolveConflicts(
        [Description("The overlay directory write_merge (or compare3 --merge-out) wrote.")] string overlay_dir,
        [Description("v1, v2, regenerate (keep v1's file until it is regenerated) or undo.")] string choice,
        [Description("Path patterns, as list_conflicts takes them.")] string[]? patterns = null,
        [Description("Conflict kinds, as list_conflicts takes them.")] string[]? kinds = null,
        [Description("Every conflict (instead of patterns and kinds).")] bool all = false,
        [Description("Record it (default false: a dry run that says what it would do).")] bool write = false)
    {
        patterns ??= [];
        kinds ??= [];
        if (all == (patterns.Length > 0 || kinds.Length > 0))
            return "error: give patterns and/or kinds, or all=true — not both, not neither";
        try { return ConflictResolver.Text(ConflictResolver.Run(overlay_dir, choice, patterns, kinds, write)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
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
        [Description("Most file diffs to render (default 5000).")] int max_diffs = 5000,
        CancellationToken cancellationToken = default)
    {
        if (Find(compare_id, out var s) is { } err) return err;
        if (!s!.IsDone) return $"compare {s.Id} is still running — wait for it (get_summary wait_seconds) first";
        if (s.Error is { } failed) return $"compare {s.Id} {(s.Cancelled ? "was cancelled" : $"failed: {failed}")}";
        try
        {
            var r = HtmlReport.Write(s, new HtmlReportOptions { IncludeLarge = include_large, IncludeIdentical = include_identical, MaxDiffs = Math.Max(0, max_diffs) }, ct: cancellationToken);
            return $"report: {r.IndexPath}\n" +
                   $"  {r.Files:N0} file(s) listed · {r.Rendered:N0} diff(s) rendered" +
                   (r.Capped > 0 ? $" · {r.Capped:N0} cut (whole diff linked)" : "") +
                   (r.Large > 0 ? $" · {r.Large:N0} large not block-diffed (include_large=true)" : "") +
                   (r.NotRendered > 0 ? $" · {r.NotRendered:N0} past max_diffs" : "") +
                   (r.Unreadable > 0 ? $" · {r.Unreadable:N0} unreadable or not renderable" : "") +
                   (r.Stopped > 0 ? $" · STOPPED: {r.Stopped:N0} without a diff (write_report again for the rest)" : "") + "\n" +
                   "  open index.html in a browser (works from disk, no server)\n";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"error: {ex.Message}";
        }
    }

    private static string? Options(string? cache, int threads, string[] trees, out CompareOptions options)
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
        options = new CompareOptions { Cache = mode.Value, Parallelism = threads > 0 ? Math.Min(threads, 64) : CompareOptions.DefaultThreadsFor(trees) };
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
        // A bad path an agent passed (a NUL in it, an unsupported form) is an answer too, never a protocol error.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
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
