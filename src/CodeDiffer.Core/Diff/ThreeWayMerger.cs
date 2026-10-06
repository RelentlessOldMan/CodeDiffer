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
    public static FileMergeResult Merge(string path, string baseText, string v1Text, string v2Text)
        => Merge(path, LineText.SplitLines(baseText), LineText.SplitLines(v1Text), LineText.SplitLines(v2Text));

    public static FileMergeResult Merge(
        string path, IReadOnlyList<string> baseLines, IReadOnlyList<string> v1Lines, IReadOnlyList<string> v2Lines)
    {
        var conflicts = new List<Conflict>();
        var clean = new List<CleanMerge>();
        foreach (var r in Regions(baseLines, v1Lines, v2Lines))
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
    public static List<MergeRegion> Regions(
        IReadOnlyList<string> baseLines, IReadOnlyList<string> v1Lines, IReadOnlyList<string> v2Lines)
    {
        int n = baseLines.Count;
        var h1 = LineDiffer.Diff(baseLines, v1Lines);
        var h2 = LineDiffer.Diff(baseLines, v2Lines);

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
