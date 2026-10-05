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
    /// Exact-trace budget. The classic forward Myers keeps a backtrack trace of ~D² ints (D = number of
    /// differing lines), so 3000 caps it near 36 MB. Past it, the linear-space (divide-and-conquer,
    /// middle-snake) Myers takes over: O(N+M) memory whatever D is, still a minimal diff. Small diffs never
    /// reach it, so their hunks are byte-identical to before (the Spawner contract tests depend on that).
    /// </summary>
    public const int DefaultMaxEditDistance = 3000;

    /// <summary>
    /// Work cap for the linear-space pass, in diagonal steps (≈ (N+M)·D). 2·10⁹ is a few seconds for real
    /// files; a pathological input past it falls back to ONE replace over the differing middle — still a
    /// correct diff, flagged coarse.
    /// </summary>
    public const long DefaultMaxWork = 2_000_000_000;

    public static List<Hunk> Diff(string oldText, string newText)
        => Diff(LineText.SplitLines(oldText), LineText.SplitLines(newText));

    public static List<Hunk> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => Diff(a, b, DefaultMaxEditDistance, out _);

    /// <summary>
    /// Diff with an explicit exact-trace budget: within it, classic Myers; past it, linear-space Myers; past
    /// <paramref name="maxWork"/> too, one coarse replace (<paramref name="coarse"/> = true).
    /// </summary>
    public static List<Hunk> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b, int maxEditDistance, out bool coarse,
        long maxWork = DefaultMaxWork)
    {
        var moves = ShortestEdit(a, b, maxEditDistance) ?? LinearSpace.Moves(a, b, maxWork);
        coarse = moves is null;
        return moves is null ? CoarseHunks(a, b) : BuildHunks(moves);
    }

    /// <summary>
    /// Linear-space Myers (Myers 1986 §4b — the refinement git's xdiff uses): find the "middle snake" of the
    /// optimal path by searching forward and backward at once, then recurse on the two halves. Memory is two
    /// V arrays of O(N+M) plus the move list; lines are interned to ints so the inner loop compares integers.
    /// Common prefix/suffix are trimmed at every level, which (with the empty-side base cases) guarantees each
    /// recursion shrinks.
    /// </summary>
    private static class LinearSpace
    {
        public static List<Edit>? Moves(IReadOnlyList<string> a, IReadOnlyList<string> b, long maxWork)
        {
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] A = Intern(a, ids), B = Intern(b, ids);
            int max = A.Length + B.Length + 2;
            var vf = new int[2 * max + 2];
            var vb = new int[2 * max + 2];
            var moves = new List<Edit>(A.Length + B.Length);
            long work = 0;
            return Solve(A, 0, A.Length, B, 0, B.Length, vf, vb, moves, ref work, maxWork) ? moves : null;
        }

        private static int[] Intern(IReadOnlyList<string> lines, Dictionary<string, int> ids)
        {
            var r = new int[lines.Count];
            for (int i = 0; i < r.Length; i++)
            {
                if (!ids.TryGetValue(lines[i], out var id)) ids[lines[i]] = id = ids.Count;
                r[i] = id;
            }
            return r;
        }

        private static bool Solve(int[] A, int aLo, int aHi, int[] B, int bLo, int bHi,
            int[] vf, int[] vb, List<Edit> moves, ref long work, long maxWork)
        {
            int pre = 0;
            while (aLo + pre < aHi && bLo + pre < bHi && A[aLo + pre] == B[bLo + pre]) pre++;
            for (int i = 0; i < pre; i++) moves.Add(Edit.Equal);
            aLo += pre; bLo += pre;
            int suf = 0;
            while (aHi - suf > aLo && bHi - suf > bLo && A[aHi - 1 - suf] == B[bHi - 1 - suf]) suf++;
            aHi -= suf; bHi -= suf;

            int n = aHi - aLo, m = bHi - bLo;
            if (n == 0) { for (int i = 0; i < m; i++) moves.Add(Edit.Insert); }
            else if (m == 0) { for (int i = 0; i < n; i++) moves.Add(Edit.Delete); }
            else
            {
                var snake = MiddleSnake(A, aLo, n, B, bLo, m, vf, vb, ref work, maxWork);
                if (snake is not { } sn) return false;
                var (x, y, u, v) = sn;
                if (!Solve(A, aLo, aLo + x, B, bLo, bLo + y, vf, vb, moves, ref work, maxWork)) return false;
                for (int i = x; i < u; i++) moves.Add(Edit.Equal);
                if (!Solve(A, aLo + u, aHi, B, bLo + v, bHi, vf, vb, moves, ref work, maxWork)) return false;
            }
            for (int i = 0; i < suf; i++) moves.Add(Edit.Equal);
            return true;
        }

        /// <summary>The middle snake (x,y)→(u,v), relative to (aLo,bLo); null when the work cap is hit.</summary>
        private static (int, int, int, int)? MiddleSnake(int[] A, int aLo, int n, int[] B, int bLo, int m,
            int[] vf, int[] vb, ref long work, long maxWork)
        {
            int delta = n - m;
            bool odd = (delta & 1) != 0;
            int dMax = (n + m + 1) / 2;
            int o = dMax + 1; // offset so diagonal k ∈ [-dMax-1, dMax+1] indexes ≥ 0
            vf[o + 1] = 0;
            vb[o + 1] = 0;
            for (int d = 0; d <= dMax; d++)
            {
                // Forward search: furthest-reaching path on each diagonal k = x - y.
                for (int k = -d; k <= d; k += 2)
                {
                    int x = k == -d || (k != d && vf[o + k - 1] < vf[o + k + 1]) ? vf[o + k + 1] : vf[o + k - 1] + 1;
                    int y = x - k, x0 = x, y0 = y;
                    while (x < n && y < m && A[aLo + x] == B[bLo + y]) { x++; y++; }
                    vf[o + k] = x;
                    work += x - x0 + 1;
                    int c = delta - k; // the backward diagonal that meets forward diagonal k
                    if (odd && c >= -(d - 1) && c <= d - 1 && x + vb[o + c] >= n)
                        return (x0, y0, x, y);
                }
                // Backward search, in reversed coordinates (x counts from the end of A).
                for (int k = -d; k <= d; k += 2)
                {
                    int x = k == -d || (k != d && vb[o + k - 1] < vb[o + k + 1]) ? vb[o + k + 1] : vb[o + k - 1] + 1;
                    int y = x - k, x0 = x, y0 = y;
                    while (x < n && y < m && A[aLo + n - 1 - x] == B[bLo + m - 1 - y]) { x++; y++; }
                    vb[o + k] = x;
                    work += x - x0 + 1;
                    int c = delta - k;
                    if (!odd && c >= -d && c <= d && x + vf[o + c] >= n)
                        return (n - x, m - y, n - x0, m - y0);
                }
                if (work > maxWork) return null;
            }
            return null; // unreachable for a well-formed input
        }
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
