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
    public static string OutDir(Session s)
    {
        ResultStore.Restore(s); // pruned by another process: saved again before anything is written into it
        return s.ResultDir ?? Path.Combine(
            Environment.GetEnvironmentVariable("CODEDIFFER_OUT_DIR") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "codediffer"),
            s.Id);
    }

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
        if (r.ReasonCount(ChangeReason.Content) > 0)
            o.Append($"  noise     {r.Changes.Count(c => c.Noise != ChangeNoise.None),8:N0}    {NoiseFilter.Line(r.Changes)}   " +
                     "(content changes that are only that; list_files hide_noise=all leaves them out, and whitespace/eol/encoding)\n");
        o.Append($"  added     {r.Count(ChangeStatus.Added),8:N0}\n");
        o.Append($"  removed   {r.Count(ChangeStatus.Removed),8:N0}\n");
        int renamed = r.Count(ChangeStatus.Renamed);
        int pure = r.Changes.Count(c => c.PureRename);
        o.Append($"  renamed   {renamed,8:N0}    ({pure:N0} pure, {renamed - pure:N0} rename+edit)\n");

        var changed = r.Changes.Where(c => c.Status != ChangeStatus.Identical).ToList();
        if (changed.Count > 0) o.Append($"changed by file type: {TypeLine(changed.Select(c => c.Type))}   (list_files type=code)\n");
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
                o.Append($"  {i.Lines()}  {path}\n");
        }

        o.Append($"read cost: {r.ComparedPairs:N0} same-size pair(s) · {r.CacheHits:N0} side(s) from hash cache" +
                 (r.CodeCompassHits > 0 ? $" ({r.CodeCompassHits:N0} via CodeCompass)" : "") +
                 (r.VerdictHits > 0 ? $" · {r.VerdictHits:N0} modified pair(s) classified before" : "") +
                 (r.PendingFiles > 0 ? $" · {r.PendingFiles:N0} too recently modified to cache yet" : "") +
                 $" · {Bytes(r.BytesRead)} read · {Clock(s.Elapsed)} · threads {s.Options.Parallelism} · cache {s.Options.CacheLabel}\n");
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
    /// <param name="type">A comma list of file types (<see cref="FileTypes"/>): code, build, docs, binary.</param>
    /// <param name="hideNoise">The noise to leave out (<see cref="NoiseFilter.TryParse"/>): "all", or a comma list.</param>
    public static string ListFiles(CompareSession s, string? status = null, string? pathGlob = null, string? reason = null,
        int page = 1, int pageSize = DefaultPageSize, bool lines = false, string? type = null, string? hideNoise = null)
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
        Func<string, string?, bool>? glob = null;
        if (!string.IsNullOrWhiteSpace(pathGlob) && !TryPathFilter(pathGlob, out glob, out err)) return $"error: {err}\n";
        HashSet<FileType>? types = null;
        if (!TryTypes(type, out types, out err)) return $"error: {err}\n";
        if (!NoiseFilter.TryParse(hideNoise, out var noise, out err)) return $"error: {err}\n";

        var match = changes.Where(c => statuses.Contains(c.Status)
            && (unreadableOnly ? c.ReasonLabel == "unreadable" : reasonFilter is null || c.Reason == reasonFilter)
            && (types is null || types.Contains(c.Type))
            && (noise is null || !noise.Hides(c))
            && (glob is null || glob(c.RelativePath, c.RenamedFrom))).ToList();

        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        int pages = Math.Max(1, (match.Count + pageSize - 1) / pageSize);
        // A page past the last is empty and says so: shown the last page again, a caller paging on never saw the end.
        bool past = page > pages;
        page = Math.Max(1, page);
        var slice = past ? [] : match.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        // A file that may still become part of a rename isn't rendered yet: its final diff may differ.
        if (lines) Render(s, maybeRename is null ? slice : slice.Where(c => !maybeRename.Contains(c.RelativePath)).ToList());

        if (progress is not null)
            o.Append($"compare {s.Id} · RUNNING, partial: {found!.Length:N0} difference(s) found so far, in the order found · {ProgressView.Line(progress, found: false)}\n");
        o.Append($"compare {s.Id} · {match.Count:N0} file(s) match{(progress is null ? "" : " so far")} · page {page}/{pages}" +
                 (past ? $" — past the last page{(progress is null ? "" : " so far")} (page={pages} is the last)" : "") + "\n");
        foreach (var c in slice)
        {
            o.Append($"{Tag(c)} {Name(c)}");
            if (c.ReasonLabel is { } rr) o.Append(c.NoiseLabel is { } nl ? $"  [{rr}, noise: {nl}]" : $"  [{rr}]");
            o.Append(c.Status switch
            {
                ChangeStatus.Added => $"  {Bytes(c.RightSize)}",
                ChangeStatus.Removed => $"  {Bytes(c.LeftSize)}",
                ChangeStatus.Renamed => $"  {(c.SimilarityMilli ?? 0) / 10}% similar" + (c.EditedRename && c.SimilarityMilli >= 995 ? " (but edited)" : ""),
                _ => c.LeftSize == c.RightSize ? $"  {Bytes(c.RightSize)}" : $"  {Bytes(c.LeftSize)} -> {Bytes(c.RightSize)}",
            });
            if (s.DiffInfo.TryGetValue(c.RelativePath, out var i))
                o.Append(i.Kind == "text" ? $"  +{i.AddedLines:N0} -{i.RemovedLines:N0} ({i.Hunks:N0} hunk(s){(i.Coarse ? ", " + FileDiffInfo.CoarseNote : "")})" : $"  ({i.Kind})");
            if (maybeRename?.Contains(c.RelativePath) == true) o.Append("  (may still pair into a rename)");
            if (c.RenameHeld is { } held) o.Append($"  ({held})");
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
        int maxLines = DefaultMaxLines, int startLine = 1, CancellationToken ct = default)
    {
        context = Math.Clamp(context, 0, 1000); // what is rendered, and so what the saved file's name says
        // While running, any difference found so far can be shown (diffs are rendered from the trees anyway).
        var found = Partial(s)?.Found();
        if (found is null && Pending(s) is { } pending) return $"compare {s.Id}: {pending}";
        IReadOnlyList<FileChange> changes = found is null ? s.Report!.Changes : [.. found.Select(f => f.Change)];
        FileChange? Find(string p) => changes.FirstOrDefault(c => c.RelativePath == p)
             ?? changes.FirstOrDefault(c => c.RenamedFrom == p)
             ?? ByCase(changes, c => c.RelativePath, p);
        // The path as given first: " a.txt" is a name of its own, not a.txt with a stray space.
        var given = path;
        var c = Find(path = Normalize(given, trim: false));
        if (c is null && Normalize(given) is var trimmed && trimmed != path) c = Find(path = trimmed);
        if (c is null && ResultStore.Unescaped(given) is var raw && raw != given) c = Find(Normalize(raw, trim: false));
        if (c is null)
        {
            var shown = ByShownName(changes, x => [x.RelativePath, x.RenamedFrom], path);
            if (shown.Count > 1) return Ambiguous(path, shown.Select(x => x.RelativePath));
            c = shown.FirstOrDefault();
        }
        if (c is null && CaseAmbiguous(changes.Select(x => x.RelativePath), path) is { } cased) return cased;
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
        var (section, info) = RenderOne(s, c, context, remember: !mayBeRename, ct: ct);
        var all = section.Split('\n');
        int total = all.Length - (section.EndsWith('\n') ? 1 : 0);
        maxLines = Math.Max(1, maxLines);
        startLine = Math.Clamp(startLine, 1, Math.Max(1, total));

        var o = new StringBuilder();
        o.Append($"{Tag(c)} {Name(c)}{(c.ReasonLabel is { } rr ? $"  [{rr}]" : "")}  ");
        o.Append(info.Kind == "text" ? info.Lines() : info.Kind);
        o.Append($" · {total:N0} patch line(s)\n");
        if (found is not null)
            o.Append("note: the compare is still running" + (mayBeRename ? "; this file may still pair into a rename" : "") + "\n");
        if (Stale(s, c) is { } stale) o.Append(stale).Append('\n');

        if (startLine == 1 && Fits(all, total, maxLines))
            return o.Append(section).ToString();

        // Capped: write the whole section once, then show the requested window plus the hunk map. Another context is a
        // sibling directory, never a suffix: x at context 5 and a file named x.U5 would share one name.
        string file;
        using (ResultStore.Hold(s))
        {
            file = OutFile(s, context == PatchOptions.DefaultContextLines ? "diffs" : $"diffs-U{context}", c.RelativePath, ".patch",
                changes.Select(x => x.RelativePath));
            WriteWhole(file, section);
        }
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
    /// <paramref name="paths"/> are the compare's other paths: one that differs from <paramref name="rel"/> only in case (a
    /// case-sensitive tree's Foo.c and foo.c) is one name here, so each of them gets its own.
    /// </summary>
    internal static string OutFile(Session s, string sub, string rel, string suffix, IEnumerable<string> paths)
    {
        var root = Path.Combine(OutDir(s), sub);
        var mirrored = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)) + suffix;
        // The path mirrored, unless the tree has a file where it needs a directory or the reverse (a file a and a
        // directory a.patch/), or another path but for case: then one flat name from the path's hash, never another file's.
        bool clash = Directory.Exists(mirrored)
                     || paths.Any(p => !string.Equals(p, rel, StringComparison.Ordinal) && string.Equals(p, rel, StringComparison.OrdinalIgnoreCase));
        for (var dir = Path.GetDirectoryName(mirrored); !clash && dir is not null && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
            clash = File.Exists(dir);
        // A name with the suffix too long to be one (a tree's name can already be 255 characters): the same.
        clash |= !Walk.TempNames.Fits(Path.GetFileName(mirrored));
        if (!clash) return mirrored;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rel)))[..16].ToLowerInvariant();
        return Path.Combine(root, "_clash", $"{Walk.TempNames.Fit(Path.GetFileName(rel), hash.Length + 1 + suffix.Length)}.{hash}{suffix}");
    }

    /// <summary>
    /// A capped view's whole text written to <paramref name="file"/>: beside it and moved into place, so a reader never
    /// sees it half written and two answers paging one file at the same moment never collide (one wrote it unshared while
    /// the other opened it: "the process cannot access the file"). A move held off by a reader is retried; one still held
    /// off keeps the file there, which is the same view's text.
    /// </summary>
    internal static void WriteWhole(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = Walk.TempNames.Unique(file);
        try { File.WriteAllText(tmp, text, new UTF8Encoding(false)); }
        catch
        {
            // Written part way (a full disk): not left beside the file.
            try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            throw;
        }
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, file, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 20 && !Directory.Exists(file)) { Thread.Sleep(25); continue; }
                try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
                if (File.Exists(file)) return;
                throw;
            }
        }
    }

    /// <summary>The one entry whose path is <paramref name="path"/> but for case, or null: none, or several (a case-sensitive
    /// tree's Foo.c and foo.c), which <see cref="CaseAmbiguous"/> answers.</summary>
    internal static T? ByCase<T>(IEnumerable<T> items, Func<T, string?> key, string path) where T : class
    {
        T? one = null;
        foreach (var x in items)
            if (string.Equals(key(x), path, StringComparison.OrdinalIgnoreCase))
            {
                if (one is not null) return null;
                one = x;
            }
        return one;
    }

    /// <summary>The answer when several paths are <paramref name="path"/> but for case and none is it exactly, or null.</summary>
    internal static string? CaseAmbiguous(IEnumerable<string> paths, string path)
    {
        var cased = paths.Where(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).Take(10).ToList();
        return cased.Count > 1
            ? $"{path} differs only in case from several files (the tree tells them apart by case); ask for one by its exact name:\n" +
              string.Concat(cased.Select(p => "  " + ResultStore.Escaped(p) + "\n"))
            : null;
    }

    /// <summary>The entries one of whose paths, as UTF-8 shows it (an unpaired surrogate as U+FFFD), is this: one is the
    /// file asked for; more show alike (up to 10 are returned).</summary>
    internal static List<T> ByShownName<T>(IEnumerable<T> items, Func<T, string?[]> paths, string path)
        => path.Contains('\uFFFD')
            ? items.Where(x => paths(x).Any(p => p is not null && ResultStore.Shown(p) == path)).Take(10).ToList()
            : [];

    /// <summary>The answer when several files show as <paramref name="path"/>: their escaped names, which tell them apart.</summary>
    internal static string Ambiguous(string path, IEnumerable<string> paths)
        => $"{path} is how several files show (each name holds an unpaired surrogate, shown as U+FFFD); ask for one by " +
           "its escaped name:\n" + string.Concat(paths.Select(p => "  " + ResultStore.Escaped(p) + "\n"));

    /// <summary>The whole A→B changeset as one git-style patch file (apply with `git apply`).</summary>
    public static string Export(CompareSession s, string? outPath = null, int context = PatchOptions.DefaultContextLines, bool literal = false)
    {
        if (context < 0) return $"error: context must be 0 or more (got {context})\n";
        if (Pending(s) is { } pending) return $"compare {s.Id}: {pending}";
        if (FastStat(s) is { } fast) return fast;
        var r = s.Report!;
        if (r.LeftDroppedDirectories + r.RightDroppedDirectories > 0)
            return $"error: compare {s.Id} is incomplete ({r.LeftDroppedDirectories} left / {r.RightDroppedDirectories} right director(ies) " +
                   "could not be listed): its adds and removes there may be listing failures, which a patch would carry out as deletes — " +
                   "no patch written; compare again";
        using var held = ResultStore.Hold(s); // no prune takes the result away under the patch
        string file;
        try { file = string.IsNullOrWhiteSpace(outPath) ? Path.Combine(OutDir(s), "changeset.patch") : Path.GetFullPath(outPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"error: out_path {outPath}: {ex.Message}\n";
        }
        // A device (NUL, CON: \\.\NUL), a drive or share root, or a directory: no file to write the patch to.
        if (file.StartsWith(@"\\.\", StringComparison.Ordinal) || Path.GetFileName(file).Length == 0 || Directory.Exists(file))
            return $"error: out_path {outPath} is not a file path (a device, a root or a directory); give a file to write the patch to\n";
        foreach (var tree in new[] { s.Left, s.Right })
            if (ResultStore.Overlaps(file, tree, oneWay: true))
                return $"error: {file} is inside the compared tree {tree} — the patch would become part of what it describes; write it somewhere else\n";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        PatchStats ps;
        long size;
        // Its own name beside the patch's (two exports at once each write their own) and moved into place: a failure
        // never leaves a truncated patch behind. A move held off by a reader of the last patch is retried.
        var tmp = Walk.TempNames.Unique(file);
        try
        {
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)) { NewLine = "\n" })
                ps = PatchWriter.Write(w, r, s.Left, s.Right, new PatchOptions { Context = context, Literal = literal, Parallelism = s.Options.Parallelism });
            size = new FileInfo(tmp).Length; // this patch's, whichever export moves last
            for (int attempt = 1; ; attempt++)
                try
                {
                    File.Move(tmp, file, overwrite: true);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 40 && !Directory.Exists(file))
                {
                    Thread.Sleep(25);
                }
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            throw;
        }
        // A reopened compare's patch is of the trees as they are now, under the saved verdicts: say so, as write_merge does.
        return ReopenedNote(s) + $"changeset {s.Id}: {file}  ({Bytes(size)})\n" +
               $"  {ps.Summary(literal)}\n" +
               "  `git apply` it: what it doesn't carry is described in '#' lines, which git skips\n";
    }

    /// <summary>
    /// Port the compare's left→right changes onto <paramref name="target"/> (dry run unless
    /// <paramref name="write"/>): totals, then the conflicted and fuzzy files (bounded), with the full
    /// per-hunk report written to a file.
    /// </summary>
    /// <param name="ct">Stops it between files (a file is written whole or not at all): the files not reached are counted.</param>
    public static string Apply(CompareSession s, string target, bool write, int maxFiles = 50, CancellationToken ct = default,
        bool resolveStamps = false)
    {
        if (Pending(s) is { } pending) return $"compare {s.Id}: {pending}";
        if (FastStat(s) is { } fast) return fast;
        var r = ChangePorter.Run(s.Report!, s.Left, s.Right, target, write, parallelism: s.Options.Parallelism, ct: ct,
            resolveStamps: resolveStamps);
        using (ResultStore.Hold(s)) // no prune takes the result away under the report
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
        var stamped = r.Files.Where(f => f.Note?.StartsWith(ChangePorter.StampsResolved, StringComparison.Ordinal) == true).ToList();
        if (stamped.Count > 0)
            o.Append($"stamps resolved: {stamped.Count:N0} file(s) where only keyword stamps collided · " +
                     $"{stamped.Count(f => f.Status == PortStatus.Clean):N0} {(r.Written ? "written" : "would apply")} · " +
                     $"{stamped.Count(f => f.Status == PortStatus.Already):N0} already there (the target's stamps kept)\n");

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

    private static (string Text, FileDiffInfo Info) RenderOne(CompareSession s, FileChange c, int context, bool remember = true,
        CancellationToken ct = default)
    {
        var w = new StringWriter { NewLine = "\n" };
        var stats = new PatchStats();
        PatchWriter.WriteChange(w, c, s.Left, s.Right, new PatchOptions { Context = Math.Clamp(context, 0, 1000), Cancellation = ct }, stats);
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
        // Line by line in place: a split was a second copy of a patch that can be a whole 16 MB file.
        for (int pos = 0; pos <= section.Length;)
        {
            int nl = section.IndexOf('\n', pos);
            var line = section.AsSpan(pos, (nl < 0 ? section.Length : nl) - pos);
            pos = nl < 0 ? section.Length + 1 : nl + 1;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal)) { body = false; continue; }
            if (line.StartsWith("@@", StringComparison.Ordinal)) { hunks++; body = true; continue; }
            if (!body || line.Length == 0) continue;
            if (line[0] == '+') plus++;
            else if (line[0] == '-') minus++;
        }
        return new FileDiffInfo(hunks, plus, minus, kind, stats.CoarseFiles > 0);
    }

    // ---- helpers ----

    /// <summary>A compare run with --fast-stat trusted the directory listing alone, so a same-size edit the listing
    /// missed (one made through another hard link) can be called identical: listed, but never patched or ported (W2).</summary>
    private static string? FastStat(CompareSession s) => s.Options.StrictStat ? null
        : $"error: compare {s.Id} ran with --fast-stat (the directory listing trusted alone), so it may have missed a " +
          "same-size edit; a patch or apply needs a compare without it — start the compare again\n";

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
    /// <summary>"code 1,180 · build 140 · docs 260 · binary 227": every type, in order, zeros too.</summary>
    public static string TypeLine(IEnumerable<FileType> types)
    {
        var n = new int[Enum.GetValues<FileType>().Length];
        foreach (var t in types) n[(int)t]++;
        return string.Join(" · ", Enum.GetValues<FileType>().Select(t => $"{FileTypes.Token(t)} {n[(int)t]:N0}"));
    }

    /// <summary>"Windows error 64: 120 failed tries, 118 then worked, 2 given up, 1 pause; …" — codes and counts only.</summary>
    public static string FailureLine(IEnumerable<Compare.ReadFailures> causes)
        => string.Join("; ", causes.Select(f => $"{f.Label}: {f.Failed:N0} failed tr{(f.Failed == 1 ? "y" : "ies")}, {f.Recovered:N0} then worked, " +
                                                 $"{f.GaveUp:N0} given up" + (f.Pauses > 0 ? $", {f.Pauses:N0} pause(s)" : "")));

    public static IEnumerable<string> Notes(CompareReport r, string? side = null)
    {
        var pre = side is null ? "" : side + ": ";
        var skipped = r.LeftSkipped.Select(x => (x, "left")).Concat(r.RightSkipped.Select(x => (x, "right"))).ToList();
        // Why a read couldn't vouch for its file, cause by cause (a file is counted once, both sides together).
        if (r.UnstableChanged > 0) yield return $"{pre}{r.UnstableChanged:N0} file(s) changed while being read (a live writer) — compared as read, not cached.";
        if (r.UnstableListing > 0)
            yield return $"{pre}{r.UnstableListing:N0} file(s) steady while read but not as their directory listing says (a stale entry: written through " +
                         "another hard link, or a server's cached listing; or touched since the walk) — compared as read, not cached. `codediffer statcheck` measures it.";
        if (r.CachedWithoutId > 0)
            yield return $"{pre}{r.CachedWithoutId:N0} file side(s) cached by size, modified and change time only: the server gives them another " +
                         "file id in its directory listing than when opened.";
        if (r.SharePauses > 0)
            yield return $"{pre}the trees stopped answering {r.SharePauses:N0} time(s), {ProgressView.Clock(r.SharePaused)} in all: the compare paused " +
                         "until they answered again, then carried on (nothing was marked unreadable for it).";
        if (r.RetriedReads > 0) yield return $"{pre}{r.RetriedReads:N0} read(s) failed and then worked when tried again.";
        if (r.ReadFailures.Count > 0) yield return $"{pre}failed reads by cause: {FailureLine(r.ReadFailures)}.";
        if (r.UnstableNoChangeTime > 0) yield return $"{pre}{r.UnstableNoChangeTime:N0} file(s) on a server that gives no change time — compared as read, not cached.";
        if (r.SkippedLinks > 0)
            yield return $"{pre}{r.SkippedLinks:N0} symlink(s)/junction(s) not followed — nothing behind them is compared" +
                         Some(skipped.Where(t => t.x.Kind == Walk.SkipKind.Link).Select(t => $"{t.Item2} {t.x.Path}"));
        if (r.SkippedNames > 0)
            yield return $"{pre}{r.SkippedNames:N0} name(s) ending in '.' or ' ', or device names like \"nul\", skipped — Windows opens another file (or the device) by that path" +
                         Some(skipped.Where(t => t.x.Kind == Walk.SkipKind.Name).Select(t => $"{t.Item2} {t.x.Path}"));
        if (r.RenameLimit is { } rl) yield return $"{pre}{rl}.";
        if (r.CacheSaveError is { } e) yield return $"{pre}the hash cache could not be saved ({e}) — the result stands; the next compare reads those files again.";
    }

    /// <summary>The directories that could not be listed, a few by name (" (left a, right b, …)"), or "" (a result saved
    /// before 2026-10-16 kept only their count).</summary>
    public static string Unlisted(CompareReport r)
        => Some(r.LeftSkipped.Where(x => x.Kind == Walk.SkipKind.Unlisted).Select(x => "left " + x.Path)
                .Concat(r.RightSkipped.Where(x => x.Kind == Walk.SkipKind.Unlisted).Select(x => "right " + x.Path)));

    /// <summary>A few of <paramref name="paths"/>, as " (a, b, c, …)", or "".</summary>
    internal static string Some(IEnumerable<string> paths)
    {
        var list = paths.Take(4).ToList();
        return list.Count == 0 ? "" : " (" + string.Join(", ", list.Take(3)) + (list.Count > 3 ? ", …" : "") + ")";
    }

    private static void Warnings(StringBuilder o, CompareReport r)
    {
        if (r.LeftDroppedDirectories + r.RightDroppedDirectories > 0)
            o.Append($"WARNING: incomplete walk — {r.LeftDroppedDirectories} left / {r.RightDroppedDirectories} right director(ies) " +
                     $"could not be listed{Unlisted(r)}; what the other tree has under them is unknown, listed as [unreadable].\n");
        foreach (var n in Notes(r)) o.Append("note: ").Append(n).Append('\n');
        if (r.UnreadableFiles > 0)
            o.Append($"WARNING: {r.UnreadableFiles} file(s) could not be read (locked, vanished or denied) — their verdict is unknown, " +
                     "listed as [unreadable] (a pair as modified, never identical); compare again once they can be read. " +
                     $"By side and cause: {ReadFailure.Line(ReadFailure.Tally(r))}.\n");
    }

    private static string Reasons(CompareReport r) =>
        string.Join(" · ", Enum.GetValues<ChangeReason>().Select(x => (k: CanonicalTokens.Token(x), n: r.ReasonCount(x)))
            .Append((k: "unreadable", n: r.UnreadableModified)).Where(t => t.n > 0)
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

    /// <summary>A comma list of file types, or null (any type) when none is given.</summary>
    internal static bool TryTypes(string? list, out HashSet<FileType>? types, out string error)
    {
        types = null;
        error = "";
        if (string.IsNullOrWhiteSpace(list)) return true;
        types = [];
        foreach (var part in list.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!FileTypes.TryParse(part, out var t)) { error = $"unknown file type '{part}' (use code, build, docs, binary)"; return false; }
            types.Add(t);
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
    /// A path filter, as list_files' path_glob and the HTML report's box take one: terms split on ';' or ',', each a
    /// <see cref="Glob"/>, a <c>/regex/</c> (case-insensitive, over the whole path), or after a '!' a path to leave out
    /// (<c>*.c; *.h; !third_party/**</c>). A path passes when it matches a term kept (any path, when all are '!') and no
    /// term left out; a rename passes on either of its paths. One term alone is the glob it always was, spaces and all.
    /// </summary>
    internal static bool TryPathFilter(string pattern, out Func<string, string?, bool> filter, out string error)
    {
        filter = (_, _) => true;
        error = "";
        bool many = pattern.IndexOfAny([';', ',']) >= 0;
        var keep = new List<Func<string, bool>>();
        var drop = new List<Func<string, bool>>();
        foreach (var part in many ? pattern.Split([';', ',']) : [pattern])
        {
            var term = many ? part.Trim() : part;
            bool not = term.StartsWith('!');
            if (not) term = term[1..].TrimStart();
            if (term.Length == 0) continue;
            Func<string, bool> test;
            if (term.Length > 2 && term[0] == '/' && term[^1] == '/')
            {
                Regex re;
                try { re = new Regex(term[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
                catch (ArgumentException ex) { error = $"bad regex {term}: {ex.Message}"; return false; }
                test = p => re.IsMatch(p);
            }
            else test = Glob(term);
            (not ? drop : keep).Add(test);
        }
        if (keep.Count == 0 && drop.Count == 0) return true;
        bool Any(List<Func<string, bool>> tests, string path, string? other) => tests.Any(t => t(path) || (other is not null && t(other)));
        filter = (path, other) => (keep.Count == 0 || Any(keep, path, other)) && !Any(drop, path, other);
        return true;
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
        bool Match(string p) => re.IsMatch(nameOnly ? p[(p.LastIndexOf('/') + 1)..] : p);
        // A name with an unpaired surrogate matches as listed too (U+FFFD): what an agent can copy into a pattern.
        return p => Match(p) || (ResultStore.HasLoneSurrogate(p) && Match(ResultStore.Shown(p)));
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

    private static string Clock(TimeSpan t) => ProgressView.Clock(t); // a day or more as hours, never dropped

    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{b / 1024.0:F1} KB",
        _ => $"{b} B",
    };

    private static string Delta(long d) => (d >= 0 ? "+" : "-") + Bytes(Math.Abs(d));
}
