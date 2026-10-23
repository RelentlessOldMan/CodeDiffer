using System.Diagnostics;
using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Hashing;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Port;

namespace CodeDiffer.Core.ThreeWay;

/// <summary>How a path fares when v1 and v2 (both derived from base) are merged.</summary>
public enum Merge3Outcome
{
    /// <summary>Only v1 changed it — the merge takes v1.</summary>
    V1Only,
    /// <summary>Only v2 changed it — the merge takes v2.</summary>
    V2Only,
    /// <summary>Both made the identical change.</summary>
    Agreed,
    /// <summary>Both changed it differently, but in separate regions — diff3 merges it cleanly.</summary>
    Merged,
    /// <summary>Both changed it and the changes collide — needs a human (or agent) decision.</summary>
    Conflict,
}

/// <summary>
/// One path's 3-way verdict. <see cref="Path"/> is the base path (or, for an add, the added path).
/// V1/V2 are each side's 2-way change (null = that side left it alone); MergedPath is where the merged
/// file lives (a rename on either side moves it; null when the merge deletes it).
/// </summary>
public sealed record Merge3Entry(
    string Path,
    FileChange? V1,
    FileChange? V2,
    Merge3Outcome Outcome,
    string? ConflictKind,
    int ConflictRegions,
    int CleanRegions,
    string? MergedPath,
    string? Note)
{
    /// <summary>What kind of file it is (<see cref="FileChange.Type"/>): binary when either side's bytes were found so.</summary>
    public FileType Type => FileTypes.Of(Path, V1?.Reason == ChangeReason.Binary || V2?.Reason == ChangeReason.Binary);

    /// <summary>The noise the entry is only (stamps, numbers, comments, whitespace, eol, encoding: each side's, joined),
    /// when every side that touched it changed it only that way (<see cref="NoiseFilter.Kind"/>); null when a side's
    /// change is real, and for a conflict, which needs deciding whatever it is — but a "stamps" one (only keyword stamps
    /// collide), whose sides changed nothing else, is noise.</summary>
    public IReadOnlyList<string>? Noise
    {
        get
        {
            if (Outcome == Merge3Outcome.Conflict && ConflictKind != TreeMerger.StampsConflict || V1 is null && V2 is null) return null;
            if (V1 is not null && NoiseFilter.Kind(V1) is null || V2 is not null && NoiseFilter.Kind(V2) is null) return null;
            return [.. NoiseFilter.Tokens.Where(t => NoiseFilter.Kind(V1)?.Contains(t) == true || NoiseFilter.Kind(V2)?.Contains(t) == true)];
        }
    }

    /// <summary>Whether <paramref name="filter"/> leaves the entry out: every side that touched it is noise it hides.</summary>
    public bool HiddenBy(NoiseFilter filter)
        => Noise is not null && (V1 is null || filter.Hides(V1)) && (V2 is null || filter.Hides(V2));
}

public sealed class ThreeWayReport
{
    public required CompareReport V1Report { get; init; }
    public required CompareReport V2Report { get; init; }
    /// <summary>Every path either side changed, sorted by path (ordinal). Untouched paths are not listed.</summary>
    public required IReadOnlyList<Merge3Entry> Entries { get; init; }
    public IReadOnlyList<(string Phase, TimeSpan Elapsed)> Timings { get; init; } = [];
    /// <summary>Bytes the process allocated in the classify and merge, after both compares.</summary>
    public long MergeAllocated { get; init; }

    public int Count(Merge3Outcome o) => Entries.Count(e => e.Outcome == o);
    public int DroppedDirectories => V1Report.LeftDroppedDirectories + V1Report.RightDroppedDirectories
                                   + V2Report.LeftDroppedDirectories + V2Report.RightDroppedDirectories;
    /// <summary>Files either compare could not read: their verdict is unknown.</summary>
    public int UnreadableFiles => V1Report.UnreadableFiles + V2Report.UnreadableFiles;
}

/// <summary>
/// 3-way TREE compare: base→v1 and base→v2 (the 2-way engine, run one after the other so the second reuses
/// the base hashes the first cached instead of re-reading the base), then every path either side touched
/// is classified. Paths both sides changed are compared v1-vs-v2 and, if different, merged with the same
/// <see cref="ThreeWayMerger"/> the 3-way contract verifies. Renames are followed: a file renamed on one
/// side and edited on the other merges at the new name.
/// </summary>
public static class TreeMerger
{
    /// <param name="progress1">Optional live progress of the base→v1 compare.</param>
    /// <param name="progress2">Optional live progress of the base→v2 compare (it starts when the first one ends).</param>
    /// <param name="ct">Cancels the run (throws <see cref="OperationCanceledException"/>); hashes read so far are kept.</param>
    public static ThreeWayReport Run(string baseDir, string v1Dir, string v2Dir, CompareOptions? options = null,
        CompareProgress? progress1 = null, CompareProgress? progress2 = null, CancellationToken ct = default)
    {
        var opt = options ?? new CompareOptions();
        var timings = new List<(string, TimeSpan)>();
        var sw = Stopwatch.StartNew();
        // The second compare reuses the first one's base listing and base hashes: the base is listed and
        // checked once, and both compares see the same snapshot of it.
        var sharedBase = new SharedTree();
        var r1 = new DirectoryComparer(opt).Compare(baseDir, v1Dir, progress1, sharedBase, ct);
        timings.Add(("compare base->v1", sw.Elapsed));
        sw.Restart();
        var r2 = new DirectoryComparer(opt).Compare(baseDir, v2Dir, progress2, sharedBase, ct);
        sharedBase = null!; // the base listing and its hashes: done with, not held through the classify
        timings.Add(("compare base->v2", sw.Elapsed));
        sw.Restart();
        long allocated = GC.GetTotalAllocatedBytes();

        // Key each change by its BASE path (renames by their source); an add has no base path, so it is keyed
        // by its own path in a separate namespace (an added path can't also be a base path, or it'd be modified).
        var side1 = Index(r1);
        var side2 = Index(r2);
        // Only the changes are kept: a row per unchanged file (twice) is only ever counted.
        r1 = r1.WithoutIdentical();
        r2 = r2.WithoutIdentical();
        var keys = side1.Keys.Union(side2.Keys).OrderBy(k => k.Path, StringComparer.Ordinal).ThenBy(k => k.Added).ToList();

        var entries = new Merge3Entry[keys.Count];
        Parallel.For(0, keys.Count, new ParallelOptions { MaxDegreeOfParallelism = opt.Parallelism, CancellationToken = ct }, i =>
        {
            var k = keys[i];
            side1.TryGetValue(k, out var c1);
            side2.TryGetValue(k, out var c2);
            try
            {
                var e = Classify(k.Path, c1, c2, baseDir, v1Dir, v2Dir, opt.MaxClassifyBytes, opt.ResolveStamps, ct);
                // A held rename, merged as its remove and its add: the entry says why it wasn't followed.
                var held = (c1?.RenameHeld is { } h1 ? $"v1: {h1}" : null) is { } n1 && c2?.RenameHeld is { } h2 ? $"{n1}; v2: {h2}"
                         : c1?.RenameHeld is { } only1 ? $"v1: {only1}" : c2?.RenameHeld is { } only2 ? $"v2: {only2}" : null;
                entries[i] = held is null ? e : e with { Note = e.Note is { } n ? $"{n}; {held}" : held };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entries[i] = new Merge3Entry(k.Path, c1, c2, Merge3Outcome.Conflict, "unreadable", 0, 0, null, ex.Message);
            }
        });

        // Two different files landing on one destination (a rename on one side, an add or another rename on
        // the other) can't both be kept: flag every entry involved — a content conflict too, or its marker file
        // would overwrite the other one. On Windows a destination differing only in case is the same file. A
        // conflict left unresolved keeps v1's file where v1 has it, so that path is taken too (v1 edits Foo.c, v2
        // deletes it and adds an unrelated foo.c: writing foo.c would overwrite v1's edit on Windows).
        var collide = entries.Select(e => (E: e, At: Occupies(e))).Where(t => t.At is not null)
            .GroupBy(t => t.At!, PathComparer).Where(g => g.Count() > 1).SelectMany(g => g.Select(t => t.E)).ToHashSet();
        for (int i = 0; i < entries.Length; i++)
            if (collide.Contains(entries[i]))
                entries[i] = entries[i].MergedPath is null
                    ? entries[i] with { Note = (entries[i].Note is { } n ? n + "; " : "") + $"another change lands on {Occupies(entries[i])} too" }
                    : entries[i] with { Outcome = Merge3Outcome.Conflict, ConflictKind = "path collision", Note = $"another change also lands on {entries[i].MergedPath}" };

        // A file where the other side has a directory (v2 turns directory D into a file D while v1 adds D/z.txt, or
        // the reverse): both can't be. Every file of the merged tree that isn't an entry is unchanged on both sides,
        // and so can't be under (or be) a path one side turned into a file — the entries alone show every clash.
        var occupied = new Dictionary<string, Merge3Entry>(PathComparer);
        foreach (var e in entries)
            if (Occupies(e) is { } at) occupied.TryAdd(at, e);
        var clash = new Dictionary<Merge3Entry, string>();
        foreach (var e in entries)
        {
            if (Occupies(e) is not { } at) continue;
            for (int slash = at.LastIndexOf('/'); slash > 0; slash = at.LastIndexOf('/', slash - 1))
                if (occupied.TryGetValue(at[..slash], out var file))
                {
                    clash.TryAdd(e, $"another change puts a file at {at[..slash]}, a directory here");
                    clash.TryAdd(file, $"another change puts {at} under it, as a directory");
                    break;
                }
        }
        for (int i = 0; i < entries.Length; i++)
            if (clash.TryGetValue(entries[i], out var why))
                entries[i] = entries[i].MergedPath is null
                    ? entries[i] with { Note = (entries[i].Note is { } n ? n + "; " : "") + why }
                    : entries[i] with { Outcome = Merge3Outcome.Conflict, ConflictKind = "file/directory clash", Note = why };

        timings.Add(("classify + merge", sw.Elapsed));
        return new ThreeWayReport { V1Report = r1, V2Report = r2, Entries = entries, Timings = timings,
                                    MergeAllocated = GC.GetTotalAllocatedBytes() - allocated };
    }

    /// <summary>Whose file is the merge, whole: 1 when v2 only moved it (byte-identical) and v1 changed it in place, 2 the
    /// reverse, else 0. Such an entry is merged by taking that side's file, never line by line.</summary>
    internal static int WholeFrom(FileChange? c1, FileChange? c2)
        => c1 is null || c2 is null ? 0
            : c2 is { Status: ChangeStatus.Renamed, PureRename: true } && c1.Status == ChangeStatus.Modified ? 1
            : c1 is { Status: ChangeStatus.Renamed, PureRename: true } && c2.Status == ChangeStatus.Modified ? 2 : 0;

    /// <summary>The conflict kind of a text file whose colliding lines differ only in keyword stamps (<c>$Id: … $</c>): with
    /// the stamps bare, it merges cleanly. Still a conflict (someone picks a stamp), written with markers like "content".</summary>
    public const string StampsConflict = "stamps";

    /// <summary>The conflict kinds written as one file with diff3 markers.</summary>
    internal static bool HasMarkers(string? kind) => kind is "content" or "add/add" or StampsConflict;

    /// <summary>How destination paths are told apart: ignoring case where the file system does.</summary>
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly record struct Key(string Path, bool Added);

    private static Dictionary<Key, FileChange> Index(CompareReport r)
    {
        var d = new Dictionary<Key, FileChange>();
        foreach (var c in r.Changes.SelectMany(c => c.ActedOn())) // a held rename: its remove and its add
            switch (c.Status)
            {
                case ChangeStatus.Identical: break;
                case ChangeStatus.Added: d[new Key(c.RelativePath, true)] = c; break;
                case ChangeStatus.Renamed: d[new Key(c.RenamedFrom!, false)] = c; break;
                default: d[new Key(c.RelativePath, false)] = c; break;
            }
        return d;
    }

    private static string? Dest(FileChange c) => c.Status == ChangeStatus.Removed ? null : c.RelativePath;

    /// <summary>The path an entry's file has in the merged tree: where the merge puts it, or — for a conflict left
    /// unresolved, which keeps v1's version — where v1 has it (none when v1 deleted it).</summary>
    private static string? Occupies(Merge3Entry e)
    {
        if (e.MergedPath is not null) return e.MergedPath;
        if (e.Outcome != Merge3Outcome.Conflict) return null;
        return e.V1 is null ? e.Path : e.V1.Status == ChangeStatus.Removed ? null : e.V1.RelativePath;
    }

    private static Merge3Entry Classify(string path, FileChange? c1, FileChange? c2, string b, string v1, string v2, long maxText,
        bool resolveStamps, CancellationToken ct)
    {
        // Behind a link one tree has in its place (not followed), under a directory one tree could not list, or a name
        // Windows can't open by path: whether it changed there is unknown — never merged.
        if ((c1?.BehindLink ?? false) || (c2?.BehindLink ?? false))
        {
            var note = (c1?.BehindLink ?? false) ? c1!.Unreadable : c2!.Unreadable;
            var unknown = !path.Split('/').All(Walk.TreeWalker.UsableName) ? "unopenable name"
                     : note?.Contains("could not be listed", StringComparison.Ordinal) == true ? "unlisted directory" : "behind a link";
            return new(path, c1, c2, Merge3Outcome.Conflict, unknown, 0, 0, null, note);
        }
        if (c2 is null) return new(path, c1, null, Merge3Outcome.V1Only, null, 0, 0, Dest(c1!), null);
        if (c1 is null) return new(path, null, c2, Merge3Outcome.V2Only, null, 0, 0, Dest(c2), null);

        Merge3Entry Conflict(string kind, string? note = null, int regions = 1, int clean = 0) =>
            new(path, c1, c2, Merge3Outcome.Conflict, kind, regions, clean, null, note);

        bool del1 = c1.Status == ChangeStatus.Removed, del2 = c2.Status == ChangeStatus.Removed;
        if (del1 && del2) return new(path, c1, c2, Merge3Outcome.Agreed, null, 0, 0, null, null);
        if (del1 || del2)
            return Conflict(del1 ? "delete/modify" : "modify/delete",
                $"{(del1 ? "v1" : "v2")} deleted it; {(del1 ? "v2" : "v1")} {(del1 ? c2 : c1).Status.ToString().ToLowerInvariant()} it");

        string d1 = c1.RelativePath, d2 = c2.RelativePath;
        if (d1 != d2 && c1.Status == ChangeStatus.Renamed && c2.Status == ChangeStatus.Renamed)
            return Conflict("rename/rename", $"v1 renamed it to {d1}, v2 to {d2}");
        string dest = c1.Status == ChangeStatus.Renamed ? d1 : d2;

        var f1 = Full(v1, d1);
        var f2 = Full(v2, d2);
        // One side only moved it (a byte-identical rename), the other changed it in place: the change is the merge, at
        // the new name — however it would merge line by line (a binary or large file can't, but needn't).
        Merge3Entry? Whole() => WholeFrom(c1, c2) switch
        {
            1 => new(path, c1, c2, Merge3Outcome.Merged, null, 0, 0, dest, $"v2 only renamed it; v1's change is taken whole"),
            2 => new(path, c1, c2, Merge3Outcome.Merged, null, 0, 0, dest, $"v1 only renamed it; v2's change is taken whole"),
            _ => null,
        };
        if (Math.Max(c1.RightSize, c2.RightSize) > maxText)
        {
            // Never loaded whole: a same-size pair is compared in chunks, up to its first difference (a cancel is seen
            // between them). Two SHA-256 passes cost about 20 times the CPU and always read both to the end.
            bool same = c1.RightSize == c2.RightSize && TextInspector.SameBytes(f1, f2, ct);
            return same ? new(path, c1, c2, Merge3Outcome.Agreed, null, 0, 0, dest, null)
                : Whole() ?? Conflict("large", "both changed a large file differently — not merged line by line");
        }
        var bytes1 = File.ReadAllBytes(f1);
        var bytes2 = File.ReadAllBytes(f2);
        if (bytes1.AsSpan().SequenceEqual(bytes2))
            return new(path, c1, c2, Merge3Outcome.Agreed, null, 0, 0, dest, null);

        bool added = c1.Status == ChangeStatus.Added; // then both are adds (same key namespace)

        if (Math.Max(bytes1.Length, bytes2.Length) > maxText)
            return Whole() ?? Conflict("large", "both changed a large file differently — not merged line by line");
        // Both sides small, the base not (both cut it down): never read it whole either.
        const string largeBase = "both changed it differently and the base file is large — not merged line by line";
        if (!added && Math.Max(c1.LeftSize, c2.LeftSize) > maxText) return Whole() ?? Conflict("large", largeBase);
        var baseBytes = added ? [] : File.ReadAllBytes(Full(b, path));
        if (baseBytes.Length > maxText) return Whole() ?? Conflict("large", largeBase); // grown since it was listed
        if (!ChangePorter.Text.TryReadAll(baseBytes, bytes1, bytes2, out var tb, out var t1, out var t2)
            || LooksBinary(bytes1) || LooksBinary(bytes2) || LooksBinary(baseBytes))
            return Whole() ?? Conflict(added ? "add/add (binary)" : "binary", "both changed a binary (or non-text) file differently");

        var form = FormOf(tb, t1, t2, added);
        var lb = Split(tb.Content, form.Normalize);
        var l1 = Split(t1.Content, form.Normalize);
        var l2 = Split(t2.Content, form.Normalize);
        var regions = ThreeWayMerger.Regions(lb, l1, l2, ct);
        int conflicts = regions.Count(r => r.Kind == RegionKind.Conflict);
        int clean = regions.Count - conflicts;
        // Conflicting lines are written with markers; a conflict in the encoding or line endings too must not vanish
        // behind them — the note says so, and that the markers file is written in v1's form.
        if (conflicts > 0)
        {
            var bare = StampsMerge(lb, l1, l2, ct);
            if (bare is not null && resolveStamps && form.Conflict is null && !(form.Normalize && form.Eol == "mixed"))
                return new(path, c1, c2, Merge3Outcome.Merged, null, 0, bare.Count, dest, form.Note is { } n ? $"{StampsResolved}; {n}" : StampsResolved);
            var ck = bare is not null ? StampsConflict : added ? "add/add" : "content";
            var why = ck == StampsConflict ? "only keyword stamps collide; the rest merges cleanly" : null;
            var also = form.Conflict is { } more ? $"{more} conflict too ({form.Note}): the markers file is written in v1's" : form.Note;
            return new(path, c1, c2, Merge3Outcome.Conflict, ck, conflicts, clean, dest, why is null ? also : also is null ? why : $"{why}; {also}");
        }
        // The lines merge, but both sides changed the encoding or the line endings differently: someone has to pick.
        if (form.Conflict is { } kind)
            return new(path, c1, c2, Merge3Outcome.Conflict, kind, 0, clean, dest, form.Note);
        return new(path, c1, c2, Merge3Outcome.Merged, null, 0, clean, dest, form.Note);
    }

    /// <summary>The note of a file merged by <see cref="CompareOptions.ResolveStamps"/>.</summary>
    public const string StampsResolved = "only keyword stamps collided: each taken from the side that changed it, v2's where both did";

    /// <summary>The regions of a merge that conflicts, merged again with each keyword stamp bare (<c>$Id$</c>): null unless
    /// that merge is clean, and every stamp a side changed on a line the other side edited can be carried into that edit
    /// (<see cref="StampedEdit"/>). A stamp never spans lines, so the bare lines are the same lines, numbered alike.</summary>
    internal static List<MergeRegion>? StampsMerge(List<string> lb, List<string> l1, List<string> l2, CancellationToken ct)
    {
        static bool Any(List<string> l) => l.Exists(s => s.Contains('$'));
        if (!Any(l1) && !Any(l2)) return null;
        static List<string> Bare(List<string> l) => l.ConvertAll(NoiseFinder.Stamps);
        var regions = ThreeWayMerger.Regions(Bare(lb), Bare(l1), Bare(l2), ct);
        if (regions.Exists(r => r.Kind == RegionKind.Conflict)) return null;
        return regions.TrueForAll(r => r.Kind is not (RegionKind.V1Only or RegionKind.V2Only) || StampedEdit(r, lb, l1, l2) is not null) ? regions : null;
    }

    /// <summary>
    /// The lines a one-sided region of a <see cref="StampsMerge"/> writes: the editing side's, each carrying a keyword stamp
    /// the other side changed on the base line it replaces (v2's where both changed it). In such a region the other side
    /// differs from the base only in stamps, line for line. Null when a changed stamp can't be carried: the edit isn't
    /// line for line, or its stamps don't pair up with the base's.
    /// </summary>
    internal static List<string>? StampedEdit(MergeRegion r, List<string> lb, List<string> l1, List<string> l2)
    {
        bool v1Edits = r.Kind == RegionKind.V1Only;
        var (edit, start, count, other, otherStart) = v1Edits
            ? (l1, r.V1Start0, r.V1Lines, l2, r.V2Start0)
            : (l2, r.V2Start0, r.V2Lines, l1, r.V1Start0);
        var lines = edit.GetRange(start, count);
        for (int k = 0; k < r.BaseLines; k++)
        {
            string was = lb[r.BaseStart0 + k], theirs = other[otherStart + k];
            if (theirs == was) continue;
            if (count != r.BaseLines || NoiseFinder.CarryStamps(lines[k], was, theirs, otherWins: v1Edits) is not { } carried) return null;
            lines[k] = carried;
        }
        return lines;
    }

    /// <summary>
    /// The merged file for one entry, diff3-style: clean regions resolved, each conflict shown as
    /// <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; v1 / ||||||| base / ======= / &gt;&gt;&gt;&gt;&gt;&gt;&gt; v2</c>. Null for an
    /// entry that has no line-level merge (one-sided, binary, delete conflicts…).
    /// </summary>
    /// <param name="ct">Stops the line merge (a giant file's diffs may run long): throws <see cref="OperationCanceledException"/>.</param>
    public static string? MergedText(Merge3Entry e, string b, string v1, string v2, CancellationToken ct = default) => Compose(e, b, v1, v2, ct)?.Merged;

    /// <summary>
    /// The merged file as bytes to write: <see cref="MergedText"/> in the merged encoding and line endings — 3-way like
    /// the lines: a side that changed them wins (see <see cref="Form"/>). Null when the entry has no line-level merge.
    /// </summary>
    public static byte[]? MergedBytes(Merge3Entry e, string b, string v1, string v2) => MergedBytes(e, b, v1, v2, out _);

    /// <param name="conflicts">How many conflict regions the merge has now (re-merged from the trees as they are).</param>
    public static byte[]? MergedBytes(Merge3Entry e, string b, string v1, string v2, out int conflicts, CancellationToken ct = default)
    {
        conflicts = 0;
        if (Compose(e, b, v1, v2, ct) is not { } m) return null;
        conflicts = m.Conflicts;
        var text = m.Form.Normalize && m.Form.Eol == "CRLF" ? m.Merged.Replace("\n", "\r\n") : m.Merged;
        return m.Form.Encoding.Encode(text);
    }

    /// <summary>
    /// What a clean merge does to the base, as a unified diff (base → merged result). Null when the entry has
    /// no line-level merge. When the sides' line endings differed, the base is compared EOL-normalized, the
    /// way it was merged.
    /// </summary>
    public static string? MergeDiff(Merge3Entry e, string b, string v1, string v2, int context = UnifiedDiff.DefaultContext,
        CancellationToken ct = default)
    {
        if (Compose(e, b, v1, v2, ct) is not { } m) return null;
        var w = new StringWriter { NewLine = "\n" };
        UnifiedDiff.Write(w, e.Path, e.MergedPath ?? e.Path, string.Concat(m.Base), m.Merged, context, null, ct);
        return w.ToString();
    }

    /// <summary>The merge of one entry; its base as lines, joined only by the one caller that diffs against it.</summary>
    private static (List<string> Base, string Merged, Form Form, int Conflicts)? Compose(Merge3Entry e, string b, string v1, string v2,
        CancellationToken ct)
    {
        if (e.V1 is null || e.V2 is null || !(e.ConflictKind is null || HasMarkers(e.ConflictKind)) || e.Outcome == Merge3Outcome.Agreed) return null;
        ct.ThrowIfCancellationRequested(); // before three files are read whole
        // Taken whole from one side (it may be binary or large): no line merge to show, and the file is never read whole.
        if (e.Outcome == Merge3Outcome.Merged && e.CleanRegions == 0 && e.ConflictRegions == 0 && WholeFrom(e.V1, e.V2) != 0) return null;
        bool added = e.V1.Status == ChangeStatus.Added;
        if (!ChangePorter.Text.TryReadAll(added ? [] : File.ReadAllBytes(Full(b, e.Path)), File.ReadAllBytes(Full(v1, e.V1.RelativePath)),
                File.ReadAllBytes(Full(v2, e.V2.RelativePath)), out var tb, out var t1, out var t2)) return null;
        var form = FormOf(tb, t1, t2, added);
        var lb = Split(tb.Content, form.Normalize);
        var l1 = Split(t1.Content, form.Normalize);
        var l2 = Split(t2.Content, form.Normalize);
        var regions = ThreeWayMerger.Regions(lb, l1, l2, ct);
        // Merged though its lines conflict: only keyword stamps collide (--resolve-stamps). Merged on the bare stamps, each
        // unchanged line taken from the side that changed its stamp (v2's where both did), and an agreed region from v2.
        bool stamps = false;
        if (e.Outcome == Merge3Outcome.Merged && regions.Exists(r => r.Kind == RegionKind.Conflict) && StampsMerge(lb, l1, l2, ct) is { } bare)
            (regions, stamps) = (bare, true);

        // A "mixed" result has no one line ending to convert to: emit each side's own lines as they are (only CRLF was
        // collapsed to compare them, so the line numbers match), the unchanged ones from the side whose style won.
        bool raw = form.Normalize && form.Eol == "mixed";
        var (ob, o1, o2) = raw ? (Split(tb.Content, false), Split(t1.Content, false), Split(t2.Content, false)) : (lb, l1, l2);
        var gap = form.GapSide switch { 1 when raw => o1, 2 when raw => o2, _ => ob };

        var o = new StringBuilder();
        int cursor = 0, conflicts = 0;
        string nl = !form.Normalize && form.Eol == "CRLF" ? "\r\n" : "\n"; // markers in the file's line endings (converted later if normalized)
        void Emit(List<string> lines, int start, int count)
        {
            for (int k = 0; k < count; k++) o.Append(lines[start + k]);
        }
        void EmitGap(int from, int to, int shift) => Emit(gap, from + shift, to - from); // unchanged base lines, from the gap source
        void EmitStampGap(int from, int to, int s1, int s2) // the same, but a side may have changed a stamp in them
        {
            for (int i = from; i < to; i++) o.Append(o2[i + s2] != ob[i] ? o2[i + s2] : o1[i + s1]);
        }
        int Shift(MergeRegion r) => form.GapSide switch { 1 when raw => r.V1Start0 - r.BaseStart0, 2 when raw => r.V2Start0 - r.BaseStart0, _ => 0 };
        void Marker(string text)
        {
            if (o.Length > 0 && o[^1] != '\n') o.Append(nl); // a side ending without a newline: marker still on its own line
            o.Append(text).Append(nl);
        }
        foreach (var r in regions)
        {
            if (stamps) EmitStampGap(cursor, r.BaseStart0, r.V1Start0 - r.BaseStart0, r.V2Start0 - r.BaseStart0);
            else EmitGap(cursor, r.BaseStart0, Shift(r));
            cursor = r.BaseStart0 + r.BaseLines;
            switch (r.Kind)
            {
                case RegionKind.V1Only or RegionKind.V2Only when stamps: o.AppendJoin("", StampedEdit(r, ob, o1, o2)!); break;
                case RegionKind.V2Only: Emit(o2, r.V2Start0, r.V2Lines); break;
                case RegionKind.Conflict:
                    conflicts++;
                    Marker($"<<<<<<< v1 ({e.V1.RelativePath}:{r.V1Start0 + 1})");
                    Emit(o1, r.V1Start0, r.V1Lines);
                    Marker($"||||||| base ({e.Path}:{r.BaseStart0 + 1})");
                    Emit(ob, r.BaseStart0, r.BaseLines);
                    Marker("=======");
                    Emit(o2, r.V2Start0, r.V2Lines);
                    Marker($">>>>>>> v2 ({e.V2.RelativePath}:{r.V2Start0 + 1})");
                    break;
                case RegionKind.Agreed when stamps || form.GapSide == 2 && raw: Emit(o2, r.V2Start0, r.V2Lines); break;
                default: Emit(o1, r.V1Start0, r.V1Lines); break; // V1Only or Agreed
            }
        }
        int tail = form.GapSide switch { 1 when raw => o1.Count - ob.Count, 2 when raw => o2.Count - ob.Count, _ => 0 };
        if (stamps) EmitStampGap(cursor, ob.Count, o1.Count - ob.Count, o2.Count - ob.Count);
        else EmitGap(cursor, lb.Count, tail);
        return (ob, o.ToString(), form, conflicts);
    }

    /// <summary>
    /// How a merged text file is written: 3-way on its encoding (with BOM) and its line-ending style, the way its lines
    /// are merged — a side that changed it wins, both changing it the same way agree, both changing it differently is a
    /// conflict (<see cref="Form.Conflict"/>). Two adds have no base: they must agree.
    /// </summary>
    /// <param name="Normalize">The sides' line endings differ: lines are compared with CRLF read as LF.</param>
    /// <param name="Eol">The result's style: LF, CRLF or mixed.</param>
    /// <param name="Encoding">The side whose encoding the result is written in.</param>
    /// <param name="GapSide">Whose line-ending style won (0 base, 1 v1, 2 v2).</param>
    internal sealed record Form(bool Normalize, string Eol, ChangePorter.Text Encoding, int GapSide, string? Conflict, string? Note);

    private static Form FormOf(ChangePorter.Text tb, ChangePorter.Text t1, ChangePorter.Text t2, bool added)
    {
        static bool Same(ChangePorter.Text a, ChangePorter.Text b)
            => a.Encoding.CodePage == b.Encoding.CodePage && a.Encoding.GetPreamble().Length == b.Encoding.GetPreamble().Length;
        static string Name(ChangePorter.Text t) => t.Encoding.WebName.ToUpperInvariant() + (t.Encoding.GetPreamble().Length > 0 ? " with BOM" : "");
        var notes = new List<string>();
        string? conflict = null;

        var enc = t1;
        if (added ? !Same(t1, t2) : !Same(t1, tb) && !Same(t2, tb) && !Same(t1, t2))
        {
            conflict = "encoding";
            notes.Add($"v1 changed the encoding to {Name(t1)}, v2 to {Name(t2)}");
        }
        else if (!added && Same(t1, tb) && !Same(t2, tb))
        {
            enc = t2;
            notes.Add($"v2 changed the encoding ({Name(tb)} → {Name(t2)}); the merge keeps it");
        }
        else if (!added && !Same(t1, tb)) notes.Add($"v1 changed the encoding ({Name(tb)} → {Name(t1)}); the merge keeps it");

        // A file with no line break at all has no line-ending style (a one-line file without its newline is not "LF"):
        // it takes the base's, or without one the other side's.
        string e1 = t1.Eol, e2 = t2.Eol, eb = added ? "none" : tb.Eol;
        if (e1 == "none") e1 = eb != "none" ? eb : e2;
        if (e2 == "none") e2 = eb != "none" ? eb : e1;
        bool noBase = eb == "none";
        if (noBase) eb = e1 == "none" ? "LF" : e1;
        string eol = eb;
        int side = 0;
        if (noBase && e1 != e2)
        {
            conflict ??= "line endings";
            notes.Add(added ? $"v1 added it with {e1} line endings, v2 with {e2}" : $"v1 gave it {e1} line endings, v2 {e2} (the base has no line break)");
            eol = e1;
            side = 1;
        }
        else if (!noBase && (e1 != eb || e2 != eb))
        {
            if (e1 == eb) (eol, side) = (e2, 2);
            else if (e2 == eb || e1 == e2) (eol, side) = (e1, 1);
            else
            {
                conflict ??= "line endings";
                notes.Add($"v1 changed the line endings to {e1}, v2 to {e2}");
                (eol, side) = (e1, 1);
            }
            if (conflict != "line endings") notes.Add($"{(side == 1 ? "v1" : "v2")} changed the line endings ({eb} → {eol}); the merge keeps them");
        }
        return new(side != 0, eol, enc, side, conflict, notes.Count > 0 ? string.Join("; ", notes) : null);
    }

    /// <summary>Lines, each with its ending; <paramref name="normalize"/> reads CRLF as LF (a lone CR stays content).</summary>
    private static List<string> Split(string text, bool normalize) => ChangePorter.Lines(normalize ? text.Replace("\r\n", "\n") : text, false);

    private static bool LooksBinary(byte[] bytes) => TextInspector.LooksBinary(bytes); // read whole: sniffed whole

    private static string Full(string root, string rel) => System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
}
