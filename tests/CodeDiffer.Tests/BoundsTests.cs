using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Giant;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Port;
using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Review round 8, tier 3: edited-rename scoring is bounded (and says so when it stops short), a cancel reaches the
/// streamed reason check and the giant-file block diff, and a rename taken as done by likeness says so.
/// </summary>
public sealed class BoundsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-bounds-" + Guid.NewGuid().ToString("N"));

    public BoundsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), new UTF8Encoding(false).GetBytes(text));
    }

    private static string Lines(int from, int to, string prefix) =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"{prefix} {i}\n"));

    private void TwoEditedRenamesAndAPureOne()
    {
        Put("L/a.c", Lines(1, 20, "a"));
        Put("R/a2.c", Lines(1, 19, "a") + "edited\n");
        Put("L/b.c", Lines(1, 20, "b"));
        Put("R/b2.c", Lines(1, 19, "b") + "edited\n");
        Put("L/p.c", Lines(1, 5, "p"));
        Put("R/q.c", Lines(1, 5, "p"));
    }

    private CompareReport Compare(long maxPairs = 5_000L * 5_000, long maxBytes = 1L << 30) =>
        new DirectoryComparer(new CompareOptions { Cache = CacheMode.Off, MaxEditedRenamePairs = maxPairs, MaxEditedRenameBytes = maxBytes })
            .Compare(P("L"), P("R"));

    [Fact]
    public void EditedRenames_WithinTheLimits_AreFound()
    {
        TwoEditedRenamesAndAPureOne();
        var r = Compare();
        Assert.Null(r.RenameLimit);
        Assert.Equal(3, r.Count(ChangeStatus.Renamed));
    }

    [Theory]
    [InlineData(3, 1L << 30)]   // 2 removed × 2 added candidates is past 3 pairs
    [InlineData(100, 100)]      // their text is past 100 bytes
    public void EditedRenames_PastALimit_AreNotLookedFor_AndThatIsSaid(long maxPairs, long maxBytes)
    {
        TwoEditedRenamesAndAPureOne();
        var r = Compare(maxPairs, maxBytes);
        Assert.Contains("edited renames not looked for", r.RenameLimit);
        // The identical rename is still found; the edited ones are listed as what they look like without one.
        Assert.Equal(("p.c", "q.c"), Assert.Single(r.Changes, c => c.Status == ChangeStatus.Renamed) is var x ? (x.RenamedFrom, x.RelativePath) : default);
        Assert.Equal(2, r.Count(ChangeStatus.Added));
        Assert.Equal(2, r.Count(ChangeStatus.Removed));
        Assert.Contains(AgentViews.Notes(r), n => n.Contains("edited renames not looked for"));
    }

    [Fact]
    public void TheRenameLimitNote_IsSavedWithTheResult()
    {
        TwoEditedRenamesAndAPureOne();
        var results = P("results");
        var s = new SessionStore(resultsRoot: results).Start(P("L"), P("R"),
            new CompareOptions { Cache = CacheMode.Off, MaxEditedRenamePairs = 3 });
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Null(s.Error);
        var back = new SessionStore(resultsRoot: results).Get(s.Id) as CompareSession;
        Assert.Contains("edited renames not looked for", back!.Report!.RenameLimit);
    }

    [Fact]
    public void ACancel_StopsTheStreamedReasonCheck_AndTheBlockDiff()
    {
        Put("l.txt", Lines(1, 2000, "x"));
        Put("r.txt", Lines(1, 2000, "x").Replace("x 1999", "x  1999"));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var classifier = new ReasonClassifier(maxClassifyBytes: 1); // streamed
        long ll = new FileInfo(P("l.txt")).Length, rl = new FileInfo(P("r.txt")).Length;
        Assert.ThrowsAny<OperationCanceledException>(() => classifier.Classify(P("l.txt"), ll, P("r.txt"), rl, stop.Token));
        Assert.Equal(ChangeReason.Whitespace, classifier.Classify(P("l.txt"), ll, P("r.txt"), rl));

        Assert.ThrowsAny<OperationCanceledException>(() => GiantFileDiffer.Diff(P("l.txt"), P("r.txt"), ct: stop.Token));
    }

    [Fact]
    public void ARenameTakenAsDoneByLikeness_SaysSo()
    {
        // A pure rename p.c -> q/p.c; the target has no p.c, and a q/p.c much like it but not it.
        Put("L/p.c", Lines(1, 20, "pure"));
        Put("R/q/p.c", Lines(1, 20, "pure"));
        Put("C/q/p.c", Lines(1, 16, "pure") + Lines(1, 4, "other"));
        var report = new DirectoryComparer(new CompareOptions { Cache = CacheMode.Off }).Compare(P("L"), P("R"));
        var f = ChangePorter.Run(report, P("L"), P("R"), P("C"), write: false).Files.Single(x => x.Path == "q/p.c");
        Assert.Equal(PortStatus.Already, f.Status);
        Assert.Contains("taken as renamed by an earlier run", f.Note);

        // Its exact bytes need no such word.
        Put("C/q/p.c", Lines(1, 20, "pure"));
        f = ChangePorter.Run(report, P("L"), P("R"), P("C"), write: false).Files.Single(x => x.Path == "q/p.c");
        Assert.Equal((PortStatus.Already, (string?)null), (f.Status, f.Note));

        // Nothing like it: a conflict, said plainly.
        Put("C/q/p.c", "someone else's\n");
        f = ChangePorter.Run(report, P("L"), P("R"), P("C"), write: false).Files.Single(x => x.Path == "q/p.c");
        Assert.Equal(PortStatus.Conflict, f.Status);
        Assert.Contains("is not the renamed file", f.Note);
    }
}
