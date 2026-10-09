using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.ThreeWay;

namespace CodeDiffer.Core.Sessions;

/// <summary>
/// Agent views of a 3-way compare, bounded like the 2-way ones: a constant-size summary, a paged list
/// of touched paths with their merge outcome, and one file's merge (diff3 conflict markers) or one side's
/// patch, capped with the overflow written to a file.
/// </summary>
public static class ThreeWayViews
{
    public static string Summary(Compare3Session s)
    {
        var o = new StringBuilder();
        o.Append($"compare3 {s.Id} · {s.Title}\n");
        if (!s.IsDone && s.Progress1 is { } p1 && s.Progress2 is { } p2) return Running(o, s, p1, p2);
        if (Pending(s) is { } pending) return o.Append(pending).ToString();
        var r = s.Report!;
        int baseFiles = r.V1Report.Changes.Count(c => c.Status != ChangeStatus.Added);
        o.Append($"{baseFiles:N0} base files · {r.Entries.Count:N0} touched by either side   (finished in {Clock(s.Elapsed)})\n");
        o.Append(AgentViews.ReopenedNote(s));
        o.Append($"  v1 only   {r.Count(Merge3Outcome.V1Only),8:N0}    take v1\n");
        o.Append($"  v2 only   {r.Count(Merge3Outcome.V2Only),8:N0}    take v2\n");
        o.Append($"  agreed    {r.Count(Merge3Outcome.Agreed),8:N0}    both made the identical change\n");
        o.Append($"  merged    {r.Count(Merge3Outcome.Merged),8:N0}    both changed it, in separate regions: merges cleanly\n");
        o.Append($"  CONFLICT  {r.Count(Merge3Outcome.Conflict),8:N0}");
        var kinds = r.Entries.Where(e => e.Outcome == Merge3Outcome.Conflict).GroupBy(e => e.ConflictKind ?? "?")
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count():N0}").ToList();
        if (kinds.Count > 0) o.Append("    ").Append(string.Join(" · ", kinds));
        o.Append('\n');
        int regions = r.Entries.Sum(e => e.ConflictRegions);
        if (regions > 0) o.Append($"conflict regions: {regions:N0} (in content/add-add conflicts)\n");
        o.Append($"per side: v1 {SideCounts(r.V1Report)} · v2 {SideCounts(r.V2Report)}\n");
        if (r.DroppedDirectories > 0)
            o.Append($"WARNING: incomplete walk — {r.DroppedDirectories} director(ies) could not be listed; the verdicts under them may be wrong.\n");
        if (r.UnreadableFiles > 0)
            o.Append($"WARNING: {r.UnreadableFiles} file(s) could not be read (locked, vanished or denied) — their verdict is unknown " +
                     "(shown as 'unreadable'); compare again once they can be read.\n");
        foreach (var n in AgentViews.Notes(r.V1Report, "base->v1").Concat(AgentViews.Notes(r.V2Report, "base->v2")))
            o.Append("note: ").Append(n).Append('\n');
        AgentViews.Saved(o, s);
        if (r.Entries.Count > 0)
            o.Append("next: list_files(status=conflict) · get_file_diff(path) shows the merge with conflict markers · write_report for HTML\n");
        return o.ToString();
    }

    /// <summary>What a merge overlay holds and how to apply it (the CLI's --merge-out and the write_merge tool).</summary>
    public static string OverlayText(OverlayResult o)
        => $"merge overlay: {o.Dir}\n" +
           $"  files\\       {o.FromV2:N0} from v2 · {o.Merged:N0} merged · {o.Markers:N0} with conflict markers · {o.Moved:N0} moved ({AgentViews.Bytes(o.Bytes)})\n" +
           $"  deletes.txt  {o.Deletes:N0} path(s)\n" +
           $"  conflicts.txt {o.Markers:N0} with markers · {o.Unresolved - o.Failed:N0} not merged (v1's version stays until decided)\n" +
           (o.Failed > 0 ? $"  FAILED: {o.Failed:N0} file(s) could not be written (see conflicts.txt; v1's version stays)\n" : "") +
           (o.DroppedDirectories > 0 ? $"  INCOMPLETE: {o.DroppedDirectories:N0} director(ies) could not be read; changes under them are missing\n" : "") +
           "  apply: delete the paths in deletes.txt from v1, remove the directories that left empty, then copy files\\\n" +
           "         over v1 (OVERLAY.txt says the same),\n" +
           $"         or: codediffer apply-overlay \"{o.Dir}\" <v1 or a copy of it> [--write]\n";

    public static string Stats(Compare3Session s)
    {
        var text = Summary(s);
        if (s.Report is not { } r) return text;
        var o = new StringBuilder(text);
        o.Append("phases: ").Append(string.Join(" · ", r.Timings.Select(t => $"{t.Phase} {t.Elapsed.TotalSeconds:F1}s"))).Append('\n');
        o.Append($"read cost: base->v1 {Gb(r.V1Report.BytesRead)} ({r.V1Report.CacheHits:N0} cache hits) · base->v2 {Gb(r.V2Report.BytesRead)} ({r.V2Report.CacheHits:N0} cache hits" +
                 (r.V2Report.ReusedLeftFiles > 0 ? $", {r.V2Report.ReusedLeftFiles:N0} base files reused from base->v1" : "") + ")\n");
        var worst = r.Entries.Where(e => e.ConflictRegions > 0).OrderByDescending(e => e.ConflictRegions).ThenBy(e => e.Path, StringComparer.Ordinal).Take(10).ToList();
        if (worst.Count > 0)
        {
            o.Append("most conflict regions:\n");
            foreach (var e in worst) o.Append($"  {e.ConflictRegions,6:N0}  {e.Path}\n");
        }
        return o.ToString();
    }

    /// <param name="status">changed (default: every touched path), both (agreed+merged+conflict), or a
    /// comma list of conflict, merged, agreed, v1, v2.</param>
    public static string ListFiles(Compare3Session s, string? status = null, string? pathGlob = null,
        int page = 1, int pageSize = AgentViews.DefaultPageSize)
    {
        if (Pending(s) is { } pending) return $"compare3 {s.Id}: {pending}";
        var r = s.Report!;
        if (!TryOutcomes(status, out var want, out var err)) return $"error: {err}\n";
        var glob = string.IsNullOrWhiteSpace(pathGlob) ? null : AgentViews.Glob(pathGlob);
        var match = r.Entries.Where(e => want.Contains(e.Outcome)
            && (glob is null || glob(e.Path) || (e.MergedPath is { } m && glob(m)))).ToList();

        pageSize = Math.Clamp(pageSize, 1, AgentViews.MaxPageSize);
        int pages = Math.Max(1, (match.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, pages);
        var o = new StringBuilder($"compare3 {s.Id} · {match.Count:N0} path(s) match · page {page}/{pages}\n");
        foreach (var e in match.Skip((page - 1) * pageSize).Take(pageSize)) o.Append(Line(e)).Append('\n');
        if (page < pages) o.Append($"next page: page={page + 1}\n");
        return o.ToString();
    }

    /// <summary>
    /// One path: for a both-sides text change, the merged file with diff3 conflict markers; for a one-sided
    /// (or agreed) change, that side's patch. Capped at <paramref name="maxLines"/>; the whole goes to a file.
    /// </summary>
    public static string FileDiff(Compare3Session s, string path, int maxLines = AgentViews.DefaultMaxLines, int startLine = 1)
    {
        if (Pending(s) is { } pending) return $"compare3 {s.Id}: {pending}";
        var r = s.Report!;
        path = path.Trim().Replace('\\', '/').TrimStart('/');
        var e = r.Entries.FirstOrDefault(x => x.Path == path)
             ?? r.Entries.FirstOrDefault(x => x.MergedPath == path || x.V1?.RelativePath == path || x.V2?.RelativePath == path)
             ?? r.Entries.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (e is null && path.Contains('�'))
        {
            // As UTF-8 shows it: an unpaired surrogate reads as U+FFFD in a listing.
            var hits = r.Entries.Where(x => ResultStore.Shown(x.Path) == path || (x.MergedPath is { } m && ResultStore.Shown(m) == path)).Take(2).ToList();
            if (hits.Count == 1) e = hits[0];
        }
        if (e is null) return $"not touched by either side in compare3 {s.Id}: {path}\n";

        var body = Body(s, e, out var kind, out _);
        var lines = body.Split('\n');
        int total = lines.Length - (body.EndsWith('\n') ? 1 : 0);
        var o = new StringBuilder(Line(e)).Append($"\n{kind} · {total:N0} line(s)\n");
        if (Stale(s, e) is { } stale) o.Append(stale).Append('\n');
        maxLines = Math.Max(1, maxLines);
        startLine = Math.Clamp(startLine, 1, Math.Max(1, total));
        if (startLine == 1 && AgentViews.Fits(lines, total, maxLines)) return o.Append(body).ToString();

        var file = AgentViews.OutFile(s, "merges", e.Path, ".merge.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, body, new UTF8Encoding(false));
        int end = AgentViews.WindowEnd(lines, total, startLine, maxLines);
        o.Append($"capped: showing lines {startLine:N0}-{end:N0} of {total:N0}{AgentViews.CutNote(lines, startLine, end, maxLines, total)}; whole file: {file}\n");
        if (startLine == 1)
        {
            var marks = lines.Select((l, n) => (l, n)).Where(x => x.l.StartsWith("<<<<<<< ", StringComparison.Ordinal)).ToList();
            if (marks.Count > 0)
            {
                o.Append($"conflict blocks start at line: {string.Join(", ", marks.Take(40).Select(m => (m.n + 1).ToString("N0")))}{(marks.Count > 40 ? ", ..." : "")}\n---\n");
            }
        }
        AgentViews.AppendWindow(o, lines, startLine, end);
        if (end < total) o.Append($"next: start_line={end + 1}\n");
        return o.ToString();
    }

    /// <summary>
    /// The full text shown for one entry: for a both-sides text change the merged file with diff3 markers
    /// (<paramref name="merge"/> = true); for a one-sided or agreed change that side's patch; otherwise both
    /// sides' patches. <paramref name="kind"/> describes which.
    /// </summary>
    public static string Body(Compare3Session s, Merge3Entry e, out string kind, out bool merge, PatchOptions? options = null)
    {
        var opt = options ?? new PatchOptions();
        merge = false;
        if (TreeMerger.MergedText(e, s.Base, s.V1, s.V2) is { } merged)
        {
            merge = true;
            kind = e.Outcome == Merge3Outcome.Conflict ? $"merged with {e.ConflictRegions} conflict marker block(s)" : "clean merge result";
            return merged;
        }
        if (e.Outcome is Merge3Outcome.V1Only or Merge3Outcome.Agreed or Merge3Outcome.V2Only)
        {
            bool v2 = e.Outcome == Merge3Outcome.V2Only;
            var w = new StringWriter { NewLine = "\n" };
            PatchWriter.WriteChange(w, (v2 ? e.V2 : e.V1)!, s.Base, v2 ? s.V2 : s.V1, opt, new PatchStats());
            kind = e.Outcome == Merge3Outcome.Agreed ? "patch (identical on both sides)" : $"patch base->{(v2 ? "v2" : "v1")}";
            return w.ToString();
        }
        // A conflict can have one side only (a path collision, a file/directory clash, a side behind a link or unreadable).
        var both = new StringWriter { NewLine = "\n" };
        both.Write("--- v1's change ---\n");
        if (e.V1 is { } c1) PatchWriter.WriteChange(both, c1, s.Base, s.V1, opt, new PatchStats());
        else both.Write("(no change of v1's at this path)\n");
        both.Write("--- v2's change ---\n");
        if (e.V2 is { } c2) PatchWriter.WriteChange(both, c2, s.Base, s.V2, opt, new PatchStats());
        else both.Write("(no change of v2's at this path)\n");
        kind = "both sides' patches (no line-level merge for this conflict)";
        return both.ToString();
    }

    /// <summary>A warning when any of the entry's files no longer has the size the compare recorded.</summary>
    internal static string? Stale(Compare3Session s, Merge3Entry e)
    {
        string? Side(FileChange? c, string root, string name)
            => c is null || c.Status == ChangeStatus.Removed ? null : AgentViews.StaleSide(AgentViews.Full(root, c.RelativePath), c.RightSize, name);
        var b = e.V1 ?? e.V2;
        var baseSide = b is { Status: not ChangeStatus.Added } ? AgentViews.StaleSide(AgentViews.Full(s.Base, e.Path), b.LeftSize, "base") : null;
        return AgentViews.StaleNote(baseSide, Side(e.V1, s.V1, "v1"), Side(e.V2, s.V2, "v2"));
    }

    /// <summary>One entry as a single line: outcome tag, base path, what each side did, conflict kind.</summary>
    public static string Line(Merge3Entry e)
    {
        string tag = e.Outcome switch
        {
            Merge3Outcome.V1Only => "v1",
            Merge3Outcome.V2Only => "v2",
            Merge3Outcome.Agreed => "AG",
            Merge3Outcome.Merged => "MG",
            _ => "CF",
        };
        string Side(FileChange? c) => c is null ? "-" : c.Status switch
        {
            ChangeStatus.Added => "added",
            ChangeStatus.Removed => "deleted",
            ChangeStatus.Renamed => $"renamed->{c.RelativePath}",
            _ => c.ReasonLabel ?? "modified",
        };
        var o = new StringBuilder($"{tag} {e.Path}  [v1 {Side(e.V1)} · v2 {Side(e.V2)}]");
        if (e.Outcome == Merge3Outcome.Conflict) o.Append($"  {e.ConflictKind}");
        if (e.ConflictRegions > 0 || e.Outcome == Merge3Outcome.Merged) o.Append($"  ({e.ConflictRegions} conflict / {e.CleanRegions} clean region(s))");
        if (e.MergedPath is { } m && m != e.Path) o.Append($"  -> {m}");
        if (e.Note is not null) o.Append($"  — {e.Note}");
        return o.ToString();
    }

    private static bool TryOutcomes(string? status, out HashSet<Merge3Outcome> set, out string error)
    {
        error = "";
        var s = string.IsNullOrWhiteSpace(status) ? "changed" : status.Trim().ToLowerInvariant();
        set = s switch
        {
            "changed" or "all" => [.. Enum.GetValues<Merge3Outcome>()],
            "both" => [Merge3Outcome.Agreed, Merge3Outcome.Merged, Merge3Outcome.Conflict],
            _ => [],
        };
        if (set.Count > 0) return true;
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Merge3Outcome? o = part switch
            {
                "conflict" or "conflicts" => Merge3Outcome.Conflict,
                "merged" => Merge3Outcome.Merged,
                "agreed" => Merge3Outcome.Agreed,
                "v1" or "v1only" => Merge3Outcome.V1Only,
                "v2" or "v2only" => Merge3Outcome.V2Only,
                _ => null,
            };
            if (o is null) { error = $"unknown status '{part}' (use changed, both, or conflict,merged,agreed,v1,v2)"; return false; }
            set.Add(o.Value);
        }
        return true;
    }

    private static string SideCounts(CompareReport r) =>
        $"{r.Count(ChangeStatus.Modified):N0} modified, {r.Count(ChangeStatus.Added):N0} added, {r.Count(ChangeStatus.Removed):N0} deleted, {r.Count(ChangeStatus.Renamed):N0} renamed";

    /// <summary>
    /// A running 3-way compare: where each of its two compares is, and what is known so far — above all the
    /// paths both sides changed, since those are the ones that will merge or conflict.
    /// </summary>
    private static string Running(StringBuilder o, Compare3Session s, CompareProgress p1, CompareProgress p2)
    {
        o.Append($"running · {Clock(s.Elapsed)} elapsed\n");
        o.Append("  base->v1: ").Append(p1.Phase == ComparePhase.Done ? $"done · {p1.FoundCount:N0} changed" : ProgressView.Line(p1)).Append('\n');
        o.Append("  base->v2: ").Append(p2.Phase == ComparePhase.Done ? $"done · {p2.FoundCount:N0} changed"
            : p2.Phase == ComparePhase.Starting ? "waits for base->v1 (it reuses the base hashes)" : ProgressView.Line(p2)).Append('\n');
        if (p2.Phase == ComparePhase.Done) o.Append("  now: classifying and merging the paths both sides touched\n");

        var v1 = p1.Found();
        var v2 = p2.Found();
        if (v2.Length > 0)
        {
            var v1Paths = v1.Select(c => c.Change.RelativePath).ToHashSet(StringComparer.Ordinal);
            var both = v2.Select(c => c.Change.RelativePath).Where(v1Paths.Contains).ToList();
            o.Append($"so far: v1 changed {v1.Length:N0}{(p1.Phase == ComparePhase.Done ? " (complete)" : "")} · v2 changed {v2.Length:N0} · " +
                     $"changed on both sides {both.Count:N0} (each will merge cleanly or conflict)\n");
            foreach (var path in both.Take(10)) o.Append("  both: ").Append(path).Append('\n');
            if (both.Count > 10) o.Append($"  ... {both.Count - 10:N0} more\n");
        }
        else if (v1.Length > 0)
            o.Append($"so far: v1 changed {v1.Length:N0}{(p1.Phase == ComparePhase.Done ? " (complete)" : "")}\n");
        o.Append("partial: renames are matched at the end of each compare, so an add/delete pair may still become one rename\n");
        o.Append("next: get_summary(wait_seconds=…) waits for the rest; list_files and get_file_diff work once it finishes\n");
        return o.ToString();
    }

    private static string? Pending(Session s)
    {
        if (s.Cancelled) return AgentViews.CancelledText(s);
        if (s.Error is { } e) return $"FAILED after {Clock(s.Elapsed)}: {e}\n";
        if (!s.IsDone) return $"running · {Clock(s.Elapsed)} elapsed — get_summary shows progress and what is found so far; get_summary(wait_seconds=…) waits for it\n";
        return null;
    }

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static string Gb(long b) => AgentViews.Bytes(b);
}
