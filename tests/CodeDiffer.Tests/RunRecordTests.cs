using System.Diagnostics;
using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>run.json's CPU time: counted from the process's cycles on Windows, not the tick-sampled figure.</summary>
public sealed class RunRecordTests
{
    [Fact]
    public void CpuTime_CountsWorkDone_InSeconds()
    {
        using var me = Process.GetCurrentProcess();
        var (before, from) = RunRecord.Cpu(me);
        var wall = Stopwatch.StartNew();
        double after;
        do Thread.SpinWait(100_000); // a test on one shared core: spin until the work shows, not for a set time
        while ((after = RunRecord.Cpu(me).Seconds) - before < 0.3 && wall.Elapsed.TotalSeconds < 30);

        Assert.Equal(OperatingSystem.IsWindows() ? "cycles" : "sampled", from);
        Assert.InRange(after - before, 0.3, wall.Elapsed.TotalSeconds * Environment.ProcessorCount + 0.5); // seconds, not cycles
    }
}
