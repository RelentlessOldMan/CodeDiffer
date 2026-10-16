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
