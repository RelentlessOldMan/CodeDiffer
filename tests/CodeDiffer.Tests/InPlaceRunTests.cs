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
        var r = ThreeWayMerger.Merge("f.c", Run(40), Run(40, (8, A + " x1"), (30, A + " y1")), Run(40, (12, A + " x2"), (20, A + " y2")));
        Assert.Empty(r.Conflicts);
        Assert.Equal(new[] { ("v1", 8), ("v2", 12), ("v2", 20), ("v1", 30) },
            r.CleanMerges.Select(m => (m.Side, m.OldStart)));
        Assert.All(r.CleanMerges, m => Assert.Equal((HunkOp.Replace, 1, 1), (m.Op, m.OldLines, m.NewLines)));
    }

    [Fact]
    public void ALineEditedIntoARun_IsAReplaceWhereItStands()
    {
        // base has one odd line in the run; v1 makes it like the rest, v2 edits a line four below.
        var b = Run(40, (15, A + " // odd"));
        var r = ThreeWayMerger.Merge("f.c", b, Run(40), Run(40, (15, A + " // odd"), (19, A + " // v2")));
        Assert.Empty(r.Conflicts);
        Assert.Equal(new[] { ("v1", 15), ("v2", 19) }, r.CleanMerges.Select(m => (m.Side, m.OldStart)));
    }

    [Fact]
    public void AnEditInPlace_IsOneReplace_AndStillMinimal()
    {
        var a = Run(40);
        var b = Run(40, (12, A + " x"), (25, A.Replace(">> 3", ">> 5")));
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

    [Fact]
    public void AnUnrelatedLineInsertedIntoARun_WhileACopyIsCut_StaysAnInsertAndADelete()
    {
        // v1 inserts P after x5 and cuts the last x; v2 edits x7: three clean merges, not v1's P as an edit of x6.
        var b = new[] { "s" }.Concat(Enumerable.Repeat("x", 20)).Append("e").ToList();
        var v1 = new[] { "s" }.Concat(Enumerable.Repeat("x", 5)).Append("P").Concat(Enumerable.Repeat("x", 14)).Append("e").ToList();
        var v2 = new[] { "s" }.Concat(Enumerable.Repeat("x", 6)).Append("x // Q").Concat(Enumerable.Repeat("x", 13)).Append("e").ToList();
        var r = ThreeWayMerger.Merge("f.c", b, v1, v2);
        Assert.Empty(r.Conflicts);
        Assert.Contains(r.CleanMerges, m => m.Side == "v1" && m.Op == HunkOp.Insert && m.OldStart == 6);
        Assert.Contains(r.CleanMerges, m => (m.Side, m.Op, m.OldStart) == ("v2", HunkOp.Replace, 8));
    }

    [Theory]
    [InlineData("u242", "u243")]        // lines unlike the run's
    [InlineData("x // a", "x // b")]    // edits of the run's line, inserted outright
    public void ReMergingACleanMerge_ChangesNothing(string i1, string i2)
    {
        // apply run twice: v1 inserts two lines, v2 cuts a copy of the run; the merge, merged again with v1, is v1's
        // changes already there (no conflict, the same text).
        var b = Enumerable.Repeat("x", 7).Concat(new[] { "u241", "x", "a", "b", "c" }).ToList();
        var v1 = Enumerable.Repeat("x", 5).Concat(new[] { i1, "x", "x", "u241", i2, "x", "a", "b", "c" }).ToList();
        var v2 = Enumerable.Repeat("x", 6).Concat(new[] { "u241", "x", "a", "b", "c" }).ToList();
        var merged = MergedText(b, v1, v2);
        Assert.NotNull(merged);
        Assert.Equal(merged, MergedText(b, v1, merged!));
        Assert.Equal(merged, MergedText(b, v2, merged!));
    }

    /// <summary>The merged text of a conflict-free merge, or null if any region conflicts.</summary>
    private static List<string>? MergedText(List<string> b, List<string> v1, List<string> v2)
    {
        var o = new List<string>();
        int at = 0;
        foreach (var g in ThreeWayMerger.Regions(b, v1, v2))
        {
            if (g.Kind == RegionKind.Conflict) return null;
            o.AddRange(b.Skip(at).Take(g.BaseStart0 - at));
            o.AddRange(g.Kind == RegionKind.V2Only ? v2.Skip(g.V2Start0).Take(g.V2Lines) : v1.Skip(g.V1Start0).Take(g.V1Lines));
            at = g.BaseStart0 + g.BaseLines;
        }
        o.AddRange(b.Skip(at));
        return o;
    }
}
