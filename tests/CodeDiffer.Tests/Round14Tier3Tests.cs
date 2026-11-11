using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Code review pass 14, tier 3: a whitespace-only last line without a line ending, added or taken away, is a line (content),
/// whole and streamed, and a whitespace record's hunks keep every line where it is; a 3-way merge of large files can be
/// stopped; two runs never share a result id, in one process or two.
/// </summary>
public sealed class Round14Tier3Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r14c-" + Guid.NewGuid().ToString("N"));

    public Round14Tier3Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private string Classify(string left, string right, bool streamed)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        string l = P($"{id}.l.txt"), r = P($"{id}.r.txt");
        File.WriteAllBytes(l, new UTF8Encoding(false).GetBytes(left));
        File.WriteAllBytes(r, new UTF8Encoding(false).GetBytes(right));
        var c = streamed ? new ReasonClassifier(0) : new ReasonClassifier();
        return c.Classify(l, new FileInfo(l).Length, r, new FileInfo(r).Length).ToString().ToLowerInvariant();
    }

    // ---- a blank last line without a line ending ----

    [Theory]
    [InlineData("b.\n", "b.\n ", "content")]          // a line added
    [InlineData("b.\n \t", "b.\n", "content")]        // taken away
    [InlineData("", "  ", "content")]
    [InlineData("b.\n ", "b.\n   ", "whitespace")]    // the same line, wider
    [InlineData("b. \n", "b.\n", "whitespace")]       // trailing spaces
    [InlineData("a\n  \nb\n", "a\n\nb\n", "whitespace")] // a blank line inside the file, its spaces taken out
    public void ABlankLastLine_IsALine(string left, string right, string want)
    {
        Assert.Equal(want, Classify(left, right, streamed: false));
        Assert.Equal(want, Classify(left, right, streamed: true));
    }

    [Fact]
    public void AWhitespaceRecord_ThatAddsALine_FailsTheCheck()
    {
        string[] a = ["b."], b = ["b.", " "];
        Assert.NotNull(HunkApplier.Problem(a, b, [new Hunk(HunkOp.Insert, 1, 0, 2, 1)], whitespaceOnly: true));
        Assert.Null(HunkApplier.Problem(a, b, [new Hunk(HunkOp.Insert, 1, 0, 2, 1)])); // a content record may
        Assert.Null(HunkApplier.Problem(["  x"], ["x"], [new Hunk(HunkOp.Replace, 1, 1, 1, 1)], whitespaceOnly: true));
    }

    // ---- a 3-way merge stopped ----

    [Fact]
    public void AThreeWayMerge_CanBeStopped()
    {
        const int n = 70_000;
        var b = Enumerable.Range(0, n).Select(i => $"line {i}").ToList();
        var v1 = b.Select((l, i) => i % 3 == 0 ? l + " v1" : l).ToList();
        var v2 = b.Select((l, i) => i % 3 == 1 ? l + " v2" : l).ToList();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => ThreeWayMerger.Regions(b, v1, v2, stop.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ThreeWayMerger.Merge("f", b, v1, v2, stop.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    // ---- a result directory named alike ----

    [Fact]
    public void RunsTakingOneIdAtOnce_OnlyOneGetsIt()
    {
        var root = P("results");
        string[] trees = [P("L"), P("R")];
        var dirs = new string?[16];
        // All in one second: across two, each stamp's claim sees the other's and both give the id up (a run then picks again).
        SpinWait.SpinUntil(() => DateTime.Now.Millisecond < 300);
        Parallel.For(0, dirs.Length, i => dirs[i] = ResultStore.CreateRunDir(root, "beef", "compare", trees));
        var got = Assert.Single(dirs, d => d is not null);
        Assert.True(File.Exists(Path.Combine(got!, "compare.json")));
        Assert.Equal(got, ResultStore.Find(root, "beef"));
        Assert.Single(Directory.GetDirectories(root)); // the others gave theirs up
    }

    [Fact]
    public void AnIdAnotherRunHolds_IsGivenUp_EvenFromAnotherSecond()
    {
        var root = P("results");
        var theirs = Directory.CreateDirectory(Path.Combine(root, "20200101-000000-beef")).FullName;
        Assert.Null(ResultStore.CreateRunDir(root, "beef", "compare", [P("L"), P("R")]));
        Assert.Equal([theirs], Directory.GetDirectories(root));

        // An older release's second run of an id: the highest n is the newest, -10 above -9.
        Directory.CreateDirectory(Path.Combine(root, "20200101-000000-beef-9"));
        var ten = Directory.CreateDirectory(Path.Combine(root, "20200101-000000-beef-10")).FullName;
        Assert.Equal(ten, ResultStore.Find(root, "beef"));
    }

    [Fact]
    public void SessionsInTwoStores_OnOneResultsDir_NeverShareAnId()
    {
        var root = P("results");
        var a = new SessionStore(resultsRoot: root);
        var b = new SessionStore(resultsRoot: root);
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 64, i => ids.Add((i % 2 == 0 ? a : b).Adopt(P("L"), P("R"), new CompareOptions(), new CompareReport([]), DateTime.UtcNow, TimeSpan.Zero).Id));
        Assert.Equal(64, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ids, id => Assert.NotNull(ResultStore.Find(root, id)));
    }
}
