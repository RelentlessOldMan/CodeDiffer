using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The Myers line differ: correct hunk decomposition in the shared CodeDiffer/CodeSpawner convention,
/// and the round-trip property (reconstruct new = old + hunks + new-content) on every case.
/// </summary>
public class LineDifferTests
{
    private static void AssertRoundTrips(string[] a, string[] b)
    {
        var hunks = LineDiffer.Diff(a, b);
        Assert.Equal(b, HunkApplier.Reconstruct(a, b, hunks));
    }

    [Fact]
    public void Identical_NoHunks()
    {
        var a = new[] { "a", "b", "c" };
        Assert.Empty(LineDiffer.Diff(a, a));
    }

    [Fact]
    public void SingleReplace()
    {
        var a = new[] { "a", "b", "c" };
        var b = new[] { "a", "X", "c" };
        Assert.Equal([new Hunk(HunkOp.Replace, 2, 1, 2, 1)], LineDiffer.Diff(a, b));
        AssertRoundTrips(a, b);
    }

    [Fact]
    public void SingleInsert_AnchorsOnLineAfterWhich()
    {
        var a = new[] { "a", "b" };
        var b = new[] { "a", "INS", "b" };
        // "INS" is inserted AFTER old line 1, landing at new line 2 — unified/CodeSpawner convention
        // (@@ -1,0 +2,1 @@), so oldStart = 1 (the line after which), not 2.
        Assert.Equal([new Hunk(HunkOp.Insert, 1, 0, 2, 1)], LineDiffer.Diff(a, b));
        AssertRoundTrips(a, b);
    }

    [Fact]
    public void SingleDelete()
    {
        var a = new[] { "a", "DEL", "b" };
        var b = new[] { "a", "b" };
        Assert.Equal([new Hunk(HunkOp.Delete, 2, 1, 2, 0)], LineDiffer.Diff(a, b));
        AssertRoundTrips(a, b);
    }

    [Fact]
    public void ContiguousChanges_CoalesceIntoOneReplace()
    {
        var a = new[] { "a", "b", "c", "d", "e" };
        var b = new[] { "a", "B", "C", "D", "e" }; // 2,3,4 changed, contiguous
        Assert.Equal([new Hunk(HunkOp.Replace, 2, 3, 2, 3)], LineDiffer.Diff(a, b));
        AssertRoundTrips(a, b);
    }

    [Fact]
    public void CodeSpawnerStyleContentEdit_OddLinesMarked_YieldsPerLineReplaces()
    {
        // Mirrors a content edit that appends a marker to odd (1-based) lines: 1,3,5,7,9 changed,
        // separated by unchanged even lines → five 1-line Replace hunks, old==new coords.
        var a = Enumerable.Range(1, 10).Select(i => $"line {i}").ToArray();
        var b = (string[])a.Clone();
        for (int i = 0; i < b.Length; i += 2) // 0-based even = odd 1-based
            b[i] += " /*mut:0:" + (i + 1) + "*/";

        var expected = new[] { 1, 3, 5, 7, 9 }.Select(l => new Hunk(HunkOp.Replace, l, 1, l, 1)).ToArray();
        Assert.Equal(expected, LineDiffer.Diff(a, b));
        AssertRoundTrips(a, b);
    }

    [Fact]
    public void MixedInsertDeleteReplace_RoundTrips()
    {
        var a = new[] { "keep1", "drop", "old2", "keep2", "keep3", "tailOld" };
        var b = new[] { "keep1", "new2", "keep2", "added", "keep3", "tailNew" };
        AssertRoundTrips(a, b);
        // and the hunks must actually be a valid, applyable partition — covered by the round-trip.
        Assert.NotEmpty(LineDiffer.Diff(a, b));
    }

    [Fact]
    public void TrailingInsertAtEnd()
    {
        var a = new[] { "a", "b" };
        var b = new[] { "a", "b", "c", "d" };
        // appended after old line 2 (@@ -2,0 +3,2 @@) ⇒ oldStart = 2 (line after which), newStart = 3.
        Assert.Equal([new Hunk(HunkOp.Insert, 2, 0, 3, 2)], LineDiffer.Diff(a, b));
        AssertRoundTrips(a, b);
    }

    [Fact]
    public void SplitLines_TrailingNewline_NoPhantomLine()
    {
        Assert.Equal(new[] { "a", "b" }, LineText.SplitLines("a\nb\n"));
        Assert.Equal(new[] { "a", "b" }, LineText.SplitLines("a\nb"));
        Assert.Equal(new[] { "a", "" }, LineText.SplitLines("a\n\n"));
        Assert.Empty(LineText.SplitLines(""));
    }
}
