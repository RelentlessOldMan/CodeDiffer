using System.Diagnostics;
using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.Verify;
using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Review round 11, tier 1: verify refuses a 3-way manifest whose clean merge drops a side's edit, a clean edit of each
/// side passed off as one conflict, and abutting edits called clean; a replace spanning an unchanged line is not
/// canonical; identical files moved by the thousand pair in linear time; an output gate sees a tree through a share; a
/// path is looked up as given before it is trimmed; a file symlink in a target is never acted through.
/// </summary>
public sealed class Round11Tier1Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r11a-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public Round11Tier1Tests() => Directory.CreateDirectory(_dir);

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

    private static string Lines(int n, Func<int, string>? edit = null)
        => string.Concat(Enumerable.Range(1, n).Select(i => (edit?.Invoke(i) ?? $"line {i}") + "\n"));

    /// <summary>Three trees of one file f.txt, base of 30 distinct lines, each side editing the lines given.</summary>
    private void ThreeTrees(int[] v1Edits, int[] v2Edits)
    {
        Put("B/f.txt", Lines(30));
        Put("V1/f.txt", Lines(30, i => v1Edits.Contains(i) ? $"v1 edit {i}" : null));
        Put("V2/f.txt", Lines(30, i => v2Edits.Contains(i) ? $"v2 edit {i}" : null));
    }

    private ConflictCrossCheckResult Check(IReadOnlyList<Conflict> conflicts, IReadOnlyList<CleanMerge> clean)
        => ConflictTreeCrossCheck.Run(P("B"), P("V1"), P("V2"), new ConflictManifest(1, "V1", "V2", conflicts, clean, ConflictTruthSha: null));

    private static Conflict At(int line, int lines = 1)
        => new("f.txt", line, lines, HunkOp.Replace, line, lines, HunkOp.Replace, line, lines);

    [Fact]
    public void AManifestFromCodeDiffersOwnMerge_PassesWithoutItsDigest()
    {
        ThreeTrees([3, 20], [7, 20]);
        var mine = ThreeWayMerger.Merge("f.txt", File.ReadAllText(P("B/f.txt")), File.ReadAllText(P("V1/f.txt")), File.ReadAllText(P("V2/f.txt")));
        var r = Check(mine.Conflicts, mine.CleanMerges);
        Assert.False(r.DecompositionMatches); // no digest stated: the fallback decides
        Assert.True(r.Ok, string.Join("; ", r.UnsoundRegions));
    }

    [Fact]
    public void ACleanMergeThatDropsTheOtherSidesEdit_Fails()
    {
        ThreeTrees([3, 20], [7, 20]);
        // v1's clean region stretched over v2's line 7, and v2's edit there recorded nowhere.
        var r = Check([At(20)], [new CleanMerge("f.txt", "v1", HunkOp.Replace, 3, 5, 3, 5)]);
        Assert.False(r.Ok);
        Assert.False(r.Files.Single().V2Reconstructs);
    }

    [Fact]
    public void TwoCleanEditsPassedOffAsOneConflict_Fail()
    {
        ThreeTrees([3, 20], [7, 20]);
        var r = Check([At(3, 5), At(20)], []);
        Assert.False(r.Ok);
        Assert.Contains(r.UnsoundRegions, u => u.Contains("f.txt:3,5") && u.Contains("merge cleanly"));
    }

    [Fact]
    public void AbuttingEditsCalledClean_Fail()
    {
        ThreeTrees([5, 20], [6, 20]);
        var r = Check([At(20)], [new CleanMerge("f.txt", "v1", HunkOp.Replace, 5, 1, 5, 1), new CleanMerge("f.txt", "v2", HunkOp.Replace, 6, 1, 6, 1)]);
        Assert.False(r.Ok);
        Assert.Contains(r.UnsoundRegions, u => u.Contains("abuts"));
    }

    [Fact]
    public void AConflictAmongRepeatedLines_IsNotSplit()
    {
        // Edits of lines 2 and 3 in a run of identical lines abut: one conflict, whatever alignment is tried.
        Put("B/f.txt", "a\na\na\na\nend\n");
        Put("V1/f.txt", "a\nX\na\na\nend\n");
        Put("V2/f.txt", "a\na\nY\na\nend\n");
        var r = Check([new Conflict("f.txt", 2, 2, HunkOp.Replace, 2, 2, HunkOp.Replace, 2, 2)], []);
        Assert.Empty(r.UnsoundRegions);
        Assert.True(r.Reconstructs);
    }

    [Fact]
    public void AReplaceSpanningAnUnchangedLine_IsNotCanonical()
    {
        string[] old = Lines(20).TrimEnd('\n').Split('\n');
        string[] @new = Lines(20, i => i is 16 or 18 ? $"edited {i}" : null).TrimEnd('\n').Split('\n');
        Assert.Contains("unchanged", HunkApplier.Problem(old, @new, [new Hunk(HunkOp.Replace, 16, 3, 16, 3)]));
        Assert.Null(HunkApplier.Problem(old, @new, [new Hunk(HunkOp.Replace, 16, 1, 16, 1), new Hunk(HunkOp.Replace, 18, 1, 18, 1)]));
    }

    [Fact]
    public void ThousandsOfIdenticalFilesMoved_PairQuicklyAndByName()
    {
        const int n = 3000;
        for (int i = 0; i < n; i++)
        {
            Put($"L/old/f{i:D4}.txt", "same\n");
            Put($"R/new/f{i:D4}.txt", "same\n");
        }
        var sw = Stopwatch.StartNew();
        var s = new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(2)));
        Assert.Null(s.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}"); // was minutes, and tens of GB
        var renames = s.Report!.Changes.Where(c => c.Status == ChangeStatus.Renamed).ToList();
        Assert.Equal(n, renames.Count);
        Assert.All(renames, c => Assert.Equal(Path.GetFileName(c.RenamedFrom!), Path.GetFileName(c.RelativePath)));
    }

    [Fact]
    public void AnOutputGate_SeesATreeThroughALocalShare()
    {
        if (!OperatingSystem.IsWindows() || _dir.Length < 3 || _dir[1] != ':') return;
        Directory.CreateDirectory(P("R"));
        var viaShare = $@"\\localhost\{_dir[0]}$\{_dir[3..]}\R";
        if (!Directory.Exists(viaShare)) return; // no admin share here
        Assert.True(ResultStore.Overlaps(Path.Combine(viaShare, "x.patch"), P("R"), oneWay: true));
        Assert.True(ResultStore.Overlaps(P("R"), viaShare));
        Assert.False(ResultStore.Overlaps($@"\\localhost\{_dir[0]}$\{_dir[3..]}\elsewhere\x.patch", P("R"), oneWay: true));
    }

    [Fact]
    public void APathIsLookedUpAsGiven_BeforeItIsTrimmed()
    {
        if (!OperatingSystem.IsWindows()) return;
        Put("L/ a.txt", "one\n");
        Put("R/ a.txt", "one changed\n");
        Put("L/a.txt", "two\n");
        Put("R/a.txt", "two changed\n");
        var s = new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Contains("+one changed", AgentViews.FileDiff(s, " a.txt"));
        Assert.Contains("+two changed", AgentViews.FileDiff(s, "a.txt"));
        Assert.Contains("+two changed", AgentViews.FileDiff(s, "a.txt ")); // a stray space still finds the file
        Assert.True(AgentViews.Glob(" a.txt")(" a.txt"));
        Assert.False(AgentViews.Glob(" a.txt")("a.txt"));
    }

    [Fact]
    public void AFileSymlinkInATarget_IsALinkOnTheWay()
    {
        Put("T/real.txt", "x\n");
        try { File.CreateSymbolicLink(P("T/link.txt"), P("T/real.txt")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no symlinks without admin/developer mode
        var guard = new LinkGuard(P("T"));
        Assert.Equal(P("T/link.txt"), guard.LinkOnTheWay(P("T/link.txt")));
        Assert.Null(guard.LinkOnTheWay(P("T/real.txt")));
        Assert.Null(guard.LinkOnTheWay(P("T/new.txt")));
        File.Delete(P("T/real.txt")); // dangling: still a link
        Assert.Equal(P("T/link.txt"), new LinkGuard(P("T")).LinkOnTheWay(P("T/link.txt")));
    }
}
