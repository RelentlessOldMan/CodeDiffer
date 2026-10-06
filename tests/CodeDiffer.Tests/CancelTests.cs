using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Cancelling a running compare: it stops, keeps the hashes it already read (so a re-run only reads the rest),
/// and a session says so — in its views and in the saved result's state.
/// </summary>
public sealed class CancelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-cancel-" + Guid.NewGuid().ToString("N"));
    private readonly string _left, _right;
    private const int Files = 60;

    public CancelTests()
    {
        _left = Path.Combine(_dir, "L");
        _right = Path.Combine(_dir, "R");
        for (int i = 0; i < Files; i++)
        {
            Put($"L/f{i:D3}.c", $"file {i:D3}\n");
            Put($"R/f{i:D3}.c", i == 7 ? $"FILE {i:D3}\n" : $"file {i:D3}\n");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Put(string rel, string text)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(text));
    }

    private CompareOptions Cached => new()
    {
        Cache = CacheMode.On,
        Parallelism = 1,
        CacheBaseDir = Path.Combine(_dir, "cache"),
        CodeCompassBaseDir = Path.Combine(_dir, "no-compass"),
        Timing = new TrustTiming(0, 0),
    };

    [Fact]
    public void Cancelled_Throws_AndKeepsTheHashesItRead()
    {
        using var stop = new CancellationTokenSource();
        var p = new CompareProgress { AfterPairChecked = done => { if (done == 20) stop.Cancel(); } };
        Assert.ThrowsAny<OperationCanceledException>(() => new DirectoryComparer(Cached).Compare(_left, _right, p, ct: stop.Token));
        Assert.InRange(p.PairsDone, 20, 21); // one thread: it stops at the next pair

        // The re-run trusts the ~20 pairs (both sides) read before the cancel and reads only the rest.
        var r = new DirectoryComparer(Cached).Compare(_left, _right);
        Assert.InRange(r.CacheHits, 40, 42);
        Assert.Equal(Files - 1, r.Count(ChangeStatus.Identical));
        Assert.Equal("f007.c", Assert.Single(r.Changes, c => c.Status == ChangeStatus.Modified).RelativePath);
    }

    [Fact]
    public void CancelledBeforeItStarts_Throws()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new DirectoryComparer(Cached).Compare(_left, _right, ct: stop.Token));
    }

    [Fact]
    public void Session_Cancel_IsReportedAndSavedAsCancelled()
    {
        var store = new SessionStore(resultsRoot: Path.Combine(_dir, "results"));
        var s = store.Start(_left, _right, Cached);
        bool cancelled = s.Cancel();
        s.Wait(TimeSpan.FromSeconds(30));
        Assert.True(s.IsDone);

        if (cancelled && s.Cancelled)
        {
            Assert.Equal("cancelled", s.Error);
            Assert.Contains("CANCELLED after ", AgentViews.Summary(s));
            Assert.Contains("CANCELLED", AgentViews.ListFiles(s));
            // The continuation that records the state runs as the task completes.
            Assert.True(SpinWait.SpinUntil(() => ResultStore.List(store.ResultsRoot!).Single().State == "cancelled", 5000));
            var ex = Assert.Throws<InvalidDataException>(() => ResultStore.Load(s.ResultDir!));
            Assert.Contains("was cancelled", ex.Message);
        }
        else
        {
            // It finished first: a cancel after the end changes nothing.
            Assert.False(s.Cancel());
            Assert.Null(s.Error);
        }
        Assert.False(s.Cancel()); // finished one way or the other: nothing left to cancel
    }
}
