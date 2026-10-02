using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Diff;

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
        int n = baseLines.Count;
        var h1 = LineDiffer.Diff(baseLines, v1Lines);
        var h2 = LineDiffer.Diff(baseLines, v2Lines);

        // base→variant line map for each side (−1 where the base line was changed away by that side).
        var m1 = BuildMatchMap(h1, n);
        var m2 = BuildMatchMap(h2, n);

        // All change hunks from both sides, as half-open base ranges [oStart,oEnd), sorted; v1 before v2.
        var regions = new List<(int oStart, int oEnd, int side)>(h1.Count + h2.Count);
        foreach (var h in h1) regions.Add((h.OldStart - 1, h.OldStart - 1 + h.OldLines, 0));
        foreach (var h in h2) regions.Add((h.OldStart - 1, h.OldStart - 1 + h.OldLines, 1));
        regions.Sort((x, y) => x.oStart != y.oStart ? x.oStart.CompareTo(y.oStart) : x.side.CompareTo(y.side));

        var conflicts = new List<Conflict>();
        var clean = new List<CleanMerge>();

        int i = 0;
        while (i < regions.Count)
        {
            int rStart = regions[i].oStart, rEnd = regions[i].oEnd;
            i++;
            while (i < regions.Count && regions[i].oStart <= rEnd) // overlap OR abut ⇒ coalesce
            {
                rEnd = Math.Max(rEnd, regions[i].oEnd);
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

            if (v1Changed && v2Changed && !SeqEqual(v1Lines, v1s, len1, v2Lines, v2s, len2))
            {
                conflicts.Add(new Conflict(
                    path, rStart + 1, baseLen,
                    Op(baseLen, len1), v1s + 1, len1,
                    Op(baseLen, len2), v2s + 1, len2));
            }
            else if (v1Changed && !v2Changed)
            {
                clean.Add(new CleanMerge(path, "v1", Op(baseLen, len1), rStart + 1, baseLen, v1s + 1, len1));
            }
            else if (v2Changed && !v1Changed)
            {
                clean.Add(new CleanMerge(path, "v2", Op(baseLen, len2), rStart + 1, baseLen, v2s + 1, len2));
            }
            else // both changed identically ⇒ agreed edit, canonical side "v1"
            {
                clean.Add(new CleanMerge(path, "v1", Op(baseLen, len1), rStart + 1, baseLen, v1s + 1, len1));
            }
        }

        return new FileMergeResult(path, conflicts, clean);
    }

    /// <summary>Map each base line to its 0-based variant line, or −1 if the side changed it away.</summary>
    private static int[] BuildMatchMap(IReadOnlyList<Hunk> hunks, int n)
    {
        var map = new int[n];
        Array.Fill(map, -1);

        int basePos = 0, varPos = 0;
        foreach (var h in hunks)
        {
            int ob0 = h.OldStart - 1, nb0 = h.NewStart - 1;
            for (; basePos < ob0; basePos++, varPos++) // unchanged run before the hunk maps 1:1
                map[basePos] = varPos;
            basePos = ob0 + h.OldLines; // skip the base lines the hunk removed/replaced
            varPos = nb0 + h.NewLines;  // variant position resumes after the hunk's new content
        }
        for (; basePos < n; basePos++, varPos++) // trailing unchanged tail
            map[basePos] = varPos;

        return map;
    }

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
