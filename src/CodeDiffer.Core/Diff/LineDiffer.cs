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

    /// <summary>
    /// Edit-distance budget. The backtrack trace costs ~D² ints (D = number of differing lines), so 3000
    /// caps it near 36 MB whatever the file size. Past it the diff falls back to ONE replace over the
    /// differing middle (common prefix/suffix still matched) — still a correct diff, just not minimal.
    /// </summary>
    public const int DefaultMaxEditDistance = 3000;

    public static List<Hunk> Diff(string oldText, string newText)
        => Diff(LineText.SplitLines(oldText), LineText.SplitLines(newText));

    public static List<Hunk> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => Diff(a, b, DefaultMaxEditDistance, out _);

    /// <summary>Diff with an explicit edit-distance budget; <paramref name="coarse"/> = the budget was exceeded.</summary>
    public static List<Hunk> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b, int maxEditDistance, out bool coarse)
    {
        var moves = ShortestEdit(a, b, maxEditDistance);
        coarse = moves is null;
        return moves is null ? CoarseHunks(a, b) : BuildHunks(moves);
    }

    /// <summary>Over-budget fallback: match the common prefix and suffix, replace the middle as one hunk.</summary>
    private static List<Hunk> CoarseHunks(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int pre = 0;
        while (pre < a.Count && pre < b.Count && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Count - pre && suf < b.Count - pre && a[a.Count - 1 - suf] == b[b.Count - 1 - suf]) suf++;
        int del = a.Count - pre - suf, ins = b.Count - pre - suf;
        if (del == 0 && ins == 0) return [];
        var op = del > 0 && ins > 0 ? HunkOp.Replace : del > 0 ? HunkOp.Delete : HunkOp.Insert;
        return [new Hunk(op, del == 0 ? pre : pre + 1, del, pre + 1, ins)];
    }

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

    /// <summary>
    /// Myers O(ND) shortest edit script as a forward list of Equal/Delete/Insert moves, or null if the edit
    /// distance exceeds <paramref name="maxD"/>. The trace keeps only each step's live diagonals (k ∈ [-d, d],
    /// 2d+3 ints) instead of a full copy of V, so memory is O(D²), not O(D·(N+M)) — same moves, same output.
    /// </summary>
    private static List<Edit>? ShortestEdit(IReadOnlyList<string> a, IReadOnlyList<string> b, int maxD)
    {
        int n = a.Count, m = b.Count, max = n + m;
        var moves = new List<Edit>();
        if (max == 0) return moves;

        int off = max + 1; // one spare diagonal each side: step 0's backtrack reads diagonal +1
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();

        bool done = false;
        for (int d = 0; d <= max && !done; d++)
        {
            if (d > maxD) return null;
            trace.Add(v.AsSpan(off - d - 1, 2 * d + 3).ToArray()); // diagonals -d-1..d+1 (state after step d-1)
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
            // vd is the -d-1..d+1 slice, so diagonal j lives at vd[j + d + 1].
            int prevK = k == -d || (k != d && vd[k + d] < vd[k + d + 2]) ? k + 1 : k - 1;
            int prevX = vd[prevK + d + 1];
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
