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
    string? Note);

public sealed class ThreeWayReport
{
    public required CompareReport V1Report { get; init; }
    public required CompareReport V2Report { get; init; }
    /// <summary>Every path either side changed, sorted by path (ordinal). Untouched paths are not listed.</summary>
    public required IReadOnlyList<Merge3Entry> Entries { get; init; }
    public IReadOnlyList<(string Phase, TimeSpan Elapsed)> Timings { get; init; } = [];

    public int Count(Merge3Outcome o) => Entries.Count(e => e.Outcome == o);
    public int DroppedDirectories => V1Report.LeftDroppedDirectories + V1Report.RightDroppedDirectories
                                   + V2Report.LeftDroppedDirectories + V2Report.RightDroppedDirectories;
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
        timings.Add(("compare base->v2", sw.Elapsed));
        sw.Restart();

        // Key each change by its BASE path (renames by their source); an add has no base path, so it is keyed
        // by its own path in a separate namespace (an added path can't also be a base path, or it'd be modified).
        var side1 = Index(r1);
        var side2 = Index(r2);
        var keys = side1.Keys.Union(side2.Keys).OrderBy(k => k.Path, StringComparer.Ordinal).ThenBy(k => k.Added).ToList();

        var entries = new Merge3Entry[keys.Count];
        Parallel.For(0, keys.Count, new ParallelOptions { MaxDegreeOfParallelism = opt.Parallelism, CancellationToken = ct }, i =>
        {
            var k = keys[i];
            side1.TryGetValue(k, out var c1);
            side2.TryGetValue(k, out var c2);
            try { entries[i] = Classify(k.Path, c1, c2, baseDir, v1Dir, v2Dir, opt.MaxClassifyBytes, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entries[i] = new Merge3Entry(k.Path, c1, c2, Merge3Outcome.Conflict, "unreadable", 0, 0, null, ex.Message);
            }
        });

        // Two different files landing on one destination (a rename on one side, an add or another rename on
        // the other) can't both be kept: flag every entry involved — a content conflict too, or its marker file
        // would overwrite the other one. On Windows a destination differing only in case is the same file.
        var collide = entries.Where(e => e.MergedPath is not null)
            .GroupBy(e => e.MergedPath!, PathComparer).Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
        for (int i = 0; i < entries.Length; i++)
            if (collide.Contains(entries[i]))
                entries[i] = entries[i] with { Outcome = Merge3Outcome.Conflict, ConflictKind = "path collision", Note = $"another change also lands on {entries[i].MergedPath}" };

        timings.Add(("classify + merge", sw.Elapsed));
        return new ThreeWayReport { V1Report = r1, V2Report = r2, Entries = entries, Timings = timings };
    }

    /// <summary>How destination paths are told apart: ignoring case where the file system does.</summary>
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly record struct Key(string Path, bool Added);

    private static Dictionary<Key, FileChange> Index(CompareReport r)
    {
        var d = new Dictionary<Key, FileChange>();
        foreach (var c in r.Changes)
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

    private static Merge3Entry Classify(string path, FileChange? c1, FileChange? c2, string b, string v1, string v2, long maxText, CancellationToken ct)
    {
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
        if (Math.Max(c1.RightSize, c2.RightSize) > maxText)
        {
            // Never loaded whole: a same-size pair is hashed in chunks (a cancel is seen between them).
            bool same = c1.RightSize == c2.RightSize && ContentHasher.HashFile(f1, ct) == ContentHasher.HashFile(f2, ct);
            return same ? new(path, c1, c2, Merge3Outcome.Agreed, null, 0, 0, dest, null)
                : Conflict("large", "both changed a large file differently — not merged line by line");
        }
        var bytes1 = File.ReadAllBytes(f1);
        var bytes2 = File.ReadAllBytes(f2);
        if (bytes1.AsSpan().SequenceEqual(bytes2))
            return new(path, c1, c2, Merge3Outcome.Agreed, null, 0, 0, dest, null);

        bool added = c1.Status == ChangeStatus.Added; // then both are adds (same key namespace)

        if (Math.Max(bytes1.Length, bytes2.Length) > maxText)
            return Conflict("large", "both changed a large file differently — not merged line by line");
        var baseBytes = added ? [] : File.ReadAllBytes(Full(b, path));
        if (!ChangePorter.Text.TryRead(baseBytes, out var tb) || !ChangePorter.Text.TryRead(bytes1, out var t1) || !ChangePorter.Text.TryRead(bytes2, out var t2)
            || LooksBinary(bytes1) || LooksBinary(bytes2) || LooksBinary(baseBytes))
            return Conflict(added ? "add/add (binary)" : "binary", "both changed a binary (or non-text) file differently");

        bool normalize = t1.Eol != tb.Eol || t2.Eol != tb.Eol;
        var lb = ChangePorter.Lines(tb.Content, normalize);
        var l1 = ChangePorter.Lines(t1.Content, normalize);
        var l2 = ChangePorter.Lines(t2.Content, normalize);
        var regions = ThreeWayMerger.Regions(lb, l1, l2);
        int conflicts = regions.Count(r => r.Kind == RegionKind.Conflict);
        int clean = regions.Count - conflicts;
        string? note = normalize ? "line endings differ between the sides; merged ignoring them" : null;
        if (conflicts > 0)
            return new(path, c1, c2, Merge3Outcome.Conflict, added ? "add/add" : "content", conflicts, clean, dest, note);
        return new(path, c1, c2, Merge3Outcome.Merged, null, 0, clean, dest, note);
    }

    /// <summary>
    /// The merged file for one entry, diff3-style: clean regions resolved, each conflict shown as
    /// <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; v1 / ||||||| base / ======= / &gt;&gt;&gt;&gt;&gt;&gt;&gt; v2</c>. Null for an
    /// entry that has no line-level merge (one-sided, binary, delete conflicts…).
    /// </summary>
    public static string? MergedText(Merge3Entry e, string b, string v1, string v2) => Compose(e, b, v1, v2)?.Merged;

    /// <summary>
    /// The merged file as bytes to write: <see cref="MergedText"/> in v1's encoding, and in v1's line endings when the
    /// sides' endings differed (it was merged ignoring them). Null when the entry has no line-level merge.
    /// </summary>
    public static byte[]? MergedBytes(Merge3Entry e, string b, string v1, string v2) => MergedBytes(e, b, v1, v2, out _);

    /// <param name="conflicts">How many conflict regions the merge has now (re-merged from the trees as they are).</param>
    public static byte[]? MergedBytes(Merge3Entry e, string b, string v1, string v2, out int conflicts)
    {
        conflicts = 0;
        if (Compose(e, b, v1, v2) is not { } m) return null;
        conflicts = m.Conflicts;
        var text = m.Normalized && m.V1.Eol == "CRLF" ? m.Merged.Replace("\n", "\r\n") : m.Merged;
        return m.V1.Encode(text);
    }

    /// <summary>
    /// What a clean merge does to the base, as a unified diff (base → merged result). Null when the entry has
    /// no line-level merge. When the sides' line endings differed, the base is compared EOL-normalized, the
    /// way it was merged.
    /// </summary>
    public static string? MergeDiff(Merge3Entry e, string b, string v1, string v2, int context = UnifiedDiff.DefaultContext)
    {
        if (Compose(e, b, v1, v2) is not { } m) return null;
        var w = new StringWriter { NewLine = "\n" };
        UnifiedDiff.Write(w, e.Path, e.MergedPath ?? e.Path, m.Base, m.Merged, context, null);
        return w.ToString();
    }

    private static (string Base, string Merged, ChangePorter.Text V1, bool Normalized, int Conflicts)? Compose(Merge3Entry e, string b, string v1, string v2)
    {
        if (e.V1 is null || e.V2 is null || e.ConflictKind is not (null or "content" or "add/add") || e.Outcome == Merge3Outcome.Agreed) return null;
        bool added = e.V1.Status == ChangeStatus.Added;
        if (!ChangePorter.Text.TryRead(added ? [] : File.ReadAllBytes(Full(b, e.Path)), out var tb)
            || !ChangePorter.Text.TryRead(File.ReadAllBytes(Full(v1, e.V1.RelativePath)), out var t1)
            || !ChangePorter.Text.TryRead(File.ReadAllBytes(Full(v2, e.V2.RelativePath)), out var t2)) return null;
        bool normalize = t1.Eol != tb.Eol || t2.Eol != tb.Eol;
        var lb = ChangePorter.Lines(tb.Content, normalize);
        var l1 = ChangePorter.Lines(t1.Content, normalize);
        var l2 = ChangePorter.Lines(t2.Content, normalize);

        var o = new StringBuilder();
        int cursor = 0, conflicts = 0;
        string nl = !normalize && t1.Eol == "CRLF" ? "\r\n" : "\n"; // markers in the file's own line endings
        void Emit(List<string> lines, int start, int count)
        {
            for (int k = 0; k < count; k++) o.Append(lines[start + k]);
        }
        void Marker(string text)
        {
            if (o.Length > 0 && o[^1] != '\n') o.Append(nl); // a side ending without a newline: marker still on its own line
            o.Append(text).Append(nl);
        }
        foreach (var r in ThreeWayMerger.Regions(lb, l1, l2))
        {
            Emit(lb, cursor, r.BaseStart0 - cursor);
            cursor = r.BaseStart0 + r.BaseLines;
            switch (r.Kind)
            {
                case RegionKind.V2Only: Emit(l2, r.V2Start0, r.V2Lines); break;
                case RegionKind.Conflict:
                    conflicts++;
                    Marker($"<<<<<<< v1 ({e.V1.RelativePath}:{r.V1Start0 + 1})");
                    Emit(l1, r.V1Start0, r.V1Lines);
                    Marker($"||||||| base ({e.Path}:{r.BaseStart0 + 1})");
                    Emit(lb, r.BaseStart0, r.BaseLines);
                    Marker("=======");
                    Emit(l2, r.V2Start0, r.V2Lines);
                    Marker($">>>>>>> v2 ({e.V2.RelativePath}:{r.V2Start0 + 1})");
                    break;
                default: Emit(l1, r.V1Start0, r.V1Lines); break; // V1Only or Agreed
            }
        }
        Emit(lb, cursor, lb.Count - cursor);
        return (string.Concat(lb), o.ToString(), t1, normalize, conflicts);
    }

    private static bool LooksBinary(byte[] bytes) => TextInspector.LooksBinary(bytes.AsSpan(0, Math.Min(bytes.Length, TextInspector.HeadBytes)));

    private static string Full(string root, string rel) => System.IO.Path.Combine(root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
}
