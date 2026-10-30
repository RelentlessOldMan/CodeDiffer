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
/// stopped; two runs naming a result directory alike never share it.
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
    public void RunsNamingADirectoryAlike_EachGetTheirOwn()
    {
        var root = P("results");
        string[] trees = [P("L"), P("R")];
        var dirs = new string[16];
        Parallel.For(0, dirs.Length, i => dirs[i] = ResultStore.CreateRunDir(root, "beef", "compare", trees));
        Assert.Equal(dirs.Length, dirs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(dirs, d => Assert.True(File.Exists(Path.Combine(d, "compare.json"))));
    }
}
