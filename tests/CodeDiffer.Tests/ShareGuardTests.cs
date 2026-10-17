using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// A tree that stops answering mid-compare pauses the compare instead of turning every file left into "unreadable"
/// (seen on a share in use: 28,000 files listed unreadable after an hour); a file that alone can't be read is tried
/// again, then listed as before, without a pause. The "share" here is a tree moved away and back while the compare runs.
/// </summary>
public sealed class ShareGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-share-guard-" + Guid.NewGuid().ToString("N"));
    private const int Files = 30;

    public ShareGuardTests()
    {
        // Sizes all differ, so the biggest-first order is known: f029 first, f000 last.
        for (int i = 0; i < Files; i++)
            foreach (var tree in new[] { "L", "R" })
                Put($"{tree}/f{i:D3}.c", new string('x', 100 + i) + "\n");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(P("R.away"))) Directory.Move(P("R.away"), P("R")); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel);

    private void Put(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), new UTF8Encoding(false).GetBytes(text));
    }

    private CompareOptions Opts(CacheMode cache = CacheMode.Off, double shareWaitSeconds = 30) => new()
    {
        Cache = cache,
        Parallelism = 1,
        CacheBaseDir = P("cache"),
        CodeCompassBaseDir = P("no-compass"),
        Timing = new TrustTiming(0, 0),
        CheckpointInterval = TimeSpan.FromHours(1),
        ShareWait = TimeSpan.FromSeconds(shareWaitSeconds),
        RetryDelays = [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)],
        ProbeDelays = [TimeSpan.FromMilliseconds(100)],
    };

    /// <summary>After the <paramref name="after"/>th pair, the right tree goes away (and, given a delay, comes back).</summary>
    private CompareProgress RightGoesAway(int after, TimeSpan? back)
    {
        var progress = new CompareProgress();
        progress.AfterPairChecked = n =>
        {
            if (n != after) return;
            Directory.Move(P("R"), P("R.away"));
            if (back is { } d) _ = Task.Delay(d).ContinueWith(_ => Directory.Move(P("R.away"), P("R")));
        };
        return progress;
    }

    [Fact]
    public void TreesThatStopAnswering_PauseTheCompare_AndItCarriesOnWhenTheyAnswer()
    {
        var progress = RightGoesAway(10, back: TimeSpan.FromSeconds(1.5));
        var r = new DirectoryComparer(Opts()).Compare(P("L"), P("R"), progress);

        Assert.Equal(0, r.UnreadableFiles);
        Assert.Equal(Files, r.Changes.Count(c => c.Status == ChangeStatus.Identical));
        Assert.Equal(1, r.SharePauses);
        Assert.True(r.SharePaused >= TimeSpan.FromSeconds(1), r.SharePaused.ToString());
        Assert.True(r.RetriedReads >= 1);
        var notices = progress.Notices();
        Assert.Contains(notices, n => n.StartsWith("PAUSED: the trees stopped answering", StringComparison.Ordinal));
        Assert.Contains(notices, n => n.StartsWith("resumed:", StringComparison.Ordinal));
        Assert.Null(progress.PausedSinceUtc);
        Assert.Equal(1, progress.Pauses);

        // Counted by cause, codes and counts only: the failed tries, the reads that then worked, the pause.
        var cause = Assert.Single(r.ReadFailures, f => f.Pauses == 1);
        Assert.True(cause.Failed >= 1 && cause.Recovered >= 1 && cause.GaveUp == 0, cause.ToString());
        var pause = Assert.Single(r.PauseLog);
        Assert.True(pause.Resumed && pause.Seconds >= 1 && pause.AtSeconds >= 0, pause.ToString());
        Assert.Equal(pause.Cause, cause.Cause);
    }

    [Fact]
    public void TheTally_IsSavedWithTheResult()
    {
        var store = new CodeDiffer.Core.Sessions.SessionStore(resultsRoot: P("results"));
        var opts = Opts();
        var s = store.Start(P("L"), P("R"), new CompareOptions
        {
            Cache = opts.Cache, Parallelism = 1, CacheBaseDir = opts.CacheBaseDir, CodeCompassBaseDir = opts.CodeCompassBaseDir,
            Timing = opts.Timing, ShareWait = opts.ShareWait, RetryDelays = opts.RetryDelays, ProbeDelays = opts.ProbeDelays,
            AfterPairChecked = n =>
            {
                if (n != 10) return;
                Directory.Move(P("R"), P("R.away"));
                _ = Task.Delay(1000).ContinueWith(_ => Directory.Move(P("R.away"), P("R")));
            },
        });
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Null(s.Error);
        var live = s.Report!;
        var loaded = ((CodeDiffer.Core.Sessions.CompareSession)CodeDiffer.Core.Sessions.ResultStore.Load(s.ResultDir!)).Report!;
        Assert.Equal(1, loaded.SharePauses);
        Assert.Equal(live.RetriedReads, loaded.RetriedReads);
        Assert.Equal(live.ReadFailures, loaded.ReadFailures);
        Assert.Equal(live.PauseLog, loaded.PauseLog);
        Assert.Contains(CodeDiffer.Core.Sessions.AgentViews.Notes(loaded), n => n.StartsWith("failed reads by cause: ", StringComparison.Ordinal));
    }

    [Fact]
    public void TreesThatDontComeBack_StopTheCompare_AndWhatWasReadIsCached()
    {
        var progress = RightGoesAway(10, back: null);
        var ex = Assert.Throws<ShareUnavailableException>(() =>
            new DirectoryComparer(Opts(CacheMode.On, shareWaitSeconds: 1)).Compare(P("L"), P("R"), progress));
        Assert.Contains("did not come back", ex.Message);
        Assert.Contains(progress.Notices(), n => n.StartsWith("stopping:", StringComparison.Ordinal));
        Assert.Equal(0, progress.UnreadableSoFar); // nothing was listed unreadable for it
        var stopped = Assert.Single(progress.Reads.Pauses());
        Assert.False(stopped.Resumed);
        Assert.True(stopped.Seconds >= 1);

        // Back again: the ten pairs read before the stop come from the cache, the rest are read.
        Directory.Move(P("R.away"), P("R"));
        var again = new DirectoryComparer(Opts(CacheMode.On)).Compare(P("L"), P("R"));
        Assert.Equal(0, again.UnreadableFiles);
        Assert.True(again.CacheHits >= 2 * 10, again.CacheHits.ToString());
        Assert.Equal(Files, again.Changes.Count(c => c.Status == ChangeStatus.Identical));
    }

    [Fact]
    public void AFileThatAloneCantBeRead_IsTriedAgain_ThenUnreadable_WithoutAPause()
    {
        using (new FileStream(P("R/f020.c"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = new DirectoryComparer(Opts()).Compare(P("L"), P("R"));
            Assert.Equal(1, r.UnreadableFiles);
            Assert.Equal(0, r.SharePauses);
            if (OperatingSystem.IsWindows())
            {
                var locked = Assert.Single(r.ReadFailures);
                Assert.Equal("Windows error 32", locked.Label); // a sharing violation
                // Tried three times in the pass, three more at the end; given up both times.
                Assert.Equal((6, 0, 2, 0), (locked.Failed, locked.Recovered, locked.GaveUp, locked.Pauses));
            }
            Assert.Equal(Files - 1, r.Changes.Count(c => c.Status == ChangeStatus.Identical));
        }
    }

    [Fact]
    public void AFileLockedForAMoment_IsReadOnARetry()
    {
        var hold = new FileStream(P("R/f020.c"), FileMode.Open, FileAccess.Read, FileShare.None);
        var progress = new CompareProgress();
        // Released while the compare is waiting to try it again.
        progress.AfterPairChecked = n => { if (n == 9) _ = Task.Delay(100).ContinueWith(_ => hold.Dispose()); };
        var opts = Opts();
        var r = new DirectoryComparer(new CompareOptions
        {
            Cache = opts.Cache, Parallelism = 1, CacheBaseDir = opts.CacheBaseDir, CodeCompassBaseDir = opts.CodeCompassBaseDir,
            Timing = new TrustTiming(0, 0), RetryDelays = [TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(400)],
        }).Compare(P("L"), P("R"), progress);
        hold.Dispose();
        Assert.Equal(0, r.UnreadableFiles);
        Assert.Equal(Files, r.Changes.Count(c => c.Status == ChangeStatus.Identical));
        Assert.True(r.RetriedReads >= 1);
        Assert.Equal(0, r.SharePauses);
    }
}
