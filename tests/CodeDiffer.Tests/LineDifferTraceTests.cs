using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The exact Myers pass keeps one bit per diagonal and step where it kept a copy of each step's diagonals: the hunks must be
/// the very ones the copied trace gave (the 3-way merge and the Spawner contract tests read them), and a diff of a few
/// thousand changed lines must not allocate the ~36 MB that trace took.
/// </summary>
public class LineDifferTraceTests
{
    private enum Move { Equal, Delete, Insert }

    /// <summary>The pass as it was: a copy of diagonals -d-1..d+1 at every step, backtracked.</summary>
    private static List<Move> CopiedTrace(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count, m = b.Count, max = n + m, off = max + 1;
        var moves = new List<Move>();
        if (max == 0) return moves;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        bool done = false;
        for (int d = 0; d <= max && !done; d++)
        {
            trace.Add(v.AsSpan(off - d - 1, 2 * d + 3).ToArray());
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) { done = true; break; }
            }
        }
        int px = n, py = m;
        for (int d = trace.Count - 1; d >= 0; d--)
        {
            var vd = trace[d];
            int k = px - py;
            int prevK = k == -d || (k != d && vd[k + d] < vd[k + d + 2]) ? k + 1 : k - 1;
            int prevX = vd[prevK + d + 1], prevY = prevX - prevK;
            while (px > prevX && py > prevY) { moves.Add(Move.Equal); px--; py--; }
            if (d > 0) { moves.Add(px == prevX ? Move.Insert : Move.Delete); px = prevX; py = prevY; }
        }
        moves.Reverse();
        return moves;
    }

    private static List<Hunk> Hunks(List<Move> moves)
    {
        var hunks = new List<Hunk>();
        int oi = 0, ni = 0, i = 0;
        while (i < moves.Count)
        {
            if (moves[i] == Move.Equal) { oi++; ni++; i++; continue; }
            int o0 = oi, n0 = ni, del = 0, ins = 0;
            for (; i < moves.Count && moves[i] != Move.Equal; i++)
                if (moves[i] == Move.Delete) { del++; oi++; } else { ins++; ni++; }
            hunks.Add(new Hunk(del > 0 && ins > 0 ? HunkOp.Replace : del > 0 ? HunkOp.Delete : HunkOp.Insert,
                del == 0 ? o0 : o0 + 1, del, n0 + 1, ins));
        }
        return hunks;
    }

    private static string[] Lines(Random rng, int count, int alphabet) => [.. Enumerable.Range(0, count).Select(_ => "L" + rng.Next(alphabet))];

    private static string[] Mutate(Random rng, string[] a, int edits, int alphabet)
    {
        var b = a.ToList();
        for (int e = 0; e < edits; e++)
        {
            int at = rng.Next(b.Count + 1);
            switch (rng.Next(3))
            {
                case 0: b.Insert(at, "N" + rng.Next(alphabet)); break;
                case 1: if (b.Count > 0) b.RemoveAt(Math.Min(at, b.Count - 1)); break;
                default: if (b.Count > 0) b[Math.Min(at, b.Count - 1)] = "R" + rng.Next(alphabet); break;
            }
        }
        return [.. b];
    }

    [Fact]
    public void Hunks_AreTheCopiedTraces_OnRandomInputs()
    {
        var rng = new Random(20261025);
        for (int trial = 0; trial < 600; trial++)
        {
            int alphabet = rng.Next(1, 14); // small alphabets: many identical lines, many equally short paths to choose among
            var a = Lines(rng, rng.Next(0, 120), alphabet);
            var b = rng.Next(4) == 0 ? Lines(rng, rng.Next(0, 120), alphabet) : Mutate(rng, a, rng.Next(0, 40), alphabet);
            var got = LineDiffer.Diff(a, b, int.MaxValue, out bool coarse);
            Assert.False(coarse);
            Assert.Equal(Hunks(CopiedTrace(a, b)), got);
        }
    }

    [Fact]
    public void ADiffOfThousandsOfChangedLines_AllocatesNoTrace()
    {
        // ~2,400 changed lines in 6,000, within the exact budget: the copied trace was ~23 MB.
        var rng = new Random(5);
        var a = Enumerable.Range(0, 6_000).Select(i => $"line {i}\n").ToArray();
        var b = a.Select(l => rng.Next(5) == 0 ? "edited " + l : l).ToArray();
        LineDiffer.Diff(a, b, LineDiffer.DefaultMaxEditDistance, out _); // the pool's buffers, rented once
        long before = GC.GetAllocatedBytesForCurrentThread();
        var hunks = LineDiffer.Diff(a, b, LineDiffer.DefaultMaxEditDistance, out bool coarse);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(coarse);
        Assert.Equal(b, HunkApplier.Reconstruct(a, b, hunks));
        Assert.True(allocated < 2 << 20, $"{allocated:N0} bytes");
    }
}
