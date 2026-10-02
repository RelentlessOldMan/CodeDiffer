using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The native 3-way merge (diff3) on UNAMBIGUOUS inputs — distinct lines, so the minimal diff is unique
/// and the decomposition is exactly the contract's. Covers the locked edge shapes: one-sided clean each
/// way, modify/modify conflict, agreed-edit (canonical side v1), modify/delete, add/add, and adjacent
/// multi-line coalescing into one region.
/// </summary>
public class ThreeWayMergerTests
{
    private static FileMergeResult Merge(string b, string v1, string v2)
        => ThreeWayMerger.Merge("f", b, v1, v2);

    [Fact]
    public void OneSided_V1Changed_IsCleanV1()
    {
        var r = Merge("a\nb\nc\n", "a\nB\nc\n", "a\nb\nc\n");
        Assert.Empty(r.Conflicts);
        var m = Assert.Single(r.CleanMerges);
        Assert.Equal(new CleanMerge("f", "v1", HunkOp.Replace, 2, 1, 2, 1), m);
    }

    [Fact]
    public void OneSided_V2Changed_IsCleanV2()
    {
        var r = Merge("a\nb\nc\n", "a\nb\nc\n", "a\nB\nc\n");
        Assert.Empty(r.Conflicts);
        var m = Assert.Single(r.CleanMerges);
        Assert.Equal(new CleanMerge("f", "v2", HunkOp.Replace, 2, 1, 2, 1), m);
    }

    [Fact]
    public void BothChangedDifferently_IsConflict()
    {
        var r = Merge("a\nb\nc\n", "a\nB\nc\n", "a\nC\nc\n");
        Assert.Empty(r.CleanMerges);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(new Conflict("f", 2, 1, HunkOp.Replace, 2, 1, HunkOp.Replace, 2, 1), c);
    }

    [Fact]
    public void BothChangedIdentically_IsCleanAgreed_SideV1()
    {
        var r = Merge("a\nb\nc\n", "a\nB\nc\n", "a\nB\nc\n");
        Assert.Empty(r.Conflicts);
        var m = Assert.Single(r.CleanMerges);
        Assert.Equal("v1", m.Side); // canonical side for an agreed edit
        Assert.Equal(new CleanMerge("f", "v1", HunkOp.Replace, 2, 1, 2, 1), m);
    }

    [Fact]
    public void ModifyDelete_IsConflict_WithDeleteSide()
    {
        // v1 replaces line 2; v2 deletes it.
        var r = Merge("a\nb\nc\n", "a\nB\nc\n", "a\nc\n");
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(new Conflict("f", 2, 1, HunkOp.Replace, 2, 1, HunkOp.Delete, 2, 0), c);
    }

    [Fact]
    public void AddAdd_IsConflict_ZeroBaseLines_TwoInserts()
    {
        // both insert a different line after base line 1 (between a and b).
        var r = Merge("a\nb\n", "a\nX\nb\n", "a\nY\nb\n");
        var c = Assert.Single(r.Conflicts);
        // add/add anchors on the line after which (1), baseLines 0, each side inserts 1 line at new line 2.
        Assert.Equal(new Conflict("f", 1, 0, HunkOp.Insert, 2, 1, HunkOp.Insert, 2, 1), c);
    }

    [Fact]
    public void AdjacentMultiLine_CoalescesToOneConflict()
    {
        // both replace the adjacent lines 2 and 3 ⇒ ONE region, baseLines 2 (union-span rule).
        var r = Merge("a\nb\nc\nd\n", "a\nB\nC\nd\n", "a\nX\nY\nd\n");
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(new Conflict("f", 2, 2, HunkOp.Replace, 2, 2, HunkOp.Replace, 2, 2), c);
    }

    [Fact]
    public void NoChange_IsEmpty()
    {
        var r = Merge("a\nb\nc\n", "a\nb\nc\n", "a\nb\nc\n");
        Assert.Empty(r.Conflicts);
        Assert.Empty(r.CleanMerges);
    }

    [Fact]
    public void NonOverlappingOneSidedEdits_BothClean_NoConflict()
    {
        // v1 edits line 2, v2 edits line 4 — disjoint base ranges ⇒ two clean merges, no conflict.
        var r = Merge("a\nb\nc\nd\ne\n", "a\nB\nc\nd\ne\n", "a\nb\nc\nD\ne\n");
        Assert.Empty(r.Conflicts);
        Assert.Equal(2, r.CleanMerges.Count);
        Assert.Contains(new CleanMerge("f", "v1", HunkOp.Replace, 2, 1, 2, 1), r.CleanMerges);
        Assert.Contains(new CleanMerge("f", "v2", HunkOp.Replace, 4, 1, 4, 1), r.CleanMerges);
    }
}
