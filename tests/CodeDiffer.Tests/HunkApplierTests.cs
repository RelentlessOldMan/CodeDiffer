using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Regression tests for HunkApplier, pinning two bugs the CodeSpawner edge fixture surfaced:
///  • a manifest may list hunks in ANY order (the diffTruthSha is order-independent, so CodeSpawner emits
///    inserts last) — Reconstruct must sort before applying, not corrupt the walk;
///  • an insert anchors on the line AFTER WHICH it goes (oldStart lines precede it), the unified/CodeSpawner
///    convention, so insert(oldStart=L) + replace/delete coords reconstruct correctly together.
/// </summary>
public class HunkApplierTests
{
    [Fact]
    public void UnorderedHunks_WithTrailingInsert_StillReconstruct()
    {
        var baseLines = new[] { "l1", "l2", "l3", "l4", "l5" };
        var newLines = new[] { "l1", "L2", "l3", "l4", "INS", "L5" }; // replace l2, append INS after l4, replace l5

        // Deliberately out of base order — insert (after line 4) listed AFTER the line-5 replace,
        // exactly how CodeSpawner orders a delta's hunks.
        var hunks = new[]
        {
            new Hunk(HunkOp.Replace, 2, 1, 2, 1),  // l2 -> L2
            new Hunk(HunkOp.Replace, 5, 1, 6, 1),  // l5 -> L5 (new line 6, after the insert)
            new Hunk(HunkOp.Insert, 4, 0, 5, 1),   // INS after base line 4, at new line 5
        };

        Assert.Equal(newLines, HunkApplier.Reconstruct(baseLines, newLines, hunks));
    }

    [Fact]
    public void InsertAtStart_AnchorsOnLineZero()
    {
        var baseLines = new[] { "a", "b" };
        var newLines = new[] { "HEAD", "a", "b" };
        var hunks = new[] { new Hunk(HunkOp.Insert, 0, 0, 1, 1) }; // before line 1 == after line 0
        Assert.Equal(newLines, HunkApplier.Reconstruct(baseLines, newLines, hunks));
    }
}
