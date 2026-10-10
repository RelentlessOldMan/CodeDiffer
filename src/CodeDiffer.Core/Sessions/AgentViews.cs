using System.Text;
using System.Text.RegularExpressions;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Port;

namespace CodeDiffer.Core.Sessions;

/// <summary>
/// The agent-facing views of a compare (docs/OUTPUT.md §4): every answer is bounded — a constant-size
/// summary, paged file lists, one file's diff with a hard line cap — so a 90 GB tree never floods the
/// agent's context. Plain text, compact, with the next useful call spelled out.
/// </summary>
public static class AgentViews
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;
    public const int DefaultMaxLines = 2000;

    /// <summary>Where capped diffs, exported changesets and apply reports go: the compare's result directory, or
    /// (for an unsaved compare) %TEMP%\codediffer\&lt;id&gt; — CODEDIFFER_OUT_DIR overrides the temp base.</summary>
    public static string OutDir(Session s) => s.ResultDir ?? Path.Combine(
        Environment.GetEnvironmentVariable("CODEDIFFER_OUT_DIR") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "codediffer"),
        s.Id);

    /// <summary>
    /// A warning when a file's size on disk no longer matches what the compare recorded — the diff is rendered
    /// from the live trees, so it would show today's file, not the compared one. Null when sizes still match
    /// (a same-size edit can't be detected without re-hashing; reopened compares say so in their header).
    /// </summary>
    internal static string? StaleSide(string full, long size, string name)
    {
        try
        {
            var fi = new FileInfo(full);
            if (!fi.Exists) return $"the {name} file is gone";
            return fi.Length == size ? null : $"the {name} file is now {Bytes(fi.Length)} (was {Bytes(size)})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"the {name} file is unreadable ({ex.Message})"; }
    }

    internal static string? StaleNote(params string?[] parts)
    {
        var found = parts.Where(p => p is not null).ToList();
        return found.Count == 0 ? null : "WARNING: changed since the compare — " + string.Join("; ", found) + ". Shown as the files are now.";
    }

    internal static string? Stale(CompareSession s, FileChange c)
    {
        string? L(string rel) => StaleSide(Full(s.Left, rel), c.LeftSize, "left");
        string? R(string rel) => StaleSide(Full(s.Right, rel), c.RightSize, "right");
        return c.Status switch
        {
            ChangeStatus.Added => StaleNote(R(c.RelativePath)),
            ChangeStatus.Removed => StaleNote(L(c.RelativePath)),
            ChangeStatus.Renamed => StaleNote(L(c.RenamedFrom!), R(c.RelativePath)),
            _ => StaleNote(L(c.RelativePath), R(c.RelativePath)),
        };
    }

    internal static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>One line for a reopened compare: when it ran, and that diffs come from the trees as they are now.</summary>
    internal static string? ReopenedNote(Session s) => s.Reopened
        ? $"saved compare from {s.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({s.ResultDir}); verdicts are as of then, diffs are rendered from the trees now\n"
        : null;

    /// <summary>The compare's state; when finished, the constant-size summary.</summary>
    public static string Summary(CompareSession s)
    {
        var o = new StringBuilder();
        o.Append($"compare {s.Id} · {s.Left}  vs  {s.Right}\n");
        if (Partial(s) is { } p) return Running(o, s, p);
        if (Pending(s) is { } pending) return o.Append(pending).ToString();
        var r = s.Report!;

        o.Append($"{r.Total:N0} files   (finished in {Clock(s.Elapsed)})\n");
        o.Append(ReopenedNote(s));
        o.Append($"  identical {r.Count(ChangeStatus.Identical),8:N0}    (hidden by default)\n");
        o.Append($"  modified  {r.Count(ChangeStatus.Modified),8:N0}    {Reasons(r)}\n");
        o.Append($"  added     {r.Count(ChangeStatus.Added),8:N0}\n");
        o.Append($"  removed   {r.Count(ChangeStatus.Removed),8:N0}\n");
        int renamed = r.Count(ChangeStatus.Renamed);
        int pure = r.Changes.Count(c => c.PureRename);
        o.Append($"  renamed   {renamed,8:N0}    ({pure:N0} pure, {renamed - pure:N0} rename+edit)\n");

        var changed = r.Changes.Where(c => c.Status != ChangeStatus.Identical).ToList();
        o.Append($"changed bytes: {Bytes(changed.Sum(c => c.LeftSize))} left · {Bytes(changed.Sum(c => c.RightSize))} right\n");
        var movers = changed.OrderByDescending(c => Math.Abs(c.RightSize - c.LeftSize)).ThenBy(c => c.RelativePath, StringComparer.Ordinal).Take(5).ToList();
        if (movers.Count > 0)
            o.Append("largest size changes: ").Append(string.Join(", ", movers.Select(c => $"{c.RelativePath} ({Delta(c.RightSize - c.LeftSize)})"))).Append('\n');
        Warnings(o, r);
        Saved(o, s);
        if (changed.Count > 0)
            o.Append($"next: list_files (paged) · get_file_diff(path) · export_changeset for the whole patch · write_report for a browsable HTML report\n");
        return o.ToString();
    }

    /// <summary>
    /// A running compare: what it is doing (with a rough time left), and the differences found so far — the
    /// agent can start on those with list_files / get_file_diff while the rest is read.
    /// </summary>
    private static string Running(StringBuilder o, CompareSession s, CompareProgress p)
    {
        o.Append($"running · {Clock(s.Elapsed)} elapsed\n");
        o.Append("  now: ").Append(ProgressView.Line(p)).Append('\n');
        var found = p.Found();
        if (found.Length == 0)
        {
            o.Append(p.Phase < ComparePhase.Contents
                ? "nothing found yet: differences start to show once both trees are listed\n"
                : "no differences found so far\n");
            o.Append("next: get_summary(wait_seconds=…) to wait\n");
            return o.ToString();
        }

        int N(ChangeStatus st) => found.Count(f => f.Change.Status == st);
        o.Append($"found so far: {N(ChangeStatus.Modified):N0} modified · {N(ChangeStatus.Added):N0} added · {N(ChangeStatus.Removed):N0} removed\n");
        if (found.Any(f => f.MayBeRename))
            o.Append("  (renames are matched at the end: an added and a removed file may still turn out to be one rename)\n");
        foreach (var f in found.Take(10)) o.Append("  ").Append(Tag(f.Change)).Append(' ').Append(f.Change.RelativePath)
            .Append(f.Change.ReasonLabel is { } rr ? $"  [{rr}]" : "").Append('\n');
        if (found.Length > 10) o.Append($"  ... {found.Length - 10:N0} more (list_files)\n");
        o.Append("next: list_files and get_file_diff work on what is found so far · get_summary(wait_seconds=…) waits for the rest\n");
        return o.ToString();
    }

    /// <summary>Totals by status and reason, byte mass, biggest files, read cost, phase timings.</summary>
    public static string Stats(CompareSession s)
    {
        var o = new StringBuilder();
        o.Append($"compare {s.Id} · {s.Left}  vs  {s.Right}\n");
        if (Pending(s) is { } pending) return o.Append(pending).ToString();
        var r = s.Report!;

        o.Append("status      files        left bytes      right bytes\n");
        foreach (var st in Enum.GetValues<ChangeStatus>())
        {
            var set = r.Changes.Where(c => c.Status == st).ToList();
            o.Append($"  {StatusToken(st),-9} {set.Count,7:N0}  {Bytes(set.Sum(c => c.LeftSize)),15}  {Bytes(set.Sum(c => c.RightSize)),15}\n");
        }
        o.Append($"reasons     {Reasons(r)}\n");

        var changed = r.Changes.Where(c => c.Status != ChangeStatus.Identical).ToList();
        if (changed.Count > 0)
        {
            o.Append("biggest changed files:\n");
            foreach (var c in changed.OrderByDescending(c => Math.Max(c.LeftSize, c.RightSize)).ThenBy(c => c.RelativePath, StringComparer.Ordinal).Take(10))
                o.Append($"  {Bytes(Math.Max(c.LeftSize, c.RightSize)),10}  {Tag(c)} {Name(c)}\n");
            o.Append("largest size changes:\n");
            foreach (var c in changed.OrderByDescending(c => Math.Abs(c.RightSize - c.LeftSize)).ThenBy(c => c.RelativePath, StringComparer.Ordinal).Take(10))
                o.Append($"  {Delta(c.RightSize - c.LeftSize),10}  {Tag(c)} {Name(c)}\n");
        }
        var lined = s.DiffInfo.Where(kv => kv.Value.Kind == "text").OrderByDescending(kv => kv.Value.AddedLines + kv.Value.RemovedLines)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(10).ToList();
        if (lined.Count > 0)
        {
            o.Append($"most-changed lines (of {s.DiffInfo.Count:N0} file diff(s) rendered so far):\n");
            foreach (var (path, i) in lined)
                o.Append($"  +{i.AddedLines:N0} -{i.RemovedLines:N0} in {i.Hunks:N0} hunk(s)  {path}\n");
        }

        o.Append($"read cost: {r.ComparedPairs:N0} same-size pair(s) · {r.CacheHits:N0} side(s) from hash cache" +
                 (r.CodeCompassHits > 0 ? $" ({r.CodeCompassHits:N0} via CodeCompass)" : "") +
                 (r.PendingFiles > 0 ? $" · {r.PendingFiles:N0} too recently modified to cache yet" : "") +
                 $" · {Bytes(r.BytesRead)} read · {Clock(s.Elapsed)} · threads {s.Options.Parallelism} · cache {s.Options.Cache.ToString().ToLowerInvariant()}\n");
        if (r.Timings.Count > 0)
            o.Append("phases: ").Append(string.Join(" · ", r.Timings.Select(t => $"{t.Phase} {t.Elapsed.TotalSeconds:F1}s"))).Append('\n');
        Warnings(o, r);
        return o.ToString();
    }

    /// <summary>
    /// One page of changed files: tag · path · reason · sizes (and, with <paramref name="lines"/>, the
    /// hunk count and ±lines, rendered for just this page). Diffs are never included.
    /// </summary>
    /// <param name="status">"changed" (default: everything but identical), "all", or a comma list of
    /// added, removed, modified, renamed, identical.</param>
    public static string ListFiles(CompareSession s, string? status = null, string? pathGlob = null, string? reason = null,
        int page = 1, int pageSize = DefaultPageSize, bool lines = false)
    {
        var o = new StringBuilder();
        // While running: the differences found so far, in the order found (so earlier pages don't shift).
        var progress = Partial(s);
        PartialChange[]? found = progress?.Found();
        if (found is null && Pending(s) is { } pending) return o.Append($"compare {s.Id}: ").Append(pending).ToString();
        IReadOnlyList<FileChange> changes = found is null ? s.Report!.Changes : [.. found.Select(f => f.Change)];
        var maybeRename = found is null ? null : found.Where(f => f.MayBeRename).Select(f => f.Change.RelativePath).ToHashSet(StringComparer.Ordinal);

        // "unreadable" is what a listing marks a file whose verdict is unknown: not a reason, but asked for as one.
        bool unreadableOnly = string.Equals(reason?.Trim(), "unreadable", StringComparison.OrdinalIgnoreCase);
        ChangeReason? reasonFilter = null;
        if (!TryStatuses(status, out var statuses, out var err) || (!unreadableOnly && !TryReason(reason, out reasonFilter, out err)))
            return $"error: {err}\n";
        var glob = string.IsNullOrWhiteSpace(pathGlob) ? null : Glob(pathGlob);

        var match = changes.Where(c => statuses.Contains(c.Status)
            && (unreadableOnly ? c.ReasonLabel == "unreadable" : reasonFilter is null || c.Reason == reasonFilter)
            && (glob is null || glob(c.RelativePath) || (c.RenamedFrom is { } f && glob(f)))).ToList();

        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        int pages = Math.Max(1, (match.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, pages);
        var slice = match.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        // A file that may still become part of a rename isn't rendered yet: its final diff may differ.
        if (lines) Render(s, maybeRename is null ? slice : slice.Where(c => !maybeRename.Contains(c.RelativePath)).ToList());

        if (progress is not null)
            o.Append($"compare {s.Id} · RUNNING, partial: {found!.Length:N0} difference(s) found so far, in the order found · {ProgressView.Line(progress, found: false)}\n");
        o.Append($"compare {s.Id} · {match.Count:N0} file(s) match{(progress is null ? "" : " so far")} · page {page}/{pages}\n");
        foreach (var c in slice)
        {
            o.Append($"{Tag(c)} {Name(c)}");
            if (c.ReasonLabel is { } rr) o.Append($"  [{rr}]");
            o.Append(c.Status switch
            {
                ChangeStatus.Added => $"  {Bytes(c.RightSize)}",
                ChangeStatus.Removed => $"  {Bytes(c.LeftSize)}",
                ChangeStatus.Renamed => $"  {(c.SimilarityMilli ?? 0) / 10}% similar" + (c.EditedRename && c.SimilarityMilli >= 995 ? " (but edited)" : ""),
                _ => c.LeftSize == c.RightSize ? $"  {Bytes(c.RightSize)}" : $"  {Bytes(c.LeftSize)} -> {Bytes(c.RightSize)}",
            });
            if (s.DiffInfo.TryGetValue(c.RelativePath, out var i))
                o.Append(i.Kind == "text" ? $"  +{i.AddedLines:N0} -{i.RemovedLines:N0} ({i.Hunks:N0} hunk(s))" : $"  ({i.Kind})");
            if (maybeRename?.Contains(c.RelativePath) == true) o.Append("  (may still pair into a rename)");
            o.Append('\n');
        }
        if (page < pages) o.Append($"next page: page={page + 1}\n");
        if (progress is not null)
            o.Append("partial: the list grows until the compare finishes; identical files are known only then\n");
        return o.ToString();
    }

    /// <summary>
    /// One file's unified diff (git form), at most <paramref name="maxLines"/> lines from
    /// <paramref name="startLine"/>. A longer diff is also written whole to a .patch file whose path is
    /// returned, with the hunk headers listed, so the agent can page or open it instead of being flooded.
    /// </summary>
    public static string FileDiff(CompareSession s, string path, int context = PatchOptions.DefaultContextLines,
        int maxLines = DefaultMaxLines, int startLine = 1)
    {
        context = Math.Clamp(context, 0, 1000); // what is rendered, and so what the saved file's name says
        // While running, any difference found so far can be shown (diffs are rendered from the trees anyway).
        var found = Partial(s)?.Found();
        if (found is null && Pending(s) is { } pending) return $"compare {s.Id}: {pending}";
        IReadOnlyList<FileChange> changes = found is null ? s.Report!.Changes : [.. found.Select(f => f.Change)];
        FileChange? Find(string p) => changes.FirstOrDefault(c => c.RelativePath == p)
             ?? changes.FirstOrDefault(c => c.RenamedFrom == p)
             ?? changes.FirstOrDefault(c => string.Equals(c.RelativePath, p, StringComparison.OrdinalIgnoreCase))
             ?? ByShownName(changes, p);
        // The path as given first: " a.txt" is a name of its own, not a.txt with a stray space.
        var c = Find(path = Normalize(path, trim: false));
        if (c is null && Normalize(path) is var trimmed && trimmed != path) c = Find(path = trimmed);
        if (c is null)
        {
            if (found is not null)
                return $"not among the {found.Length:N0} difference(s) found so far in running compare {s.Id}: {path}\n" +
                       "(its contents may not be checked yet; identical files are known only at the end) — get_summary(wait_seconds=…) waits\n";
            var name = path[(path.LastIndexOf('/') + 1)..];
            var near = changes.Where(x => x.RelativePath.EndsWith(name, StringComparison.OrdinalIgnoreCase)).Take(5).Select(x => x.RelativePath).ToList();
            return $"not in compare {s.Id}: {path}\n" + (near.Count > 0 ? "did you mean: " + string.Join(", ", near) + "\n" : "");
        }
        if (c.Status == ChangeStatus.Identical) return $"{c.RelativePath}: identical\n";

        // An add/remove found mid-run may still become a rename: show it, but don't remember its line counts.
        bool mayBeRename = found?.Any(f => f.MayBeRename && f.Change.RelativePath == c.RelativePath) == true;
        var (section, info) = RenderOne(s, c, context, remember: !mayBeRename);
        var all = section.Split('\n');
        int total = all.Length - (section.EndsWith('\n') ? 1 : 0);
        maxLines = Math.Max(1, maxLines);
        startLine = Math.Clamp(startLine, 1, Math.Max(1, total));

        var o = new StringBuilder();
        o.Append($"{Tag(c)} {Name(c)}{(c.ReasonLabel is { } rr ? $"  [{rr}]" : "")}  ");
        o.Append(info.Kind == "text" ? $"+{info.AddedLines:N0} -{info.RemovedLines:N0} in {info.Hunks:N0} hunk(s)" : info.Kind);
        o.Append($" · {total:N0} patch line(s)\n");
        if (found is not null)
            o.Append("note: the compare is still running" + (mayBeRename ? "; this file may still pair into a rename" : "") + "\n");
        if (Stale(s, c) is { } stale) o.Append(stale).Append('\n');

        if (startLine == 1 && Fits(all, total, maxLines))
            return o.Append(section).ToString();

        // Capped: write the whole section once, then show the requested window plus the hunk map. Another context is a
        // sibling directory, never a suffix: x at context 5 and a file named x.U5 would share one name.
        var file = OutFile(s, context == PatchOptions.DefaultContextLines ? "diffs" : $"diffs-U{context}", c.RelativePath, ".patch");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, section, new UTF8Encoding(false));
        int end = WindowEnd(all, total, startLine, maxLines);
        o.Append($"capped: showing lines {startLine:N0}-{end:N0} of {total:N0}{CutNote(all, startLine, end, maxLines, total)}; full patch: {file}\n");
        if (startLine == 1 && info.Hunks > 0)
        {
            var heads = all.Select((l, n) => (l, n)).Where(x => x.l.StartsWith("@@", StringComparison.Ordinal)).ToList();
            o.Append($"hunks ({heads.Count:N0}; line in patch → header):\n");
            foreach (var (l, n) in heads.Take(40)) o.Append($"  {n + 1,7:N0}  {l}\n");
            if (heads.Count > 40) o.Append($"  ... {heads.Count - 40:N0} more\n");
            o.Append("---\n");
        }
        AppendWindow(o, all, startLine, end);
        if (end < total) o.Append($"next: start_line={end + 1}\n");
        return o.ToString();
    }

    /// <summary>Most characters one diff answer carries, and of one line in it (a minified file is one huge line):
    /// a line cap alone lets 2,000 long lines through as megabytes.</summary>
    internal const int MaxChars = 256 * 1024, MaxLineChars = 2000;

    /// <summary>Whether the whole text fits one answer: lines, characters and no over-long line.</summary>
    internal static bool Fits(string[] lines, int total, int maxLines, int maxChars = MaxChars)
    {
        if (total > maxLines) return false;
        long chars = 0;
        for (int i = 0; i < total; i++)
        {
            if (lines[i].Length > MaxLineChars) return false;
            chars += lines[i].Length + 1;
        }
        return chars <= maxChars;
    }

    /// <summary>The last line (1-based) of the window from <paramref name="startLine"/>: at most
    /// <paramref name="maxLines"/> lines and about <paramref name="maxChars"/> characters (long lines counted as cut),
    /// never fewer than one line.</summary>
    internal static int WindowEnd(string[] lines, int total, int startLine, int maxLines, int maxChars = MaxChars)
    {
        int end = startLine - 1;
        long chars = 0;
        while (end < total && end - startLine + 1 < maxLines)
        {
            chars += Math.Min(lines[end].Length, MaxLineChars) + 1;
            if (chars > maxChars && end >= startLine) break;
            end++;
        }
        return end;
    }

    /// <summary>Lines startLine..end, each over <see cref="MaxLineChars"/> cut with a note.</summary>
    internal static void AppendWindow(StringBuilder o, string[] lines, int startLine, int end)
    {
        for (int i = startLine - 1; i < end; i++)
        {
            var l = lines[i];
            if (l.Length <= MaxLineChars) o.Append(l);
            else o.Append(l, 0, MaxLineChars).Append($" ... [line cut: {l.Length:N0} characters; whole line in the file]");
            o.Append('\n');
        }
    }

    /// <summary>Why a window stopped short of the line cap, if it did.</summary>
    internal static string CutNote(string[] lines, int startLine, int end, int maxLines, int total)
    {
        bool chars = end < total && end - startLine + 1 < maxLines;
        bool longLine = false;
        for (int i = startLine - 1; i < end && !longLine; i++) longLine = lines[i].Length > MaxLineChars;
        return (chars ? $" (cut at {MaxChars / 1024} KB)" : "") + (longLine ? $" (lines over {MaxLineChars:N0} characters cut)" : "");
    }

    /// <summary>
    /// Where a capped view writes its whole text: the relative path mirrored under <c>&lt;out&gt;\&lt;sub&gt;</c>, so two
    /// files never share a name (flattening "a/b.c" and "a_b.c" to one name made one overwrite the other).
    /// </summary>
    internal static string OutFile(Session s, string sub, string rel, string suffix)
    {
        var root = Path.Combine(OutDir(s), sub);
        var mirrored = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)) + suffix;
        // The path mirrored, unless the tree has a file where it needs a directory or the reverse (a file a and a
        // directory a.patch/): then one flat name from the path's hash, never another file's.
        bool clash = Directory.Exists(mirrored);
        for (var dir = Path.GetDirectoryName(mirrored); !clash && dir is not null && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
            clash = File.Exists(dir);
        if (!clash) return mirrored;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rel)))[..16].ToLowerInvariant();
        return Path.Combine(root, "_clash", $"{Path.GetFileName(rel)}.{hash}{suffix}");
    }

    /// <summary>The one change whose path, as UTF-8 shows it (an unpaired surrogate as U+FFFD), is this.</summary>
    internal static FileChange? ByShownName(IReadOnlyList<FileChange> changes, string path)
    {
        if (!path.Contains('\uFFFD')) return null;
        var hits = changes.Where(c => ResultStore.Shown(c.RelativePath) == path || (c.RenamedFrom is { } f && ResultStore.Shown(f) == path)).Take(2).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>The whole A→B changeset as one git-style patch file (apply with `git apply`).</summary>
    public static string Export(CompareSession s, string? outPath = null, int context = PatchOptions.DefaultContextLines, bool literal = false)
    {
        if (context < 0) return $"error: context must be 0 or more (got {context})\n";
        if (Pending(s) is { } pending) return $"compare {s.Id}: {pending}";
        var r = s.Report!;
        if (r.LeftDroppedDirectories + r.RightDroppedDirectories > 0)
            return $"error: compare {s.Id} is incomplete ({r.LeftDroppedDirectories} left / {r.RightDroppedDirectories} right director(ies) " +
                   "could not be listed): its adds and removes there may be listing failures, which a patch would carry out as deletes — " +
                   "no patch written; compare again";
        var file = string.IsNullOrWhiteSpace(outPath) ? Path.Combine(OutDir(s), "changeset.patch") : Path.GetFullPath(outPath);
        foreach (var tree in new[] { s.Left, s.Right })
            if (ResultStore.Overlaps(file, tree, oneWay: true))
                return $"error: {file} is inside the compared tree {tree} — the patch would become part of what it describes; write it somewhere else\n";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        PatchStats ps;
        var tmp = file + ".tmp";
        try
        {
            // Written beside its name and moved into place: a failure never leaves a truncated patch behind.
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)) { NewLine = "\n" })
                ps = PatchWriter.Write(w, r, s.Left, s.Right, new PatchOptions { Context = context, Literal = literal, Parallelism = s.Options.Parallelism });
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return $"changeset {s.Id}: {file}  ({Bytes(new FileInfo(file).Length)})\n" +
               $"  {ps.Summary(literal)}\n" +
               "  `git apply` it: what it doesn't carry is described in '#' lines, which git skips\n";
    }

    /// <summary>
    /// Port the compare's left→right changes onto <paramref name="target"/> (dry run unless
    /// <paramref name="write"/>): totals, then the conflicted and fuzzy files (bounded), with the full
    /// per-hunk report written to a file.
    /// </summary>
    public static string Apply(CompareSession s, string target, bool write, int maxFiles = 50)
    {
        if (Pending(s) is { } pending) return $"compare {s.Id}: {pending}";
        var r = ChangePorter.Run(s.Report!, s.Left, s.Right, target, write, parallelism: s.Options.Parallelism);
        return PortText(r, $"compare {s.Id}", Path.Combine(OutDir(s), $"apply-{(write ? "written" : "dryrun")}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt"), maxFiles);
    }

    /// <summary>The bounded port report (also used by the CLI); the full detail goes to <paramref name="reportFile"/>.</summary>
    public static string PortText(PortResult r, string label, string? reportFile, int maxFiles = 50,
                                  string writeHint = "call again with write=true")
    {
        var o = new StringBuilder();
        o.Append($"{(r.Cancelled ? "CANCELLED " : "")}{(r.Written ? r.Cancelled ? "part applied" : "APPLIED" : "dry run")} · {label} onto {r.Target}\n");
        o.Append($"files: {r.Count(PortStatus.Clean):N0} {(r.Written ? "written" : "would apply")} · {r.Count(PortStatus.Already):N0} already there · " +
                 $"{r.Count(PortStatus.Conflict):N0} conflict{(r.Written ? " (left untouched)" : "")}" +
                 (r.Cancelled ? $" · {r.Count(PortStatus.NotReached):N0} not reached" : "") + "\n");
        if (r.Cancelled && r.Written)
            o.Append("  every file written is whole; the rest are untouched — run the same apply again to finish (what is done comes out \"already\")\n");
        o.Append($"hunks: {r.Count(HunkOutcome.Applied):N0} applied · {r.Count(HunkOutcome.Fuzzy):N0} fuzzy (shifted) · " +
                 $"{r.Count(HunkOutcome.Already):N0} already · {r.Count(HunkOutcome.Conflict):N0} conflict\n");

        var conflicts = r.Files.Where(f => f.Status == PortStatus.Conflict).ToList();
        var fuzzy = r.Files.Where(f => f.Status == PortStatus.Clean && f.Hunks.Any(h => h.Outcome == HunkOutcome.Fuzzy)).ToList();
        if (conflicts.Count > 0) o.Append("conflicts:\n");
        foreach (var f in conflicts.Take(maxFiles)) o.Append("  ").Append(FileLine(f)).Append('\n');
        if (conflicts.Count > maxFiles) o.Append($"  ... {conflicts.Count - maxFiles:N0} more\n");
        if (fuzzy.Count > 0) o.Append("fuzzy (applied at a shifted line):\n");
        foreach (var f in fuzzy.Take(Math.Max(5, maxFiles / 2))) o.Append("  ").Append(FileLine(f)).Append('\n');
        if (fuzzy.Count > Math.Max(5, maxFiles / 2)) o.Append($"  ... {fuzzy.Count - Math.Max(5, maxFiles / 2):N0} more\n");

        if (reportFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportFile)!);
            var full = new StringBuilder(o.ToString()).Append("\nall files:\n");
            foreach (var f in r.Files.Where(f => f.Status != PortStatus.NotReached))
            {
                full.Append(FileLine(f)).Append('\n');
                foreach (var h in f.Hunks.Where(h => h.BaseLine > 0 || h.TargetLine > 0))
                    full.Append($"    {h.Outcome.ToString().ToLowerInvariant(),-8} base {h.BaseLine},{h.BaseLines} -> target {h.TargetLine},{h.TargetLines}" +
                                (h.Offset != 0 ? $" (offset {h.Offset:+0;-0})" : "") + "\n");
            }
            File.WriteAllText(reportFile, full.ToString(), new UTF8Encoding(false));
            o.Append($"full report: {reportFile}\n");
        }
        if (!r.Written && !r.Cancelled && r.Count(PortStatus.Clean) > 0) o.Append($"nothing was written — {writeHint} to apply the clean files\n");
        return o.ToString();
    }

    private static string FileLine(PortFile f)
    {
        var name = f.From is { } from && from != f.Path ? $"{from} -> {f.Path}" : f.Path;
        var hs = f.Hunks.Where(h => h.BaseLine > 0 || h.TargetLine > 0).ToList();
        var detail = hs.Count == 0 ? "" : " · " + string.Join(", ",
            hs.GroupBy(h => h.Outcome).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"));
        var where = f.Status == PortStatus.Conflict && hs.FirstOrDefault(h => h.Outcome == HunkOutcome.Conflict) is { BaseLine: > 0 } c
            ? $" (first at base line {c.BaseLine}, target line {c.TargetLine})" : "";
        var action = f.Action == "none" ? "" : f.Action; // conflict/already files do nothing; don't print "none"
        return $"{f.Status.ToString().ToLowerInvariant(),-8} {action,-7} {name}{detail}{where}{(f.Note is null ? "" : " — " + f.Note)}";
    }

    // ---- rendering ----

    private static void Render(CompareSession s, List<FileChange> files)
    {
        var todo = files.Where(c => c.Status != ChangeStatus.Identical && !s.DiffInfo.ContainsKey(c.RelativePath)).ToList();
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = s.Options.Parallelism }, c =>
        {
            // A large file's block diff reads it whole; a listing must stay cheap, so it is only labelled.
            if (Math.Max(c.LeftSize, c.RightSize) > new PatchOptions().MaxTextBytes) s.DiffInfo.TryAdd(c.RelativePath, new(0, 0, 0, "large"));
            else RenderOne(s, c, PatchOptions.DefaultContextLines);
        });
    }

    private static (string Text, FileDiffInfo Info) RenderOne(CompareSession s, FileChange c, int context, bool remember = true)
    {
        var w = new StringWriter { NewLine = "\n" };
        var stats = new PatchStats();
        PatchWriter.WriteChange(w, c, s.Left, s.Right, new PatchOptions { Context = Math.Clamp(context, 0, 1000) }, stats);
        var text = w.ToString();
        var info = Count(text, stats);
        // Remembered for the listings, which count hunks at the default context (more context merges hunks); not when
        // unreadable, as it may be readable next time.
        if (remember && context == PatchOptions.DefaultContextLines && info.Kind != "unreadable") s.DiffInfo[c.RelativePath] = info;
        return (text, info);
    }

    internal static FileDiffInfo Count(string section, PatchStats stats)
    {
        var kind = stats.UnreadableFiles > 0 ? "unreadable" : stats.BinaryFiles > 0 ? "binary" : stats.GiantFiles > 0 ? "large" : stats.NoteFiles > 0 ? "eol/encoding note" : "text";
        int hunks = 0, plus = 0, minus = 0;
        bool body = false; // file headers (---/+++) come before the first @@; after it every +/- is content
        foreach (var line in section.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal)) { body = false; continue; }
            if (line.StartsWith("@@", StringComparison.Ordinal)) { hunks++; body = true; continue; }
            if (!body || line.Length == 0) continue;
            if (line[0] == '+') plus++;
            else if (line[0] == '-') minus++;
        }
        return new FileDiffInfo(hunks, plus, minus, kind);
    }

    // ---- helpers ----

    private static string? Pending(CompareSession s)
    {
        if (s.Cancelled) return CancelledText(s);
        if (s.Error is { } e) return $"FAILED after {Clock(s.Elapsed)}: {e}\n";
        if (!s.IsDone) return $"running · {Clock(s.Elapsed)} elapsed — this needs the finished compare; get_summary shows progress " +
                              "and what is found so far, get_summary(wait_seconds=…) waits for it\n";
        return null;
    }

    public static string CancelledText(Session s)
        => $"CANCELLED after {Clock(s.Elapsed)} — " + (s.Options.Cache == CacheMode.Off
            ? "nothing was cached (cache=off), so starting it again reads everything again\n"
            : "the hashes it read are kept, so starting the same compare again only reads what this one didn't get to\n");

    /// <summary>The live progress of a compare still running in this process, else null.</summary>
    private static CompareProgress? Partial(CompareSession s) => s.IsDone ? null : s.Progress;

    /// <summary>Where the compare was saved (or why it wasn't) — only for a compare run in this process.</summary>
    internal static void Saved(StringBuilder o, Session s)
    {
        if (s.Reopened) return;
        if (s.SaveError is { } err) o.Append($"note: the result could not be saved ({err}); it lives only in this process\n");
        else if (s.ResultDir is { } dir) o.Append($"saved: {dir}  (reopen later by id {s.Id})\n");
    }

    /// <summary>
    /// What a compare left out or couldn't keep, beyond the warnings: links not followed, names Windows can't open by
    /// path, files that changed while read, a hash cache that couldn't be saved. Shared by every view (2-way, 3-way per
    /// side, HTML, CLI) so none of them leaves one out.
    /// </summary>
    public static IEnumerable<string> Notes(CompareReport r, string? side = null)
    {
        var pre = side is null ? "" : side + ": ";
        static string Some(IEnumerable<string> paths)
        {
            var list = paths.Take(4).ToList();
            return list.Count == 0 ? "" : " (" + string.Join(", ", list.Take(3)) + (list.Count > 3 ? ", …" : "") + ")";
        }
        var skipped = r.LeftSkipped.Select(x => (x, "left")).Concat(r.RightSkipped.Select(x => (x, "right"))).ToList();
        if (r.UnstableFiles > 0) yield return $"{pre}{r.UnstableFiles:N0} file(s) changed while being read (live writer) — compared as read, not cached.";
        if (r.SkippedLinks > 0)
            yield return $"{pre}{r.SkippedLinks:N0} symlink(s)/junction(s) not followed — nothing behind them is compared" +
                         Some(skipped.Where(t => t.x.Kind == Walk.SkipKind.Link).Select(t => $"{t.Item2} {t.x.Path}"));
        if (r.SkippedNames > 0)
            yield return $"{pre}{r.SkippedNames:N0} name(s) ending in '.' or ' ', or device names like \"nul\", skipped — Windows opens another file (or the device) by that path" +
                         Some(skipped.Where(t => t.x.Kind == Walk.SkipKind.Name).Select(t => $"{t.Item2} {t.x.Path}"));
        if (r.RenameLimit is { } rl) yield return $"{pre}{rl}.";
        if (r.CacheSaveError is { } e) yield return $"{pre}the hash cache could not be saved ({e}) — the result stands; the next compare reads those files again.";
    }

    private static void Warnings(StringBuilder o, CompareReport r)
    {
        if (r.LeftDroppedDirectories + r.RightDroppedDirectories > 0)
            o.Append($"WARNING: incomplete walk — {r.LeftDroppedDirectories} left / {r.RightDroppedDirectories} right director(ies) " +
                     "could not be listed; adds/removes under them may be listing failures.\n");
        foreach (var n in Notes(r)) o.Append("note: ").Append(n).Append('\n');
        if (r.UnreadableFiles > 0)
            o.Append($"WARNING: {r.UnreadableFiles} file(s) could not be read (locked, vanished or denied) — their verdict is unknown, " +
                     "listed as [unreadable] (a pair as modified, never identical); compare again once they can be read.\n");
    }

    private static string Reasons(CompareReport r) =>
        string.Join(" · ", Enum.GetValues<ChangeReason>().Select(x => (k: CanonicalTokens.Token(x), n: r.ReasonCount(x)))
            .Append((k: "unreadable", n: r.UnreadableFiles)).Where(t => t.n > 0)
            .Select(t => $"{t.k} {t.n:N0}"));

    private static bool TryStatuses(string? status, out HashSet<ChangeStatus> set, out string error)
    {
        error = "";
        var s = string.IsNullOrWhiteSpace(status) ? "changed" : status.Trim().ToLowerInvariant();
        if (s == "changed") { set = [ChangeStatus.Added, ChangeStatus.Removed, ChangeStatus.Modified, ChangeStatus.Renamed]; return true; }
        if (s == "all") { set = [.. Enum.GetValues<ChangeStatus>()]; return true; }
        set = [];
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = part == "deleted" ? "removed" : part;
            var match = Enum.GetValues<ChangeStatus>().Where(x => StatusToken(x) == token).ToList();
            if (match.Count == 0) { error = $"unknown status '{part}' (use changed, all, or added,removed,modified,renamed,identical)"; return false; }
            set.Add(match[0]);
        }
        return true;
    }

    private static bool TryReason(string? reason, out ChangeReason? value, out string error)
    {
        value = null;
        error = "";
        if (string.IsNullOrWhiteSpace(reason)) return true;
        var token = reason.Trim().ToLowerInvariant();
        foreach (var x in Enum.GetValues<ChangeReason>())
            if (CanonicalTokens.Token(x) == token) { value = x; return true; }
        error = $"unknown reason '{reason}' (use content, eol, whitespace, encoding, binary, metadata, unreadable)";
        return false;
    }

    /// <summary>
    /// Glob over '/'-separated relative paths, case-insensitive: <c>*</c> and <c>?</c> stay within a
    /// segment, <c>**</c> spans segments. A pattern without '/' matches the file name anywhere.
    /// </summary>
    internal static Func<string, bool> Glob(string pattern)
    {
        pattern = Normalize(pattern, trim: false); // a space in a glob is part of the name it matches
        bool nameOnly = !pattern.Contains('/');
        var rx = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char ch = pattern[i];
            if (ch == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                bool slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                rx.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (ch == '*') rx.Append("[^/]*");
            else if (ch == '?') rx.Append("[^/]");
            else rx.Append(Regex.Escape(ch.ToString()));
        }
        var re = new Regex(rx.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return nameOnly ? p => re.IsMatch(p[(p.LastIndexOf('/') + 1)..]) : p => re.IsMatch(p);
    }

    internal static string Normalize(string path, bool trim = true) => (trim ? path.Trim() : path).Replace('\\', '/').TrimStart('/');

    private static string StatusToken(ChangeStatus st) => st.ToString().ToLowerInvariant();

    private static string Tag(FileChange c) => c.Status switch
    {
        ChangeStatus.Added => "A",
        ChangeStatus.Removed => "D",
        ChangeStatus.Modified => "M",
        ChangeStatus.Renamed => "R",
        _ => "=",
    };

    private static string Name(FileChange c) => c.RenamedFrom is { } f ? $"{f} -> {c.RelativePath}" : c.RelativePath;

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{b / 1024.0:F1} KB",
        _ => $"{b} B",
    };

    private static string Delta(long d) => (d >= 0 ? "+" : "-") + Bytes(Math.Abs(d));
}
