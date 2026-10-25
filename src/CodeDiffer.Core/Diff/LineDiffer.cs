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
    /// Exact-trace budget. The classic forward Myers keeps a backtrack trace of ~D²/2 bits (D = number of
    /// differing lines), so 3000 caps it near 560 KB, and its search near D² steps. Past it, the linear-space (divide-and-conquer,
    /// middle-snake) Myers takes over: O(N+M) memory whatever D is, still a minimal diff. Small diffs never
    /// reach it, so their hunks are byte-identical to before (the Spawner contract tests depend on that).
    /// </summary>
    public const int DefaultMaxEditDistance = 3000;

    /// <summary>
    /// Floor of the work cap for the linear-space pass, in diagonal steps (≈ D²/2 for D differing lines, whatever the
    /// length). 2·10⁸ is a couple of seconds; a pathological input past the cap falls back to ONE replace over the
    /// differing middle — still a correct diff, flagged coarse. See <see cref="WorkCap"/>.
    /// </summary>
    public const long DefaultMaxWork = 200_000_000;

    /// <summary>Ceiling of the work cap: a few seconds before a diff gives up.</summary>
    public const long MaxWorkCeiling = 600_000_000;

    /// <summary>
    /// The work cap for a pair of N and M lines: room for a diff of up to half their lines ((N+M)²/8 steps), between
    /// <see cref="DefaultMaxWork"/> and <see cref="MaxWorkCeiling"/> (so half their lines only up to ~69,000 lines in all;
    /// past that, ~35,000 changed lines, flagged coarse). A fixed cap gave up on any diff of past ~20,000
    /// changed lines however long the files — 40,000 lines with every fourth edited went coarse (one conflict over the
    /// whole file in a 3-way merge) though the exact diff takes about as long; a pair mostly unlike still gives up.
    /// </summary>
    public static long WorkCap(long n, long m)
    {
        long t = n + m;
        return Math.Clamp(t > 100_000_000 ? long.MaxValue : t * t / 8, DefaultMaxWork, MaxWorkCeiling);
    }

    public static List<Hunk> Diff(string oldText, string newText)
        => Diff(LineText.SplitLines(oldText), LineText.SplitLines(newText));

    public static List<Hunk> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => Diff(a, b, DefaultMaxEditDistance, out _);

    /// <summary>
    /// The hunks of a whitespace-only change, or null: when both sides have as many lines and each line is the other's
    /// but for whitespace (<see cref="Compare.TextInspector.SameButWhitespace"/>, a line ending's CR aside), line i is line i
    /// and each run of lines that differ is one replace. Myers, among repeated lines, may pair a line with an identical
    /// one elsewhere instead (<c>x / ␠␠x</c> → <c>␠␠x / x</c>: a delete and an insert), hunks that change more than
    /// whitespace in a file whose only change is whitespace.
    /// </summary>
    public static List<Hunk>? WhitespaceAligned(IReadOnlyList<string> a, IReadOnlyList<string> b, Compare.StringRules rules = Compare.StringRules.Both)
    {
        if (a.Count != b.Count) return null;
        static string Line(string line) => line.EndsWith('\r') ? line[..^1] : line;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal) && !Compare.TextInspector.SameButWhitespace(Line(a[i]), Line(b[i]), rules))
                return null;
        var hunks = new List<Hunk>();
        for (int i = 0; i < a.Count;)
        {
            if (string.Equals(a[i], b[i], StringComparison.Ordinal)) { i++; continue; }
            int start = i;
            while (i < a.Count && !string.Equals(a[i], b[i], StringComparison.Ordinal)) i++;
            hunks.Add(new Hunk(HunkOp.Replace, start + 1, i - start, start + 1, i - start));
        }
        return hunks;
    }

    /// <summary>
    /// Diff with an explicit exact-trace budget: within it, classic Myers; past it, linear-space Myers; past
    /// <paramref name="maxWork"/> too (default <see cref="WorkCap"/>), one coarse replace (<paramref name="coarse"/> = true). Cancellable at every
    /// edit-distance step.
    /// </summary>
    public static List<Hunk> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b, int maxEditDistance, out bool coarse,
        long? maxWork = null, CancellationToken ct = default)
    {
        // One side is the other's prefix (an added or removed file, an append): every exact pass ends in the same
        // single hunk after the common lines, so skip them. Classic Myers was quadratic here.
        int pre = 0;
        while (pre < a.Count && pre < b.Count && a[pre] == b[pre]) pre++;
        if (pre == a.Count || pre == b.Count)
        {
            coarse = false;
            return CoarseHunks(a, b);
        }
        // The edit distance is at least the difference in length: past the budget, the exact pass would search
        // only to give up.
        var moves = (Math.Abs(a.Count - b.Count) > maxEditDistance ? null : ShortestEdit(a, b, maxEditDistance, ct))
                    ?? LinearSpace.Moves(a, b, maxWork ?? WorkCap(a.Count, b.Count), ct);
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
        /// <summary>
        /// A line with no copy in the other file can never be matched, so (as git's xdiff does) the search runs without
        /// them and they go back as deletes and inserts after: the same common lines, so still minimal. A file rewritten
        /// with mostly new lines (1,100 lines become 25,800: 25,000 new, 700 cut) was a diff of 26,000 changed lines,
        /// past the work cap, so one replace over the lot — a 3-way merge of it one conflict over the whole file where
        /// git's was 12 lines; without its new lines it is a few hundred.
        /// </summary>
        public static List<Edit>? Moves(IReadOnlyList<string> a, IReadOnlyList<string> b, long maxWork, CancellationToken ct)
        {
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int[] A = Intern(a, ids), B = Intern(b, ids);
            var inA = new bool[ids.Count];
            var inB = new bool[ids.Count];
            foreach (int id in A) inA[id] = true;
            foreach (int id in B) inB[id] = true;
            int[] ka = Kept(A, inB), kb = Kept(B, inA); // the indexes of the lines that can match
            int[] RA = Pick(A, ka), RB = Pick(B, kb);
            int max = RA.Length + RB.Length + 2;
            var vf = new int[2 * max + 2];
            var vb = new int[2 * max + 2];
            var moves = new List<Edit>(RA.Length + RB.Length);
            long work = 0;
            if (!Solve(RA, 0, RA.Length, RB, 0, RB.Length, vf, vb, moves, ref work, maxWork, ct)) return null;
            if (ka.Length == A.Length && kb.Length == B.Length) return moves;

            // The dropped lines back in place: those before a kept line go just before it, deletes first.
            var all = new List<Edit>(A.Length + B.Length);
            int i = 0, j = 0, p = 0, q = 0;
            foreach (var mv in moves)
            {
                if (mv != Edit.Insert) { for (; i < ka[p]; i++) all.Add(Edit.Delete); i++; p++; }
                if (mv != Edit.Delete) { for (; j < kb[q]; j++) all.Add(Edit.Insert); j++; q++; }
                all.Add(mv);
            }
            for (; i < A.Length; i++) all.Add(Edit.Delete);
            for (; j < B.Length; j++) all.Add(Edit.Insert);
            return all;
        }

        private static int[] Kept(int[] lines, bool[] inOther)
        {
            var r = new List<int>(lines.Length);
            for (int i = 0; i < lines.Length; i++) if (inOther[lines[i]]) r.Add(i);
            return [.. r];
        }

        private static int[] Pick(int[] lines, int[] at)
        {
            var r = new int[at.Length];
            for (int i = 0; i < at.Length; i++) r[i] = lines[at[i]];
            return r;
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
            int[] vf, int[] vb, List<Edit> moves, ref long work, long maxWork, CancellationToken ct)
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
                var snake = MiddleSnake(A, aLo, n, B, bLo, m, vf, vb, ref work, maxWork, ct);
                if (snake is not { } sn) return false;
                var (x, y, u, v) = sn;
                if (!Solve(A, aLo, aLo + x, B, bLo, bLo + y, vf, vb, moves, ref work, maxWork, ct)) return false;
                for (int i = x; i < u; i++) moves.Add(Edit.Equal);
                if (!Solve(A, aLo + u, aHi, B, bLo + v, bHi, vf, vb, moves, ref work, maxWork, ct)) return false;
            }
            for (int i = 0; i < suf; i++) moves.Add(Edit.Equal);
            return true;
        }

        /// <summary>The middle snake (x,y)→(u,v), relative to (aLo,bLo); null when the work cap is hit.</summary>
        private static (int, int, int, int)? MiddleSnake(int[] A, int aLo, int n, int[] B, int bLo, int m,
            int[] vf, int[] vb, ref long work, long maxWork, CancellationToken ct)
        {
            int delta = n - m;
            bool odd = (delta & 1) != 0;
            int dMax = (n + m + 1) / 2;
            int o = dMax + 1; // offset so diagonal k ∈ [-dMax-1, dMax+1] indexes ≥ 0
            vf[o + 1] = 0;
            vb[o + 1] = 0;
            for (int d = 0; d <= dMax; d++)
            {
                ct.ThrowIfCancellationRequested();
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

    /// <summary>A lower bound on the edit distance: the sum over distinct lines of the difference in their counts.</summary>
    private static long CountBound(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in a) counts[l] = counts.GetValueOrDefault(l) + 1;
        foreach (var l in b) counts[l] = counts.GetValueOrDefault(l) - 1;
        long bound = 0;
        foreach (var c in counts.Values) bound += Math.Abs(c);
        return bound;
    }

    /// <summary>
    /// Myers O(ND) shortest edit script as a forward list of Equal/Delete/Insert moves, or null if the edit
    /// distance exceeds <paramref name="maxD"/>. The backtrack needs, of each step's furthest reach on each diagonal, only
    /// which neighbour it came from (diagonal k+1, a line inserted, or k−1, one deleted): one bit per diagonal and step,
    /// so D²/2 bits (~560 KB at the 3,000 budget) where a copy of each step's diagonals took D² ints (~36 MB), the most a
    /// 3-way merge of a few thousand files allocated. The bits give the path's diagonal at every step, back from the end;
    /// replayed forward, each step's move and then its run of equal lines, it is the same path — same moves, same output.
    /// </summary>
    private static List<Edit>? ShortestEdit(IReadOnlyList<string> a, IReadOnlyList<string> b, int maxD, CancellationToken ct)
    {
        int n = a.Count, m = b.Count, max = n + m;
        if (max == 0) return [];

        int off = max + 1; // one spare diagonal each side: step 0 reads diagonal +1
        var v = Rent(2 * max + 3);
        var fromAbove = System.Buffers.ArrayPool<ulong>.Shared.Rent(64); // bit (d, k) at d(d+1)/2 + (k+d)/2
        try
        {
            Array.Clear(v, 0, 2 * max + 3);
            int last = -1;
            for (int d = 0; d <= max && last < 0; d++)
            {
                if (d > maxD) return null;
                // Halfway through the budget, check the bound the line counts give: every line one side has more
                // copies of than the other is a delete or an insert. Past the budget, the rest of the search is waste.
                if (d == maxD / 2 && maxD >= 64 && CountBound(a, b) > maxD) return null;
                ct.ThrowIfCancellationRequested();
                long at = (long)d * (d + 1) / 2;
                int words = (int)((at + d + 64) >> 6);
                if (words > fromAbove.Length)
                {
                    var grown = System.Buffers.ArrayPool<ulong>.Shared.Rent(Math.Max(2 * fromAbove.Length, words));
                    Array.Copy(fromAbove, grown, fromAbove.Length);
                    System.Buffers.ArrayPool<ulong>.Shared.Return(fromAbove);
                    fromAbove = grown;
                }
                for (int k = -d; k <= d; k += 2)
                {
                    bool down = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]);
                    long bit = at + ((k + d) >> 1);
                    if (down) fromAbove[bit >> 6] |= 1UL << (int)(bit & 63);
                    else fromAbove[bit >> 6] &= ~(1UL << (int)(bit & 63));
                    int x = down
                        ? v[off + k + 1]   // down move = insertion
                        : v[off + k - 1] + 1; // right move = deletion
                    int y = x - k;
                    while (x < n && y < m && a[x] == b[y]) { x++; y++; } // diagonal = equal
                    v[off + k] = x;
                    if (x >= n && y >= m) { last = d; break; }
                }
            }

            // The path's diagonal at each step, back from the end (n, m).
            var ks = new int[last + 1];
            for (int d = last, k = n - m; d >= 0; d--)
            {
                ks[d] = k;
                long bit = (long)d * (d + 1) / 2 + ((k + d) >> 1);
                k = ((fromAbove[bit >> 6] >> (int)(bit & 63)) & 1) != 0 ? k + 1 : k - 1;
            }
            // Forward along it: each step's move, then its equal lines (step 0 has only those).
            var moves = new List<Edit>((max + last) / 2);
            int px = 0, py = 0;
            for (int d = 0; d <= last; d++)
            {
                if (d > 0)
                {
                    if (ks[d - 1] == ks[d] + 1) { moves.Add(Edit.Insert); py++; }
                    else { moves.Add(Edit.Delete); px++; }
                }
                while (px < n && py < m && a[px] == b[py]) { moves.Add(Edit.Equal); px++; py++; }
            }
            System.Diagnostics.Debug.Assert(px == n && py == m);
            return moves;
        }
        finally
        {
            System.Buffers.ArrayPool<ulong>.Shared.Return(fromAbove);
            Return(v);
        }
    }

    /// <summary>A pooled buffer. One past <see cref="PooledInts"/> is a giant file's: allocated, and left to the GC after,
    /// so the pool (one per thread and core) never holds megabytes between diffs.</summary>
    private static int[] Rent(int length) => length > PooledInts ? new int[length] : System.Buffers.ArrayPool<int>.Shared.Rent(length);

    private static void Return(int[] buffer)
    {
        if (buffer.Length <= PooledInts) System.Buffers.ArrayPool<int>.Shared.Return(buffer);
    }

    /// <summary>1 MB of ints: the V array of two files of 130,000 lines between them.</summary>
    private const int PooledInts = 1 << 18;
}
