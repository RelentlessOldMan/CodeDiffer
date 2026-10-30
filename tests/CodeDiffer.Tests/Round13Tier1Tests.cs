using System.Text;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.ThreeWay;
using CodeDiffer.Core.Verify;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Code review pass 13, tier 1: an edit in place among identical lines is an edit of the run's line, and a merge merged again
/// changes nothing (see also <see cref="InPlaceRunTests"/>); verify fails a manifest that writes such an edit as an
/// insert and a cut apart; a line diff's work cap grows with the files; a manifest's counts near 2^31 fail, not crash.
/// </summary>
public sealed class Round13Tier1Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r13a-" + Guid.NewGuid().ToString("N"));

    public Round13Tier1Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string rel, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), new UTF8Encoding(false).GetBytes(string.Concat(lines.Select(l => l + "\n"))));
    }

    private ConflictCrossCheckResult Check(IReadOnlyList<Conflict> conflicts, IReadOnlyList<CleanMerge> clean)
        => ConflictTreeCrossCheck.Run(P("B"), P("V1"), P("V2"), new ConflictManifest(1, "V1", "V2", conflicts, clean, ConflictTruthSha: null));

    // ---- which lines pair ----

    [Theory]
    [InlineData("}", "};", true)]
    [InlineData("    acc = 1;", "    acc = 2;", true)]
    [InlineData("x", "x /*v1:3:5*/", true)]
    [InlineData("x", "P", false)]
    [InlineData("x", "u242", false)]
    [InlineData("}", "int y = 0;", false)]
    [InlineData("", "// note", false)]
    public void ALineResemblesARunLine_OnlyAsAnEditOfIt(string run, string line, bool want)
        => Assert.Equal(want, ThreeWayMerger.Resembles(run, line));

    // ---- verify and an edit in place written apart ----

    [Fact]
    public void BothSidesEditsOfOneRunLine_MergedSideBySide_Fail()
    {
        // Both sides edit base line 14 (an x in a run); the manifest writes each edit as an insert and a cut of another
        // copy, so the two merge side by side. The file's other conflict used to let it through.
        var x = Enumerable.Repeat("x", 20).ToList();
        Put("B/f.txt", new[] { "a", "s" }.Concat(x));
        Put("V1/f.txt", new[] { "A1", "s" }.Concat(x.Take(11)).Append("x P").Concat(x.Take(8)));
        Put("V2/f.txt", new[] { "A2", "s" }.Concat(x.Take(11)).Append("x Q").Concat(x.Take(8)));
        var top = new Conflict("f.txt", 1, 1, HunkOp.Replace, 1, 1, HunkOp.Replace, 1, 1);
        var sbs = Check([top], [
            new CleanMerge("f.txt", "v1", HunkOp.Delete, 3, 1, 3, 0), new CleanMerge("f.txt", "v1", HunkOp.Insert, 14, 0, 14, 1),
            new CleanMerge("f.txt", "v2", HunkOp.Insert, 13, 0, 14, 1), new CleanMerge("f.txt", "v2", HunkOp.Delete, 22, 1, 23, 0)]);
        Assert.True(sbs.Reconstructs);
        Assert.False(sbs.Ok);
        Assert.Contains(sbs.UnsoundRegions, u => u.Contains("edit in place"));

        var truth = Check([top, new Conflict("f.txt", 14, 1, HunkOp.Replace, 14, 1, HunkOp.Replace, 14, 1)], []);
        Assert.True(truth.Ok, string.Join("; ", truth.UnsoundRegions));
    }

    // ---- the work cap grows with the files ----

    [Fact]
    public void ALongFileWithEveryFourthLineEdited_DiffsExactly_AndMergesClean()
    {
        var b = Enumerable.Range(0, 40_000).Select(i => "line " + i).ToList();
        var v1 = b.Select((l, i) => i % 4 == 0 ? l + " v1" : l).ToList();
        var h = LineDiffer.Diff(b, v1, LineDiffer.DefaultMaxEditDistance, out bool coarse);
        Assert.False(coarse);
        Assert.Equal(10_000, h.Count);
        var v2 = b.Select((l, i) => i == 2 ? l + " v2" : l).ToList();
        Assert.DoesNotContain(ThreeWayMerger.Regions(b, v1, v2), r => r.Kind == RegionKind.Conflict);

        Assert.Equal(LineDiffer.DefaultMaxWork, LineDiffer.WorkCap(100, 100));
        Assert.Equal(LineDiffer.MaxWorkCeiling, LineDiffer.WorkCap(int.MaxValue, int.MaxValue));
    }

    // ---- counts near 2^31 ----

    [Fact]
    public void AManifestCountNear2To31_FailsTheCheck_NotCrashes()
    {
        var b = Enumerable.Range(1, 10).Select(i => "line " + i).ToList();
        Put("B/f.txt", b);
        Put("V1/f.txt", b.Select((l, i) => i == 2 ? "v1" : l));
        Put("V2/f.txt", b.Select((l, i) => i == 6 ? "v2" : l));
        foreach (var bad in new[]
                 {
                     new CleanMerge("f.txt", "v1", HunkOp.Replace, 3, 1, 3, int.MaxValue),
                     new CleanMerge("f.txt", "v1", HunkOp.Replace, 3, int.MaxValue, 3, 1),
                     new CleanMerge("f.txt", "v1", HunkOp.Replace, int.MaxValue, 1, int.MaxValue, 1),
                     new CleanMerge("f.txt", "v1", HunkOp.Replace, int.MinValue, 1, 3, 1),
                 })
        {
            var r = Check([], [bad, new CleanMerge("f.txt", "v2", HunkOp.Replace, 7, 1, 7, 1)]);
            Assert.False(r.Ok);
            Assert.False(r.Reconstructs);
        }
        Assert.False(HunkApplier.Rebuilds(b, b, [new Hunk(HunkOp.Replace, 3, 1, 3, int.MaxValue)]));
    }
}
