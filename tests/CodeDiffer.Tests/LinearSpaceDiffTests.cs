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
        var b = Enumerable.Range(0, 2000).Select(i => $"a{i * 7919 % 2000}").ToArray(); // a's lines, shuffled
        var hunks = LineDiffer.Diff(a, b, maxEditDistance: 0, out bool coarse, maxWork: 1000);
        Assert.True(coarse);
        Assert.Equal(b, HunkApplier.Reconstruct(a, b, hunks));
    }

    [Fact]
    public void AFileRewrittenWithMostlyNewLines_IsNotCoarse()
    {
        // A file rewritten with mostly new lines: 1,000 lines become 26,000, nearly all new, a few cut. As
        // changed lines that is far past the work cap; the new lines can match nothing, so set aside they are no work.
        var a = Enumerable.Range(0, 1_000).Select(i => $"msgid \"{i}\"").ToArray();
        var b = new List<string>();
        for (int i = 0; i < a.Length; i++)
        {
            if (i % 40 == 3) continue; // cut
            b.Add(a[i]);
            for (int k = 0; k < 25; k++) b.Add($"msgstr \"{i}.{k}\"");
        }
        var hunks = LineDiffer.Diff(a, b, LineDiffer.DefaultMaxEditDistance, out bool coarse);
        Assert.False(coarse);
        Assert.Equal(b, HunkApplier.Reconstruct(a, b, hunks));
        Assert.Equal(25 + 975 * 25, Cost(hunks)); // minimal: the 25 cuts and the 975 kept lines' new lines
    }

    [Fact]
    public void AMergeWithAFileRewrittenWithMostlyNewLines_ConflictsOnlyWhereTheOtherSideEdited()
    {
        // v1 rewrites the file with mostly new lines, v2 fixes one msgid: one small conflict there, not one over the whole file.
        var b = Enumerable.Range(0, 1_000).Select(i => $"msgid \"{i}\"").ToArray();
        var v1 = b.SelectMany((l, i) => new[] { l }.Concat(Enumerable.Range(0, 25).Select(k => $"msgstr \"{i}.{k}\""))).ToArray();
        var v2 = b.Select((l, i) => i == 500 ? "msgid \"500 fixed\"" : l).ToArray();
        var conflicts = ThreeWayMerger.Regions(b, v1, v2).Where(r => r.Kind == RegionKind.Conflict).ToList();
        Assert.True(conflicts.Count <= 1 && conflicts.All(c => c.V1Lines < 60), string.Join(", ", conflicts));
    }

    [Fact]
    public void LinesOnlyOneSideHas_SetAside_StillMinimal()
    {
        // Every trial forces the linear-space pass on inputs with many lines unique to one side, mixed with shared ones.
        var rng = new Random(99);
        for (int trial = 0; trial < 300; trial++)
        {
            var a = Enumerable.Range(0, rng.Next(0, 80)).Select(i => rng.Next(3) == 0 ? $"A{trial}.{i}" : "L" + rng.Next(6)).ToArray();
            var b = Enumerable.Range(0, rng.Next(0, 80)).Select(i => rng.Next(3) == 0 ? $"B{trial}.{i}" : "L" + rng.Next(6)).ToArray();
            var exact = LineDiffer.Diff(a, b, maxEditDistance: int.MaxValue, out _);
            var linear = LineDiffer.Diff(a, b, maxEditDistance: 0, out bool coarse);
            Assert.False(coarse);
            Assert.Equal(b, HunkApplier.Reconstruct(a, b, linear));
            Assert.Equal(Cost(exact), Cost(linear));
        }
    }
}
