using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>What a run record counts underneath: exceptions thrown (type and Windows error code, no message), which way
/// each live stat went, and what each phase allocated; and <c>codediffer runs</c> shows them.</summary>
public sealed class ProcessTallyTests
{
    private sealed class TallyProbeException : Exception;
    private static byte[]? _sink;

    [Fact]
    public void Key_IsTheType_AndTheWindowsCode_NeverTheMessage()
    {
        Assert.Equal("IOException 87", ProcessTally.Key(new IOException("secret path", unchecked((int)0x80070057))));
        Assert.Equal("InvalidOperationException", ProcessTally.Key(new InvalidOperationException("secret path")));
        // An ArgumentException's HResult (E_INVALIDARG) reads as Windows error 87, and isn't one.
        Assert.Equal("DecoderFallbackException", ProcessTally.Key(new System.Text.DecoderFallbackException("x")));
        // A plain IOException's HResult (COR_E_IO, 0x80131620) carries no Windows code; its low half isn't one.
        Assert.Equal("IOException", ProcessTally.Key(new IOException("x")));
        // A Win32Exception's HResult is E_FAIL: its code is its own.
        Assert.Equal("Win32Exception 1450", ProcessTally.Key(new System.ComponentModel.Win32Exception(1450)));
    }

    [Fact]
    public void Exceptions_CaughtOnes_AreCounted_SinceASnapshot()
    {
        ProcessTally.Install();
        ProcessTally.Install(); // a second install doesn't count twice
        var before = ProcessTally.Snapshot().Exceptions;
        for (int i = 0; i < 3; i++)
            try { throw new TallyProbeException(); }
            catch (TallyProbeException) { }
        var grew = ProcessTally.Since(ProcessTally.Snapshot().Exceptions, before).ToDictionary(x => x.Key, x => x.Count);
        Assert.Equal(3, grew[nameof(TallyProbeException)]);
    }

    [Fact]
    public void Runs_ShowsAllocationByPhase_AndTheStatWays()
    {
        using var t = new TempDir();
        t.Put("L/a.txt", "one\ntwo\n");
        t.Put("R/a.txt", "one\ntwo\n");
        t.Put("L/b.txt", "x\n");
        t.Put("R/b.txt", "y\n");
        var exe = ExeRunner.Exe("CodeDiffer.Cli.exe");
        var c = ExeRunner.Run(exe, t.Root, t.P("state"), ["compare", t.P("L"), t.P("R")]);
        Assert.True(c.ExitCode is 0 or 1, c.ToString());
        var r = ExeRunner.Run(exe, t.Root, t.P("state"), ["runs"]);
        Assert.Equal(0, r.ExitCode);
        Assert.Matches(@"allocated: .*contents [\d,]+ MB", r.Stdout);
        Assert.Contains("file stats and opens by way: ", r.Stdout); // the pair hashed, stat-bracketed
        Assert.DoesNotContain(t.Root, r.Stdout);
    }

    [Fact]
    public void Runs_Compare3_GivesEachCompareOnlyItsOwnBytes_AndTheMergeItsOwn()
    {
        // The second compare is starting while the first runs, and the first is done while the second runs and the merge
        // after: counted, each showed the other's bytes as its own (a first compare's "done" of gigabytes).
        using var t = new TempDir();
        t.Put("B/a.txt", "one\ntwo\nthree\n");
        t.Put("V1/a.txt", "ONE\ntwo\nthree\n");
        t.Put("V2/a.txt", "one\ntwo\nTHREE\n");
        var exe = ExeRunner.Exe("CodeDiffer.Cli.exe");
        var c = ExeRunner.Run(exe, t.Root, t.P("state"), ["compare3", t.P("B"), t.P("V1"), t.P("V2")]);
        Assert.True(c.ExitCode is 0 or 1, c.ToString());
        var r = ExeRunner.Run(exe, t.Root, t.P("state"), ["runs"]);
        Assert.Equal(0, r.ExitCode);
        var allocated = r.Stdout.Split('\n').Where(l => l.TrimStart().StartsWith("allocated:")).ToList();
        Assert.Equal(2, allocated.Count);
        Assert.All(allocated, l => Assert.DoesNotMatch("starting|done", l));
        Assert.Matches(@"merge +1 changed paths classified and merged in .* · allocated [\d,]+ MB", r.Stdout);
        Assert.Matches(@"save +result saved in .* · allocated [\d,]+ MB", r.Stdout);
        Assert.Matches(@"CPU over .* \((cycles|sampled)\) ·", r.Stdout);
    }

    [Fact]
    public void PhaseAllocated_CountsNeitherStartingNorDone()
    {
        var p = new CodeDiffer.Core.Compare.CompareProgress();
        _sink = new byte[1 << 20];
        p.SetPhase(CodeDiffer.Core.Compare.ComparePhase.Walking);
        _sink = new byte[1 << 20];
        p.SetPhase(CodeDiffer.Core.Compare.ComparePhase.Done);
        _sink = new byte[1 << 20];
        var by = p.PhaseAllocated().ToDictionary(x => x.Phase, x => x.Bytes);
        Assert.True(by.GetValueOrDefault(CodeDiffer.Core.Compare.ComparePhase.Walking) >= 1 << 20, string.Join(", ", by));
        Assert.False(by.ContainsKey(CodeDiffer.Core.Compare.ComparePhase.Starting));
        Assert.False(by.ContainsKey(CodeDiffer.Core.Compare.ComparePhase.Done));
    }
}
