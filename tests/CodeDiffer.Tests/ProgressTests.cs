using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Progress and partial answers of a running compare: the counters the comparer keeps, the differences it
/// publishes as it finds them, and the agent views over a compare that hasn't finished (held open by the test).
/// </summary>
public class ProgressTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-progress-" + Guid.NewGuid().ToString("N"));
    private readonly string _left, _right;
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public ProgressTests()
    {
        _left = Path.Combine(_dir, "L");
        _right = Path.Combine(_dir, "R");
        Environment.SetEnvironmentVariable("CODEDIFFER_OUT_DIR", Path.Combine(_dir, "out"));
        for (int i = 0; i < 20; i++)
        {
            Put($"L/src/s{i}.c", $"same {i}\n");
            Put($"R/src/s{i}.c", $"same {i}\n");
        }
        for (int i = 0; i < 3; i++)
        {
            Put($"L/src/m{i}.c", "one\naaa\nthree\n");
            Put($"R/src/m{i}.c", "one\nbbb\nthree\n"); // same size, different bytes
        }
        Put("L/grow.txt", "short\n");
        Put("R/grow.txt", "a good deal longer\n");
        var moved = string.Concat(Enumerable.Range(1, 40).Select(i => $"moved line {i}\n"));
        Put("L/old/moved.c", moved);
        Put("R/new/moved.c", moved); // a pure rename
        Put("L/gone.h", "x\n");
        Put("R/new.h", "y\n");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEDIFFER_OUT_DIR", null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Put(string rel, string text)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(text));
    }

    [Fact]
    public void Compare_FillsProgress_AndFoundMatchesTheReport()
    {
        var p = new CompareProgress();
        var r = new DirectoryComparer(NoCache).Compare(_left, _right, p);

        Assert.Equal(ComparePhase.Done, p.Phase);
        Assert.Equal(26, p.ListedLeft);
        Assert.Equal(26, p.ListedRight);
        Assert.Equal(23, p.SameSizePairs); // 20 identical + 3 same-size edits
        Assert.Equal(23, p.PairsDone);
        Assert.Equal(p.SameSizeBytes, p.BytesDone);
        Assert.Equal(r.BytesRead, p.BytesRead);

        var found = p.Found();
        var modified = found.Where(f => f.Change.Status == ChangeStatus.Modified).ToList();
        Assert.All(modified, f => Assert.False(f.MayBeRename));
        Assert.Equal(
            r.Changes.Where(c => c.Status == ChangeStatus.Modified).Select(c => c.RelativePath).Order(),
            modified.Select(f => f.Change.RelativePath).Order());
        Assert.All(modified, f => Assert.Equal(ChangeReason.Content, f.Change.Reason));

        // Adds/removes are published right after the walk, flagged: the rename is only matched at the end.
        var tentative = found.Where(f => f.MayBeRename).Select(f => (f.Change.Status, f.Change.RelativePath)).Order().ToList();
        Assert.Equal([(ChangeStatus.Added, "new.h"), (ChangeStatus.Added, "new/moved.c"),
                      (ChangeStatus.Removed, "gone.h"), (ChangeStatus.Removed, "old/moved.c")], tentative);
        Assert.Contains(r.Changes, c => c.Status == ChangeStatus.Renamed && c.RenamedFrom == "old/moved.c");
    }

    [Fact]
    public void RunningCompare_ViewsShowWhatIsFoundSoFar()
    {
        var p = new CompareProgress();
        var report = new DirectoryComparer(NoCache).Compare(_left, _right, p);
        var done = new TaskCompletionSource<CompareReport>();
        var s = new CompareSession("t001", _left, _right, NoCache, p, done.Task);

        var sum = AgentViews.Summary(s);
        Assert.Contains("running · ", sum);
        Assert.Contains("now: ", sum);
        Assert.Contains("found so far: 4 modified · 2 added · 2 removed", sum);
        Assert.Contains("may still turn out to be one rename", sum);
        Assert.Contains("list_files and get_file_diff work on what is found so far", sum);

        var list = AgentViews.ListFiles(s, lines: true);
        Assert.Contains("RUNNING, partial: 8 difference(s) found so far", list);
        Assert.Contains("8 file(s) match so far", list);
        Assert.Contains("M src/m0.c  [content]", list);
        Assert.Contains("+1 -1 (1 hunk(s))", list);
        Assert.Contains("A new/moved.c", list);
        Assert.Contains("(may still pair into a rename)", list);
        Assert.False(s.DiffInfo.ContainsKey("new/moved.c")); // its final diff may be a rename: not remembered
        Assert.Contains("partial: the list grows", list);

        var diff = AgentViews.FileDiff(s, "src/m1.c");
        Assert.Contains("+bbb", diff);
        Assert.Contains("note: the compare is still running\n", diff);
        Assert.Contains("may still pair into a rename", AgentViews.FileDiff(s, "new/moved.c"));
        Assert.Contains("not among the 8 difference(s) found so far", AgentViews.FileDiff(s, "src/s0.c"));
        Assert.Contains("needs the finished compare", AgentViews.Stats(s));

        done.SetResult(report);
        Assert.Contains("(finished in", AgentViews.Summary(s));
        var final = AgentViews.ListFiles(s);
        Assert.Contains("R old/moved.c -> new/moved.c", final);
        Assert.DoesNotContain("RUNNING", final);
        Assert.Contains("identical", AgentViews.FileDiff(s, "src/s0.c"));
    }

    [Fact]
    public void ProgressLine_FollowsThePhases()
    {
        var p = new CompareProgress();
        Assert.Equal("starting", ProgressView.Line(p));
        p.SetPhase(ComparePhase.Walking);
        p.Listed(true, 5);
        p.Listed(false, 7);
        Assert.Equal("listing files · 5 left · 7 right", ProgressView.Line(p));

        p.Paired(12, 4, 4096);
        p.SetPhase(ComparePhase.Contents);
        p.PairChecked(1024, 2048, 0);
        p.PairChecked(1024, 0, 2);
        p.Add(new FileChange("a.c", ChangeStatus.Modified, ChangeReason.Content, 1024, 1024));
        var line = ProgressView.Line(p);
        Assert.Contains("checking contents · 2/4 same-size files · 2.0 KB of 4.0 KB · 2.0 KB read", line);
        Assert.Contains("2 from cache", line);
        Assert.EndsWith("1 difference(s) found so far", line);
        Assert.Null(p.Remaining()); // too early to estimate

        // All from cache: byte totals would mislead (biggest go first and finish at once), so only files show.
        var warm = new CompareProgress();
        warm.Paired(12, 4, 4096);
        warm.SetPhase(ComparePhase.Contents);
        warm.PairChecked(3072, 0, 2);
        Assert.Equal("checking contents · 1/4 same-size files · 0 B read · 2 from cache · 0 difference(s) found so far", ProgressView.Line(warm));
        Assert.Equal("checking contents · 1/4 same-size files · 0 B read · 2 from cache", ProgressView.Line(warm, found: false));
    }

    [Fact]
    public void RunningCompare3_NamesThePathsBothSidesChanged()
    {
        var b = Path.Combine(_dir, "B");
        var v1 = Path.Combine(_dir, "V1");
        var v2 = Path.Combine(_dir, "V2");
        foreach (var n in new[] { "a", "b", "c" })
            foreach (var d in new[] { "B", "V1", "V2" })
                Put($"{d}/{n}.c", $"{n} 1\n{n} 2\n{n} 3\n");
        Put("V1/a.c", "a 1\nA 2\na 3\n");
        Put("V1/b.c", "B 1\nb 2\nb 3\n");
        Put("V2/b.c", "b 1\nb 2\nB 3\n");
        Put("V2/c.c", "c 1\nC 2\nc 3\n");

        var p1 = new CompareProgress();
        var p2 = new CompareProgress();
        var done = new TaskCompletionSource<ThreeWayReport>();
        var s = new Compare3Session("t002", b, v1, v2, NoCache, p1, p2, done.Task);

        new DirectoryComparer(NoCache).Compare(b, v1, p1);
        var first = ThreeWayViews.Summary(s);
        Assert.Contains("base->v1: done · 2 changed", first);
        Assert.Contains("base->v2: waits for base->v1", first);
        Assert.Contains("so far: v1 changed 2 (complete)", first);

        new DirectoryComparer(NoCache).Compare(b, v2, p2);
        var second = ThreeWayViews.Summary(s);
        Assert.Contains("changed on both sides 1", second);
        Assert.Contains("  both: b.c\n", second);
        Assert.Contains("now: classifying and merging", second);
        Assert.Contains("get_summary shows progress", ThreeWayViews.ListFiles(s));

        done.SetResult(TreeMerger.Run(b, v1, v2, NoCache));
        Assert.Contains("merged", ThreeWayViews.Summary(s));
        Assert.DoesNotContain("running", ThreeWayViews.Summary(s));
    }
}
