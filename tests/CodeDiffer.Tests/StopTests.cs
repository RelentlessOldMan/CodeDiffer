using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Report;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The merge views, the merge overlay, the HTML report of a compare3 and an apply can be stopped; and a write that fails
/// part way (a full disk) leaves no temp behind.
/// </summary>
public sealed class StopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-stop-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };
    private static readonly CancellationToken Stopped = new(canceled: true);

    public StopTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), new UTF8Encoding(false).GetBytes(text));
    }

    private static string Lines(string tag, int changed) =>
        string.Concat(Enumerable.Range(1, 40).Select(i => i == changed ? $"{tag} {i}\n" : $"line {i}\n"));

    /// <summary>A saved compare3 of one file both sides changed apart (a clean merge) and one they changed alike (a conflict).</summary>
    private Compare3Session Saved3()
    {
        Put("B/m.c", Lines("base", 0)); Put("V1/m.c", Lines("v1", 5)); Put("V2/m.c", Lines("v2", 30));
        Put("B/c.c", Lines("base", 0)); Put("V1/c.c", Lines("v1", 9)); Put("V2/c.c", Lines("v2", 9));
        var s = new SessionStore(resultsRoot: P("results")).Start3(P("B"), P("V1"), P("V2"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Null(s.Error);
        return s;
    }

    [Fact]
    public void TheMergeViews_Stop()
    {
        var s = Saved3();
        foreach (var e in s.Report!.Entries)
        {
            Assert.NotNull(TreeMerger.MergedText(e, s.Base, s.V1, s.V2));
            Assert.ThrowsAny<OperationCanceledException>(() => TreeMerger.MergedText(e, s.Base, s.V1, s.V2, Stopped));
            Assert.ThrowsAny<OperationCanceledException>(() => TreeMerger.MergedBytes(e, s.Base, s.V1, s.V2, out _, Stopped));
            Assert.ThrowsAny<OperationCanceledException>(() => TreeMerger.MergeDiff(e, s.Base, s.V1, s.V2, ct: Stopped));
        }
        Assert.ThrowsAny<OperationCanceledException>(() => ThreeWayViews.FileDiff(s, "m.c", ct: Stopped));
    }

    [Fact]
    public void AStoppedOverlay_StaysIncomplete_AndIsNeverApplied()
    {
        var s = Saved3();
        var dir = P("overlay");
        Assert.ThrowsAny<OperationCanceledException>(() => MergeOverlay.Write(s.Report!, s.Base, s.V1, s.V2, dir, ct: Stopped));
        Assert.True(File.Exists(Path.Combine(dir, "INCOMPLETE.txt")));
        Assert.False(File.Exists(Path.Combine(dir, "OVERLAY.txt")));
        Assert.Throws<ArgumentException>(() => OverlayApplier.Run(dir, s.V1, write: false));
        // Written again into a new directory, it is whole.
        var o = MergeOverlay.Write(s.Report!, s.Base, s.V1, s.V2, P("overlay2"));
        Assert.Equal(1, o.Merged);
        Assert.Equal(1, o.Markers);
        Assert.False(File.Exists(Path.Combine(o.Dir, "INCOMPLETE.txt")));
    }

    [Fact]
    public void AStoppedCompare3Report_ListsTheMergesNotRendered()
    {
        var s = Saved3();
        var r = HtmlReport.Write(s, ct: Stopped);
        Assert.Equal(2, r.Stopped);
        Assert.Equal(0, r.Rendered);
        Assert.True(File.Exists(r.IndexPath));
    }

    [Fact]
    public void AStoppedApply_ReachesNoFile_AndSaysSo()
    {
        Put("L/a.c", Lines("line", 0));
        Put("R/a.c", Lines("new", 3));
        Put("T/a.c", Lines("line", 0));
        var s = new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        var text = AgentViews.Apply(s, P("T"), write: true, ct: Stopped);
        Assert.Contains("CANCELLED", text);
        Assert.Contains("1 not reached", text);
        Assert.Equal(Lines("line", 0), File.ReadAllText(P("T/a.c")));
    }

    [Fact]
    public void AResultFileWrittenPartWay_LeavesNoTemp()
    {
        Directory.CreateDirectory(P("r"));
        var path = P("r/compare.json");
        var tmp = path + ".tmp";
        Assert.Throws<IOException>(() => ResultStore.Replace(tmp, path, new FileStream(tmp, FileMode.Create, FileAccess.Write), fs =>
        {
            fs.Write("{\"half"u8);
            throw new IOException("There is not enough space on the disk.");
        }));
        Assert.False(File.Exists(tmp));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(P("r")));
    }
}
