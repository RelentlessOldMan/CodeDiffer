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

    private CompareOptions Cached => Opts();

    private CompareOptions Opts(CacheMode cache = CacheMode.On, Action<long>? afterPair = null) => new()
    {
        Cache = cache,
        AfterPairChecked = afterPair,
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
    public void KilledOutright_KeepsTheHashesSavedMidRun()
    {
        // A kill runs no catch: all a killed run leaves is what was on disk at that moment. Copy the cache folder mid-run
        // (once a checkpoint has saved the first 30 pairs), as a kill would leave it, and re-run from the copy.
        var cache = Path.Combine(_dir, "cache");
        var killed = Path.Combine(_dir, "cache-at-kill");
        var p = new CompareProgress();
        p.AfterPairChecked = done =>
        {
            if (done != 30) return;
            for (var until = DateTime.UtcNow.AddSeconds(10); DateTime.UtcNow < until && p.Checkpoints == 0;) Thread.Sleep(20);
            if (p.Checkpoints > 0) CopyDir(cache, killed);
        };
        var opts = new CompareOptions
        {
            Cache = CacheMode.On, Parallelism = 1, CacheBaseDir = cache, CodeCompassBaseDir = Path.Combine(_dir, "no-compass"),
            Timing = new TrustTiming(0, 0), CheckpointInterval = TimeSpan.FromMilliseconds(50),
        };
        new DirectoryComparer(opts).Compare(_left, _right, p);
        Assert.True(Directory.Exists(killed), "no checkpoint came before the 30th pair");

        var r = new DirectoryComparer(new CompareOptions
        {
            Cache = CacheMode.On, Parallelism = 1, CacheBaseDir = killed, CodeCompassBaseDir = Path.Combine(_dir, "no-compass"),
            Timing = new TrustTiming(0, 0),
        }).Compare(_left, _right);
        Assert.InRange(r.CacheHits, 2, 60); // what the checkpoint saved by pair 30, never more
        Assert.Equal(Files - 1, r.Count(ChangeStatus.Identical));
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            if (!f.EndsWith("ledger.lock", StringComparison.Ordinal)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
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
        // The compare holds at its 5th pair until the cancel has been asked for: always cancelled mid-run.
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var options = Opts(afterPair: done => { if (done == 5) { reached.Set(); release.Wait(30_000); } });
        var store = new SessionStore(resultsRoot: Path.Combine(_dir, "results"));
        var s = store.Start(_left, _right, options);
        Assert.True(reached.Wait(30_000));
        Assert.True(s.Cancel());
        release.Set();
        s.Wait(TimeSpan.FromSeconds(30));

        Assert.True(s.IsDone);
        Assert.True(s.Cancelled);
        Assert.Equal("cancelled", s.Error);
        Assert.Contains("CANCELLED after ", AgentViews.Summary(s));
        Assert.Contains("CANCELLED", AgentViews.ListFiles(s));
        // The continuation that records the state runs as the task completes.
        Assert.True(SpinWait.SpinUntil(() => ResultStore.List(store.ResultsRoot!).Single().State == "cancelled", 5000));
        var ex = Assert.Throws<InvalidDataException>(() => ResultStore.Load(s.ResultDir!));
        Assert.Contains("was cancelled", ex.Message);
        Assert.False(s.Cancel()); // finished: nothing left to cancel
    }

    [Fact]
    public void CancelledRehash_KeepsTheLedgerItDidNotGetTo()
    {
        Assert.Equal(0, new DirectoryComparer(Cached).Compare(_left, _right).CacheHits); // cold: hashes all 60 pairs

        using var stop = new CancellationTokenSource();
        var p = new CompareProgress { AfterPairChecked = done => { if (done == 20) stop.Cancel(); } };
        var rehash = Opts(CacheMode.Rehash);
        Assert.ThrowsAny<OperationCanceledException>(() => new DirectoryComparer(rehash).Compare(_left, _right, p, ct: stop.Token));

        // The 20 it re-read are fresh; the 40 it didn't get to keep their old entries — nothing is lost.
        Assert.Equal(2 * Files, new DirectoryComparer(Cached).Compare(_left, _right).CacheHits);
    }

    [Fact]
    public void Compare3Rehash_KeepsTheBaseLedger()
    {
        var v2 = Path.Combine(_dir, "V2");
        for (int i = 0; i < Files; i++) Put($"V2/f{i:D3}.c", $"file {i:D3}\n");
        var rehash = Opts(CacheMode.Rehash);
        CodeDiffer.Core.ThreeWay.TreeMerger.Run(_left, _right, v2, rehash);
        // The base->v2 pass reuses base ids (reads no base file) but must not shrink the base ledger base->v1 wrote.
        var warm = CodeDiffer.Core.ThreeWay.TreeMerger.Run(_left, _right, v2, Cached);
        Assert.Equal(2 * Files, warm.V1Report.CacheHits);
    }

    [Fact]
    public void RenameDetection_SeesACancel()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var gone = new CodeDiffer.Core.Walk.TreeWalker([".git"], 1).WalkAll(_left).Files;
        var added = new CodeDiffer.Core.Walk.TreeWalker([".git"], 1).WalkAll(_right).Files;
        Assert.ThrowsAny<OperationCanceledException>(() => new RenameDetector(new CompareOptions()).Detect(gone, added, stop.Token));
    }
}
