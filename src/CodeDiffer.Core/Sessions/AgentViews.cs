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
        int pure = r.Changes.Count(c => c.Status == ChangeStatus.Renamed && c.SimilarityMilli == 1000);
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
            .Append(f.Change.Reason is { } rr ? $"  [{CanonicalTokens.Token(rr)}]" : "").Append('\n');
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

        if (!TryStatuses(status, out var statuses, out var err) || !TryReason(reason, out var reasonFilter, out err))
            return $"error: {err}\n";
        var glob = string.IsNullOrWhiteSpace(pathGlob) ? null : Glob(pathGlob);

        var match = changes.Where(c => statuses.Contains(c.Status)
            && (reasonFilter is null || c.Reason == reasonFilter)
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
            if (c.Reason is { } rr) o.Append($"  [{CanonicalTokens.Token(rr)}]");
            o.Append(c.Status switch
            {
                ChangeStatus.Added => $"  {Bytes(c.RightSize)}",
                ChangeStatus.Removed => $"  {Bytes(c.LeftSize)}",
                ChangeStatus.Renamed => $"  {(c.SimilarityMilli ?? 0) / 10}% similar",
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
        // While running, any difference found so far can be shown (diffs are rendered from the trees anyway).
        var found = Partial(s)?.Found();
        if (found is null && Pending(s) is { } pending) return $"compare {s.Id}: {pending}";
        IReadOnlyList<FileChange> changes = found is null ? s.Report!.Changes : [.. found.Select(f => f.Change)];
        path = Normalize(path);
        var c = changes.FirstOrDefault(c => c.RelativePath == path)
             ?? changes.FirstOrDefault(c => c.RenamedFrom == path)
             ?? changes.FirstOrDefault(c => string.Equals(c.RelativePath, path, StringComparison.OrdinalIgnoreCase));
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
        o.Append($"{Tag(c)} {Name(c)}{(c.Reason is { } rr ? $"  [{CanonicalTokens.Token(rr)}]" : "")}  ");
        o.Append(info.Kind == "text" ? $"+{info.AddedLines:N0} -{info.RemovedLines:N0} in {info.Hunks:N0} hunk(s)" : info.Kind);
        o.Append($" · {total:N0} patch line(s)\n");
        if (found is not null)
            o.Append("note: the compare is still running" + (mayBeRename ? "; this file may still pair into a rename" : "") + "\n");
        if (Stale(s, c) is { } stale) o.Append(stale).Append('\n');

        if (total <= maxLines && startLine == 1)
            return o.Append(section).ToString();

        // Capped: write the whole section once, then show the requested window plus the hunk map.
        var file = Path.Combine(OutDir(s), Safe(c.RelativePath) + (context == PatchOptions.DefaultContextLines ? "" : $".U{context}") + ".patch");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, section, new UTF8Encoding(false));
        int end = Math.Min(total, startLine + maxLines - 1);
        o.Append($"capped: showing lines {startLine:N0}-{end:N0} of {total:N0}; full patch: {file}\n");
        if (startLine == 1 && info.Hunks > 0)
        {
            var heads = all.Select((l, n) => (l, n)).Where(x => x.l.StartsWith("@@", StringComparison.Ordinal)).ToList();
            o.Append($"hunks ({heads.Count:N0}; line in patch → header):\n");
            foreach (var (l, n) in heads.Take(40)) o.Append($"  {n + 1,7:N0}  {l}\n");
            if (heads.Count > 40) o.Append($"  ... {heads.Count - 40:N0} more\n");
            o.Append("---\n");
        }
        for (int i = startLine - 1; i < end; i++) o.Append(all[i]).Append('\n');
        if (end < total) o.Append($"next: startLine={end + 1}\n");
        return o.ToString();
    }

    /// <summary>The whole A→B changeset as one git-style patch file (apply with `git apply`).</summary>
    public static string Export(CompareSession s, string? outPath = null, int context = PatchOptions.DefaultContextLines, bool literal = false)
    {
        if (Pending(s) is { } pending) return $"compare {s.Id}: {pending}";
        var r = s.Report!;
        var file = string.IsNullOrWhiteSpace(outPath) ? Path.Combine(OutDir(s), "changeset.patch") : Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        PatchStats ps;
        using (var w = new StreamWriter(file, false, new UTF8Encoding(false)) { NewLine = "\n" })
            ps = PatchWriter.Write(w, r, s.Left, s.Right, new PatchOptions { Context = context, Literal = literal, Parallelism = s.Options.Parallelism });
        return $"changeset {s.Id}: {file}  ({Bytes(new FileInfo(file).Length)})\n" +
               $"  {ps.TextFiles:N0} text · {ps.BinaryFiles:N0} binary · {ps.GiantFiles:N0} large (block ranges) · {ps.NoteFiles:N0} eol/encoding note(s)" +
               (ps.CoarseFiles > 0 ? $" · {ps.CoarseFiles:N0} coarse" : "") + "\n" +
               (literal ? "  literal: every changed byte is in the patch; `git apply` reproduces the right tree's text files exactly\n"
                        : "  eol/encoding-only files are notes, not hunks; pass literal=true for a fully applicable patch\n") +
               (ps.BinaryFiles + ps.GiantFiles > 0 ? "  binary and large files are described, not carried — copy those separately\n" : "");
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
        return PortText(r, $"compare {s.Id}", Path.Combine(OutDir(s), $"apply-{(write ? "written" : "dryrun")}-{DateTime.Now:HHmmss}.txt"), maxFiles);
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
        if (remember) s.DiffInfo[c.RelativePath] = info;
        return (text, info);
    }

    internal static FileDiffInfo Count(string section, PatchStats stats)
    {
        var kind = stats.BinaryFiles > 0 ? "binary" : stats.GiantFiles > 0 ? "large" : stats.NoteFiles > 0 ? "eol/encoding note" : "text";
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

    private static void Warnings(StringBuilder o, CompareReport r)
    {
        if (r.LeftDroppedDirectories + r.RightDroppedDirectories > 0)
            o.Append($"WARNING: incomplete walk — {r.LeftDroppedDirectories} left / {r.RightDroppedDirectories} right director(ies) " +
                     "could not be listed; adds/removes under them may be listing failures.\n");
        if (r.UnstableFiles > 0)
            o.Append($"note: {r.UnstableFiles} file(s) changed while being read (live writer) — compared as read.\n");
    }

    private static string Reasons(CompareReport r) =>
        string.Join(" · ", Enum.GetValues<ChangeReason>().Select(x => (x, n: r.ReasonCount(x))).Where(t => t.n > 0)
            .Select(t => $"{CanonicalTokens.Token(t.x)} {t.n:N0}"));

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
        error = $"unknown reason '{reason}' (use content, eol, whitespace, encoding, binary, metadata)";
        return false;
    }

    /// <summary>
    /// Glob over '/'-separated relative paths, case-insensitive: <c>*</c> and <c>?</c> stay within a
    /// segment, <c>**</c> spans segments. A pattern without '/' matches the file name anywhere.
    /// </summary>
    internal static Func<string, bool> Glob(string pattern)
    {
        pattern = Normalize(pattern);
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

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

    private static string Safe(string rel) => Regex.Replace(rel, @"[^A-Za-z0-9._-]", "_");

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
