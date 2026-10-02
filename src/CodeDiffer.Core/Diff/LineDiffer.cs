using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Diff;

/// <summary>
/// Line-level diff via Myers' O(ND) algorithm (the algorithm git uses), producing coalesced
/// unified-diff hunks in CodeDiffer's/CodeSpawner's shared convention (1-based coords):
///   • a maximal run that both sides change        → Replace(oldStart, del, newStart, ins)
///   • a run only the new side adds                 → Insert(oldStart, 0, newStart, ins)
///   • a run only the old side drops                → Delete(oldStart, del, newStart, 0)
/// Because positions are tracked on BOTH sides, a pure content edit (equal counts, no prior shift)
/// yields Replace(start, n, start, n) — byte-identical to CodeSpawner's Coalesce — so a verify can
/// assert hunk-equality against the delta manifest, not just the digest.
///
/// Hunks are coordinate-only ground truth (no text), matching the manifest. A real patch-applicable
/// unified diff (with +/- lines) is rendered on demand from the hunks + the two files.
/// </summary>
public static class LineDiffer
{
    private enum Edit { Equal, Delete, Insert }

    public static List<Hunk> Diff(string oldText, string newText)
        => Diff(LineText.SplitLines(oldText), LineText.SplitLines(newText));

    public static List<Hunk> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => BuildHunks(ShortestEdit(a, b));

    private static List<Hunk> BuildHunks(List<Edit> moves)
    {
        var hunks = new List<Hunk>();
        int oi = 0, ni = 0; // 0-based consumed counts in old / new
        int i = 0;
        while (i < moves.Count)
        {
            if (moves[i] == Edit.Equal) { oi++; ni++; i++; continue; }

            int oldStart0 = oi, newStart0 = ni, del = 0, ins = 0;
            while (i < moves.Count && moves[i] != Edit.Equal)
            {
                if (moves[i] == Edit.Delete) { del++; oi++; }
                else { ins++; ni++; }
                i++;
            }
            var op = del > 0 && ins > 0 ? HunkOp.Replace : del > 0 ? HunkOp.Delete : HunkOp.Insert;
            // Insert anchors on the line AFTER WHICH content is added (oldStart0 base lines precede it) —
            // the unified-diff / CodeSpawner convention. Replace/delete anchor on the first affected line.
            int oldStart = del == 0 ? oldStart0 : oldStart0 + 1;
            hunks.Add(new Hunk(op, oldStart, del, newStart0 + 1, ins));
        }
        return hunks;
    }

    /// <summary>Myers O(ND) shortest edit script as a forward list of Equal/Delete/Insert moves.</summary>
    private static List<Edit> ShortestEdit(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count, m = b.Count, max = n + m;
        var moves = new List<Edit>();
        if (max == 0) return moves;

        int off = max;
        var v = new int[2 * max + 1];
        var trace = new List<int[]>();

        bool done = false;
        for (int d = 0; d <= max && !done; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1])
                    ? v[off + k + 1]   // down move = insertion
                    : v[off + k - 1] + 1; // right move = deletion
                int y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; } // diagonal = equal
                v[off + k] = x;
                if (x >= n && y >= m) { done = true; break; }
            }
        }

        // Backtrack through the saved traces to recover the moves (collected reversed, then flipped).
        int px = n, py = m;
        for (int d = trace.Count - 1; d >= 0; d--)
        {
            var vd = trace[d];
            int k = px - py;
            int prevK = k == -d || (k != d && vd[off + k - 1] < vd[off + k + 1]) ? k + 1 : k - 1;
            int prevX = vd[off + prevK];
            int prevY = prevX - prevK;

            while (px > prevX && py > prevY) { moves.Add(Edit.Equal); px--; py--; }
            if (d > 0)
            {
                moves.Add(px == prevX ? Edit.Insert : Edit.Delete);
                px = prevX;
                py = prevY;
            }
        }

        moves.Reverse();
        return moves;
    }
}
