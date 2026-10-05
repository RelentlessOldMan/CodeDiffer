using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The linear-space Myers fallback (used past the exact-trace budget) must be (1) correct — its hunks rebuild
/// the new side exactly — and (2) minimal — the same number of deleted+inserted lines as the classic exact
/// Myers. Checked on many seeded random inputs with heavy duplication (the ambiguous case), forcing the
/// fallback with a zero exact budget.
/// </summary>
public class LinearSpaceDiffTests
{
    private static string[] RandomLines(Random rng, int count, int alphabet)
        => Enumerable.Range(0, count).Select(_ => "L" + rng.Next(alphabet)).ToArray();

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

    private static int Cost(IEnumerable<Hunk> hunks) => hunks.Sum(h => h.OldLines + h.NewLines);

    [Fact]
    public void LinearSpace_IsCorrectAndMinimal_OnRandomInputs()
    {
        var rng = new Random(12345);
        for (int trial = 0; trial < 300; trial++)
        {
            int alphabet = rng.Next(2, 12); // small alphabets ⇒ lots of identical lines (ambiguity)
            var a = RandomLines(rng, rng.Next(0, 80), alphabet);
            var b = rng.Next(4) == 0 ? RandomLines(rng, rng.Next(0, 80), alphabet) : Mutate(rng, a, rng.Next(0, 25), alphabet);

            var exact = LineDiffer.Diff(a, b, maxEditDistance: int.MaxValue, out bool c1);
            var linear = LineDiffer.Diff(a, b, maxEditDistance: 0, out bool c2); // forces linear-space
            Assert.False(c1);
            Assert.False(c2);
            Assert.Equal(b, HunkApplier.Reconstruct(a, b, linear));
            Assert.Equal(Cost(exact), Cost(linear));
        }
    }

    [Fact]
    public void LargeRealisticEdit_StaysMinimal_AndNotCoarse()
    {
        // ~65k lines with ~3% scattered edits ⇒ far past the 3000 exact budget (the death a23_5.c shape).
        var rng = new Random(7);
        var a = Enumerable.Range(0, 65_000).Select(i => $"int f{i}(void) {{ return {i % 97}; }}").ToArray();
        var b = a.ToArray();
        for (int i = 0; i < 2_000; i++) b[rng.Next(b.Length)] = $"/* edited {i} */";
        var hunks = LineDiffer.Diff(a, b, LineDiffer.DefaultMaxEditDistance, out bool coarse);
        Assert.False(coarse);
        Assert.Equal(b, HunkApplier.Reconstruct(a, b, hunks));
        Assert.True(hunks.Count > 1_000); // scattered hunks, not one block
    }

    [Fact]
    public void WorkCap_FallsBackToCoarse_StillCorrect()
    {
        var a = Enumerable.Range(0, 2000).Select(i => $"a{i}").ToArray();
        var b = Enumerable.Range(0, 2000).Select(i => $"b{i}").ToArray();
        var hunks = LineDiffer.Diff(a, b, maxEditDistance: 0, out bool coarse, maxWork: 1000);
        Assert.True(coarse);
        Assert.Equal(b, HunkApplier.Reconstruct(a, b, hunks));
    }
}
