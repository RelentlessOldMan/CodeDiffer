using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Diff;

/// <summary>How a diff3 region was changed: by one side only, by both identically, or by both differently.</summary>
public enum RegionKind { V1Only, V2Only, Agreed, Conflict }

/// <summary>One diff3 region: 0-based start + length of its span in base, V1 and V2.</summary>
public readonly record struct MergeRegion(RegionKind Kind, int BaseStart0, int BaseLines, int V1Start0, int V1Lines, int V2Start0, int V2Lines);

/// <summary>One file's 3-way decomposition: the conflict regions and the cleanly-merged regions.</summary>
public sealed record FileMergeResult(string Path, IReadOnlyList<Conflict> Conflicts, IReadOnlyList<CleanMerge> CleanMerges);

/// <summary>
/// The native 3-way merge: classic diff3 over base B, variant V1, variant V2, producing the exact
/// Conflict / CleanMerge decomposition the CodeSpawner conflict contract defines. Built on the same
/// Myers line differ as the 2-way path (B→V1 and B→V2 hunks), so coordinates agree with the deltas.
///
/// Algorithm (diff3): from each side's hunks build a base→variant line map; the base lines NOT touched
/// by a side are "matched" by it, and a base line matched by BOTH sides is STABLE. Changed hunks from
/// either side are swept left-to-right and any that overlap or abut coalesce into one REGION (the locked
/// union-span rule; a stable line untouched by both terminates a region). Per region:
///   • exactly one side changed (other == base)        → CleanMerge from the changed side
///   • both changed to byte-identical content           → CleanMerge, side "v1" canonical (agreed edit)
///   • both changed to differing content                → Conflict
///
/// Coordinates follow the contract: baseStart = region's 1-based base start (= the shared 2-way insert
/// anchor for a zero-width region), baseLines = base span; each side's new* references ITS variant tree,
/// a delete side carries newLines 0 at the 1-based line where the cut begins. Region-level op per side:
/// baseLines 0 ⇒ insert, side-lines 0 ⇒ delete, else replace.
/// </summary>
public static class ThreeWayMerger
{
    public static FileMergeResult Merge(string path, string baseText, string v1Text, string v2Text, CancellationToken ct = default)
        => Merge(path, LineText.SplitLines(baseText), LineText.SplitLines(v1Text), LineText.SplitLines(v2Text), ct);

    public static FileMergeResult Merge(
        string path, IReadOnlyList<string> baseLines, IReadOnlyList<string> v1Lines, IReadOnlyList<string> v2Lines, CancellationToken ct = default)
    {
        var conflicts = new List<Conflict>();
        var clean = new List<CleanMerge>();
        foreach (var r in Regions(baseLines, v1Lines, v2Lines, ct))
        {
            int baseLen = r.BaseLines, len1 = r.V1Lines, len2 = r.V2Lines;
            // baseStart: an insert region (baseLen 0) anchors on the line after which it goes (= BaseStart0);
            // a replace/delete region on its first affected line (= BaseStart0+1).
            int baseStart = baseLen == 0 ? r.BaseStart0 : r.BaseStart0 + 1;
            switch (r.Kind)
            {
                case RegionKind.Conflict:
                    conflicts.Add(new Conflict(
                        path, baseStart, baseLen,
                        Op(baseLen, len1), r.V1Start0 + 1, len1,
                        Op(baseLen, len2), r.V2Start0 + 1, len2));
                    break;
                case RegionKind.V2Only:
                    clean.Add(new CleanMerge(path, "v2", Op(baseLen, len2), baseStart, baseLen, r.V2Start0 + 1, len2));
                    break;
                default: // V1Only, or both changed identically ⇒ agreed edit, canonical side "v1"
                    clean.Add(new CleanMerge(path, "v1", Op(baseLen, len1), baseStart, baseLen, r.V1Start0 + 1, len1));
                    break;
            }
        }
        return new FileMergeResult(path, conflicts, clean);
    }

    /// <summary>
    /// The diff3 regions, in base order: each a maximal coalesced span of change from either side, with
    /// its 0-based half-open spans in base, V1 and V2. Lines between regions are identical in all three.
    /// </summary>
    /// <param name="ct">Checked as the two line diffs run (each can take seconds on files of 100,000s of lines).</param>
    public static List<MergeRegion> Regions(
        IReadOnlyList<string> baseLines, IReadOnlyList<string> v1Lines, IReadOnlyList<string> v2Lines, CancellationToken ct = default)
    {
        var d1 = LineDiffer.Diff(baseLines, v1Lines, LineDiffer.DefaultMaxEditDistance, out _, ct: ct);
        var d2 = LineDiffer.Diff(baseLines, v2Lines, LineDiffer.DefaultMaxEditDistance, out _, ct: ct);
        var h1 = InPlace(d1, baseLines, v1Lines);
        var h2 = InPlace(d2, baseLines, v2Lines);
        var regions = Regions(baseLines, v1Lines, v2Lines, h1, h2);
        if (ReferenceEquals(h1, d1) && ReferenceEquals(h2, d2)) return regions;

        // Where the lines as written show one side already holding every change of the other (merging a merge again, say)
        // and the edits in place don't merge to that side, the lines read two ways and nothing tells which: v1 `}; }` and
        // v2 `}` from `} }` is an edit and a cut (both kept: `};`) or v1 already holding v2's cut (v1). Taking either would
        // lose an edit or make one twice without a word, so where the two readings part they are one conflict, from the
        // first region the lines as written don't have to the last (each side's edits in it, not one side's alone).
        var raw = Regions(baseLines, v1Lines, v2Lines, d1, d2);
        int holds = Holder(raw);
        if (holds == 0 || (Holder(regions) & holds) != 0) return regions;
        var agreed = new HashSet<MergeRegion>(raw);
        int first = regions.FindIndex(r => !agreed.Contains(r)), last = regions.FindLastIndex(r => !agreed.Contains(r));
        var (f, l) = (regions[first], regions[last]);
        var span = new MergeRegion(RegionKind.Conflict, f.BaseStart0, l.BaseStart0 + l.BaseLines - f.BaseStart0,
            f.V1Start0, l.V1Start0 + l.V1Lines - f.V1Start0, f.V2Start0, l.V2Start0 + l.V2Lines - f.V2Start0);
        return [.. regions.Take(first), span, .. regions.Skip(last + 1)];
    }

    /// <summary>Which side a conflict-free merge is: 1 if it is V1 (V2 changed nothing V1 lacks), 2 if V2, 3 both, else 0.</summary>
    private static int Holder(List<MergeRegion> regions)
    {
        if (regions.Any(r => r.Kind == RegionKind.Conflict)) return 0;
        return (regions.Any(r => r.Kind == RegionKind.V2Only) ? 0 : 1) | (regions.Any(r => r.Kind == RegionKind.V1Only) ? 0 : 2);
    }

    private static List<MergeRegion> Regions(IReadOnlyList<string> baseLines, IReadOnlyList<string> v1Lines,
        IReadOnlyList<string> v2Lines, List<Hunk> h1, List<Hunk> h2)
    {
        int n = baseLines.Count;

        // base→variant line map for each side (−1 where the base line was changed away by that side).
        var m1 = BuildMatchMap(h1, n);
        var m2 = BuildMatchMap(h2, n);

        // All change hunks from both sides, as half-open base ranges [oStart,oEnd), sorted; v1 before v2.
        // An insert (OldLines 0) sits in the gap AFTER base line OldStart, so its base position is OldStart;
        // replace/delete start at OldStart-1 (0-based first affected line).
        var spans = new List<(int oStart, int oEnd, int side)>(h1.Count + h2.Count);
        foreach (var h in h1) { int s = BaseStart0(h); spans.Add((s, s + h.OldLines, 0)); }
        foreach (var h in h2) { int s = BaseStart0(h); spans.Add((s, s + h.OldLines, 1)); }
        spans.Sort((x, y) => x.oStart != y.oStart ? x.oStart.CompareTo(y.oStart) : x.side.CompareTo(y.side));

        var regions = new List<MergeRegion>();
        int i = 0;
        while (i < spans.Count)
        {
            int rStart = spans[i].oStart, rEnd = spans[i].oEnd;
            i++;
            while (i < spans.Count && spans[i].oStart <= rEnd) // overlap OR abut ⇒ coalesce
            {
                rEnd = Math.Max(rEnd, spans[i].oEnd);
                i++;
            }

            // Region boundaries are stable lines (matched by both sides), so the maps give exact spans.
            int v1s = rStart == 0 ? 0 : m1[rStart - 1] + 1;
            int v1e = rEnd == n ? v1Lines.Count : m1[rEnd];
            int v2s = rStart == 0 ? 0 : m2[rStart - 1] + 1;
            int v2e = rEnd == n ? v2Lines.Count : m2[rEnd];

            int baseLen = rEnd - rStart, len1 = v1e - v1s, len2 = v2e - v2s;
            bool v1Changed = !SeqEqual(v1Lines, v1s, len1, baseLines, rStart, baseLen);
            bool v2Changed = !SeqEqual(v2Lines, v2s, len2, baseLines, rStart, baseLen);
            var kind = v1Changed && v2Changed
                ? SeqEqual(v1Lines, v1s, len1, v2Lines, v2s, len2) ? RegionKind.Agreed : RegionKind.Conflict
                : v1Changed ? RegionKind.V1Only
                : v2Changed ? RegionKind.V2Only
                : RegionKind.Agreed; // neither differs from base (cannot arise from real hunks); harmless
            regions.Add(new MergeRegion(kind, rStart, baseLen, v1s, len1, v2s, len2));
        }
        return regions;
    }

    /// <summary>
    /// A side's hunks with each line edited in place among identical lines a replace where it stands. In a run of
    /// identical lines (a block of <c>acc = …;</c>, a column of <c>}</c>) Myers — git too — writes an edited line as an
    /// insert where it is and a delete of one of the run's copies wherever the search met it, often the run's end: as
    /// minimal as the replace, but two sides editing the same line then show their inserts and deletes apart and merge
    /// "cleanly", both edits side by side. Every copy in a run is the same line, so which one goes is free: each delete
    /// moves to an insert in its run (nearest first), and the pair is the replace of the line it stands on. The same for
    /// a run of identical new lines with one changed into it. As many lines kept as before: still a minimal diff.
    /// Only a line that is an edit of the run's line pairs (<see cref="Resembles"/>): an unrelated line inserted into a run
    /// while another copy is cut is an insert and a delete, as written.
    /// </summary>
    internal static List<Hunk> InPlace(List<Hunk> hunks, IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (hunks.Count < 2) return hunks; // a delete and an insert apart are two hunks
        var map = BuildMatchMap(hunks, a.Count);
        bool moved = PairRuns(map, a, b);
        var inv = Invert(map, b.Count);
        moved |= PairRuns(inv, b, a);
        return moved ? HunksOf(Invert(inv, a.Count), a.Count, b.Count) : hunks;
    }

    /// <summary>Whether <paramref name="line"/> reads as an edit of the run line <paramref name="run"/>: past indentation,
    /// what the two start and end with in common covers at least half of the run line (<c>}</c> → <c>};</c>,
    /// <c>acc = 1;</c> → <c>acc = 2;</c>, a comment appended). A blank run line resembles nothing.</summary>
    internal static bool Resembles(string run, string line)
    {
        var r = run.AsSpan().Trim();
        var s = line.AsSpan().Trim();
        if (r.IsEmpty) return false;
        int p = r.CommonPrefixLength(s);
        int lim = Math.Min(r.Length, s.Length) - p, q = 0;
        while (q < lim && r[r.Length - 1 - q] == s[s.Length - 1 - q]) q++;
        return 2 * (p + q) >= r.Length;
    }

    /// <summary>Within each run of identical lines of <paramref name="a"/>, moves its deletes (lines mapped to −1) to the
    /// lines of <paramref name="b"/> inserted between its kept lines that are edits of the run's line. True if any moved.</summary>
    private static bool PairRuns(int[] map, IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        bool moved = false;
        for (int r0 = 0; r0 < a.Count;)
        {
            int r1 = r0 + 1;
            while (r1 < a.Count && string.Equals(a[r1], a[r0], StringComparison.Ordinal)) r1++;
            string line = a[r0];
            if (r1 - r0 > 1) moved |= PairRun(map, r0, r1, b, j => Resembles(line, b[j]));
            r0 = r1;
        }
        return moved;
    }

    private static bool PairRun(int[] map, int r0, int r1, IReadOnlyList<string> b, Func<int, bool> pairs)
    {
        int bCount = b.Count;
        // The run's kept lines (their b lines in order) and the deletes in each gap around them: gap k is before kept k.
        var kept = new List<int>();
        var dels = new List<int> { 0 };
        for (int i = r0; i < r1; i++)
            if (map[i] >= 0) { kept.Add(map[i]); dels.Add(0); }
            else dels[^1]++;
        int c = kept.Count;
        if (c == 0 || c == r1 - r0) return false;

        // The inserts in each gap a delete could pair with: those that are edits of the run's line. The first and last
        // gaps share theirs with the lines deleted just outside the run (up to the nearest kept line), which take the
        // gap's inserts nearest them (a replace pairs its lines in order).
        int p = r0 - 1, q = r1;
        while (p >= 0 && map[p] < 0) p--;
        while (q < map.Length && map[q] < 0) q++;
        int Room(int lo, int hi)
        {
            int n = 0;
            for (int j = lo; j < hi; j++) if (pairs(j)) n++;
            return n;
        }
        var room = new int[c + 1];
        room[0] = Room((p >= 0 ? map[p] + 1 : 0) + (r0 - p - 1), kept[0]);
        for (int k = 1; k < c; k++) room[k] = Room(kept[k - 1] + 1, kept[k]);
        room[c] = Room(kept[c - 1] + 1, (q < map.Length ? map[q] : bCount) - (q - r1));

        // Deletes beyond their gap's inserts are free; inserts beyond their gap's deletes want one. Pair the nearest
        // free delete and wanting gap, again and again.
        var free = new List<(int Gap, int Count)>();
        var want = new List<(int Gap, int Count)>();
        var count = new int[c + 1];
        for (int k = 0; k <= c; k++)
        {
            count[k] = dels[k];
            if (dels[k] > room[k]) free.Add((k, dels[k] - room[k]));
            else if (dels[k] < room[k]) want.Add((k, room[k] - dels[k]));
        }
        if (free.Count == 0 || want.Count == 0 || free.Count + want.Count > 4096) return false;
        while (free.Count > 0 && want.Count > 0)
        {
            int bf = 0, bw = 0;
            for (int f = 0, w = 0; f < free.Count && w < want.Count;) // the closest pair is adjacent in gap order
            {
                if (Math.Abs(free[f].Gap - want[w].Gap) < Math.Abs(free[bf].Gap - want[bw].Gap)) (bf, bw) = (f, w);
                if (free[f].Gap < want[w].Gap) f++; else w++;
            }
            int n = Math.Min(free[bf].Count, want[bw].Count);
            count[free[bf].Gap] -= n;
            count[want[bw].Gap] += n;
            if ((free[bf] = (free[bf].Gap, free[bf].Count - n)).Count == 0) free.RemoveAt(bf);
            if ((want[bw] = (want[bw].Gap, want[bw].Count - n)).Count == 0) want.RemoveAt(bw);
        }

        int at = r0;
        for (int k = 0; k <= c; k++)
        {
            for (int x = 0; x < count[k]; x++) map[at++] = -1;
            if (k < c) map[at++] = kept[k];
        }
        return true;
    }

    private static int[] Invert(int[] map, int count)
    {
        var inv = new int[count];
        Array.Fill(inv, -1);
        for (int i = 0; i < map.Length; i++)
            if (map[i] >= 0) inv[map[i]] = i;
        return inv;
    }

    /// <summary>The hunks of a line map, as <see cref="LineDiffer"/> writes them.</summary>
    private static List<Hunk> HunksOf(int[] map, int n, int m)
    {
        var hunks = new List<Hunk>();
        int pi = -1, pj = -1;
        for (int i = 0; i <= n; i++)
        {
            int j = i < n ? map[i] : m;
            if (j < 0) continue;
            int del = i - pi - 1, ins = j - pj - 1;
            if (del > 0 || ins > 0)
                hunks.Add(new Hunk(del > 0 && ins > 0 ? HunkOp.Replace : del > 0 ? HunkOp.Delete : HunkOp.Insert,
                    del == 0 ? pi + 1 : pi + 2, del, pj + 2, ins));
            (pi, pj) = (i, j);
        }
        return hunks;
    }

    /// <summary>Map each base line to its 0-based variant line, or −1 if the side changed it away.</summary>
    private static int[] BuildMatchMap(IReadOnlyList<Hunk> hunks, int n)
    {
        var map = new int[n];
        Array.Fill(map, -1);

        int basePos = 0, varPos = 0;
        foreach (var h in hunks)
        {
            int ob0 = BaseStart0(h), nb0 = h.NewStart - 1;
            for (; basePos < ob0; basePos++, varPos++) // unchanged run before the hunk maps 1:1
                map[basePos] = varPos;
            basePos = ob0 + h.OldLines; // skip the base lines the hunk removed/replaced (insert: none)
            varPos = nb0 + h.NewLines;  // variant position resumes after the hunk's new content
        }
        for (; basePos < n; basePos++, varPos++) // trailing unchanged tail
            map[basePos] = varPos;

        return map;
    }

    /// <summary>0-based base position of a hunk: an insert sits in the gap after line OldStart; a
    /// replace/delete starts at the first affected line (OldStart-1).</summary>
    private static int BaseStart0(Hunk h) => h.OldLines == 0 ? h.OldStart : h.OldStart - 1;

    private static HunkOp Op(int baseLines, int sideLines)
        => baseLines == 0 ? HunkOp.Insert : sideLines == 0 ? HunkOp.Delete : HunkOp.Replace;

    private static bool SeqEqual(
        IReadOnlyList<string> a, int aStart, int aLen, IReadOnlyList<string> b, int bStart, int bLen)
    {
        if (aLen != bLen) return false;
        for (int k = 0; k < aLen; k++)
            if (!string.Equals(a[aStart + k], b[bStart + k], StringComparison.Ordinal))
                return false;
        return true;
    }
}
