using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>What a run record counts underneath: exceptions thrown (type and Windows error code, no message), which way
/// each live stat went, and what each phase allocated; and <c>codediffer runs</c> shows them.</summary>
public sealed class ProcessTallyTests
{
    private sealed class TallyProbeException : Exception;

    [Fact]
    public void Key_IsTheType_AndTheWindowsCode_NeverTheMessage()
    {
        Assert.Equal("IOException 87", ProcessTally.Key(new IOException("secret path", unchecked((int)0x80070057))));
        Assert.Equal("InvalidOperationException", ProcessTally.Key(new InvalidOperationException("secret path")));
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
}
