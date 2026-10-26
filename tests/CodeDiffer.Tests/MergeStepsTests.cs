using CodeDiffer.Core.Diff;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>A merge's steps are charged their own bytes only: a diff inside another step is the diff's, the step it
/// interrupted carries on after, and a thread with no tally counts nothing.</summary>
public class MergeStepsTests
{
    private static byte[]? _sink;

    [Fact]
    public void ADiffInsideAStep_IsTheDiffs_AndTheStepGoesOnAfter()
    {
        var a = Enumerable.Range(0, 2_000).Select(i => $"line {i}\n").ToArray();
        var b = a.Select((l, i) => i % 7 == 0 ? "edited " + l : l).ToArray();
        var steps = new MergeSteps();
        var tally = steps.Begin();
        MergeSteps.Enter(MergeStep.Align);
        _sink = new byte[4 << 20];
        LineDiffer.Diff(a, b, LineDiffer.DefaultMaxEditDistance, out _);
        _sink = new byte[4 << 20];
        MergeSteps.Enter(MergeStep.Other);
        steps.End(tally);

        Assert.InRange(steps.Allocated(MergeStep.Align), 8L << 20, 9L << 20);
        Assert.InRange(steps.Allocated(MergeStep.Diff), 1, 4L << 20);
        Assert.Equal(1, steps.Diffs(DiffWay.Exact));
        Assert.Equal(0, steps.Diffs(DiffWay.LinearSpace));
    }

    [Fact]
    public void WithNoTally_NothingIsCounted()
    {
        var steps = new MergeSteps();
        Assert.Equal(MergeStep.Other, MergeSteps.Enter(MergeStep.Diff));
        LineDiffer.Diff(["a\n", "b\n"], ["a\n", "c\n"], LineDiffer.DefaultMaxEditDistance, out _);
        Assert.Equal(0, steps.Diffs(DiffWay.Exact));
        Assert.Equal(0, steps.Allocated(MergeStep.Diff));
    }
}
