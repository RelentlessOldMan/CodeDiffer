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
        if (Pending(s) is { } pending) return o.Append(pending).ToString();
        var r = s.Report!;
        int baseFiles = r.V1Report.Changes.Count(c => c.Status != ChangeStatus.Added);
        o.Append($"{baseFiles:N0} base files · {r.Entries.Count:N0} touched by either side   (finished in {Clock(s.Elapsed)})\n");
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
        if (r.Entries.Count > 0)
            o.Append("next: list_files(status=conflict) · get_file_diff(path) shows the merge with conflict markers\n");
        return o.ToString();
    }

    public static string Stats(Compare3Session s)
    {
        var text = Summary(s);
        if (s.Report is not { } r) return text;
        var o = new StringBuilder(text);
        o.Append("phases: ").Append(string.Join(" · ", r.Timings.Select(t => $"{t.Phase} {t.Elapsed.TotalSeconds:F1}s"))).Append('\n');
        o.Append($"read cost: base->v1 {Gb(r.V1Report.BytesRead)} ({r.V1Report.CacheHits:N0} cache hits) · base->v2 {Gb(r.V2Report.BytesRead)} ({r.V2Report.CacheHits:N0} cache hits)\n");
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
        if (e is null) return $"not touched by either side in compare3 {s.Id}: {path}\n";

        string body, kind;
        if (TreeMerger.MergedText(e, s.Base, s.V1, s.V2) is { } merged)
        {
            body = merged;
            kind = e.Outcome == Merge3Outcome.Conflict ? $"merged with {e.ConflictRegions} conflict marker block(s)" : "clean merge result";
        }
        else if (e.Outcome is Merge3Outcome.V1Only or Merge3Outcome.Agreed or Merge3Outcome.V2Only)
        {
            bool v2 = e.Outcome == Merge3Outcome.V2Only;
            var w = new StringWriter { NewLine = "\n" };
            PatchWriter.WriteChange(w, (v2 ? e.V2 : e.V1)!, s.Base, v2 ? s.V2 : s.V1, new PatchOptions(), new PatchStats());
            body = w.ToString();
            kind = e.Outcome == Merge3Outcome.Agreed ? "patch (identical on both sides)" : $"patch base->{(v2 ? "v2" : "v1")}";
        }
        else
        {
            var both = new StringWriter { NewLine = "\n" };
            both.Write("--- v1's change ---\n");
            PatchWriter.WriteChange(both, e.V1!, s.Base, s.V1, new PatchOptions(), new PatchStats());
            both.Write("--- v2's change ---\n");
            PatchWriter.WriteChange(both, e.V2!, s.Base, s.V2, new PatchOptions(), new PatchStats());
            body = both.ToString();
            kind = "both sides' patches (no line-level merge for this conflict)";
        }

        var lines = body.Split('\n');
        int total = lines.Length - (body.EndsWith('\n') ? 1 : 0);
        var o = new StringBuilder(Line(e)).Append($"\n{kind} · {total:N0} line(s)\n");
        maxLines = Math.Max(1, maxLines);
        startLine = Math.Clamp(startLine, 1, Math.Max(1, total));
        if (total <= maxLines && startLine == 1) return o.Append(body).ToString();

        var file = Path.Combine(AgentViews.OutDir(s), System.Text.RegularExpressions.Regex.Replace(e.Path, @"[^A-Za-z0-9._-]", "_") + ".merge.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, body, new UTF8Encoding(false));
        int end = Math.Min(total, startLine + maxLines - 1);
        o.Append($"capped: showing lines {startLine:N0}-{end:N0} of {total:N0}; whole file: {file}\n");
        if (startLine == 1)
        {
            var marks = lines.Select((l, n) => (l, n)).Where(x => x.l.StartsWith("<<<<<<< ", StringComparison.Ordinal)).ToList();
            if (marks.Count > 0)
            {
                o.Append($"conflict blocks start at line: {string.Join(", ", marks.Take(40).Select(m => (m.n + 1).ToString("N0")))}{(marks.Count > 40 ? ", ..." : "")}\n---\n");
            }
        }
        for (int i = startLine - 1; i < end; i++) o.Append(lines[i]).Append('\n');
        if (end < total) o.Append($"next: startLine={end + 1}\n");
        return o.ToString();
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
            _ => c.Reason is { } rr ? CanonicalTokens.Token(rr) : "modified",
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

    private static string? Pending(Session s)
    {
        if (s.Error is { } e) return $"FAILED after {Clock(s.Elapsed)}: {e}\n";
        if (!s.IsDone) return $"running · {Clock(s.Elapsed)} elapsed — call get_summary(wait_seconds=…) to wait for it\n";
        return null;
    }

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static string Gb(long b) => AgentViews.Bytes(b);
}
