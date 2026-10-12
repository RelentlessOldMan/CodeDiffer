using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// An edit in place among identical lines is a replace where it stands in the 3-way merge: Myers writes it as an insert
/// and a delete of some other copy in the run, so two sides editing the same line merged "cleanly", both edits kept.
/// </summary>
public sealed class InPlaceRunTests
{
    private const string A = "    acc = (acc * 1664525 + 1013904223) ^ (acc >> 3);";

    private static List<string> Run(int n, params (int Line, string Text)[] edits)
    {
        var lines = Enumerable.Repeat(A, n).Prepend("int f() {").Append("}").ToList();
        foreach (var (line, text) in edits) lines[line - 1] = text;
        return lines;
    }

    [Fact]
    public void BothSidesEditingOneLineOfARun_Conflict()
    {
        var r = ThreeWayMerger.Merge("f.c", Run(40), Run(40, (12, A + " // v1")), Run(40, (12, A + " // v2")));
        var c = Assert.Single(r.Conflicts);
        Assert.Equal((12, 1, HunkOp.Replace, 12, 1, HunkOp.Replace, 12, 1),
            (c.BaseStart, c.BaseLines, c.V1Op, c.V1NewStart, c.V1NewLines, c.V2Op, c.V2NewStart, c.V2NewLines));
        Assert.Empty(r.CleanMerges);
    }

    [Fact]
    public void SidesEditingLinesApartInARun_MergeCleanAtTheirLines()
    {
        var r = ThreeWayMerger.Merge("f.c", Run(40), Run(40, (8, "x1"), (30, "y1")), Run(40, (12, "x2"), (20, "y2")));
        Assert.Empty(r.Conflicts);
        Assert.Equal(new[] { ("v1", 8), ("v2", 12), ("v2", 20), ("v1", 30) },
            r.CleanMerges.Select(m => (m.Side, m.OldStart)));
        Assert.All(r.CleanMerges, m => Assert.Equal((HunkOp.Replace, 1, 1), (m.Op, m.OldLines, m.NewLines)));
    }

    [Fact]
    public void ALineEditedIntoARun_IsAReplaceWhereItStands()
    {
        // base has one odd line in the run; v1 makes it like the rest, v2 edits a line four below.
        var b = Run(40, (15, "odd"));
        var r = ThreeWayMerger.Merge("f.c", b, Run(40), Run(40, (15, "odd"), (19, "v2")));
        Assert.Empty(r.Conflicts);
        Assert.Equal(new[] { ("v1", 15), ("v2", 19) }, r.CleanMerges.Select(m => (m.Side, m.OldStart)));
    }

    [Fact]
    public void AnEditInPlace_IsOneReplace_AndStillMinimal()
    {
        var a = Run(40);
        var b = Run(40, (12, "x"), (25, "y"));
        var h = ThreeWayMerger.InPlace(LineDiffer.Diff(a, b), a, b);
        Assert.Equal(new[] { new Hunk(HunkOp.Replace, 12, 1, 12, 1), new Hunk(HunkOp.Replace, 25, 1, 25, 1) }, h);

        // A real insert and a real delete in a run stay as many lines: deletes beyond the inserts stay deletes.
        var c = Run(40).Take(30).Append("new").Concat(Run(40).Skip(33)).ToList(); // 3 copies out, 1 line in
        var hc = ThreeWayMerger.InPlace(LineDiffer.Diff(a, c), a, c);
        Assert.Equal(3, hc.Sum(x => x.OldLines));
        Assert.Equal(1, hc.Sum(x => x.NewLines));
        Assert.Null(HunkApplier.Problem(a, c, hc));
        Assert.Null(HunkApplier.Problem(a, b, h));
    }
}
