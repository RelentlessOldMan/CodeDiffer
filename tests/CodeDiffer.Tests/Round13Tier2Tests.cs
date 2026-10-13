using System.Diagnostics;
using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Report;
using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Review round 13, tier 2: a backslash that escapes nothing (a C# verbatim string, PowerShell, batch) never turns a change
/// inside a string into whitespace; a capped diff paged by several answers at once, and reports written at once, never
/// collide; files a case-sensitive tree tells apart by case get their own saved diffs and an ambiguous name is answered.
/// </summary>
public sealed class Round13Tier2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r13b-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public Round13Tier2Tests() => Directory.CreateDirectory(_dir);

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

    private static CompareSession Done(CompareSession s)
    {
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Null(s.Error);
        return s;
    }

    private static string Saved(string answer, string label = "full patch: ")
    {
        var line = answer.Split('\n').Single(l => l.Contains(label));
        return line[(line.IndexOf(label, StringComparison.Ordinal) + label.Length)..];
    }

    // ---- A2: a backslash that escapes nothing ----

    [Theory]
    [InlineData("var p = @\"C:\\temp\\\"; Log(\"a, b\");\n", "var p = @\"C:\\temp\\\"; Log(\"a,b\");\n", "content")]   // C# verbatim
    [InlineData("Join-Path \"C:\\Temp\\\" \"a, b\"\n", "Join-Path \"C:\\Temp\\\" \"a,b\"\n", "content")]                  // PowerShell
    [InlineData("set D=\"C:\\x\\\" & echo \"a  b\"\n", "set D=\"C:\\x\\\" & echo \"a b\"\n", "whitespace")]                // a run narrowed: either way
    [InlineData("s = \"say \\\"hi\\\"\";  x=1;\n", "s = \"say \\\"hi\\\"\"; x = 1;\n", "whitespace")]                       // escapes, spacing outside
    [InlineData("s = \"a\\\" , b\";\n", "s = \"a\\\" ,b\";\n", "content")]                                                  // in a string as an escape reads it
    public void ABackslashThatMayEscapeNothing_NeverHidesAChangeInAString(string left, string right, string want)
    {
        Put("l.txt", left);
        Put("r.txt", right);
        long ll = new FileInfo(P("l.txt")).Length, rl = new FileInfo(P("r.txt")).Length;
        Assert.Equal(want, new ReasonClassifier().Classify(P("l.txt"), ll, P("r.txt"), rl).ToString().ToLowerInvariant());
        Assert.Equal(want, new ReasonClassifier(0).Classify(P("l.txt"), ll, P("r.txt"), rl).ToString().ToLowerInvariant()); // streamed
        Assert.Equal(want == "whitespace", TextInspector.SameButWhitespace(left, right));
    }

    [Fact]
    public void AWhitespaceRecord_ChangingAVerbatimString_FailsTheCheck()
    {
        string[] a = ["var p = @\"C:\\temp\\\"; Log(\"a, b\");"], b = ["var p = @\"C:\\temp\\\"; Log(\"a,b\");"];
        Assert.NotNull(HunkApplier.Problem(a, b, [new Hunk(HunkOp.Replace, 1, 1, 1, 1)], whitespaceOnly: true));
    }

    // ---- C1: written at once ----

    [Fact]
    public void ACappedDiffPagedByManyAnswersAtOnce_NeverCollides()
    {
        string Many(string tag) => string.Concat(Enumerable.Range(1, 3000).Select(i => i % 3 == 0 ? $"{tag} {i}\n" : $"line {i}\n"));
        Put("L/big.txt", Many("old"));
        Put("R/big.txt", Many("new"));
        var s = Done(new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache));
        var file = Saved(AgentViews.FileDiff(s, "big.txt", maxLines: 50));
        // Pages asked for while the saved patch is read, as an agent opens it between pages.
        var answers = new string[64];
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                try { Assert.Contains("+new 3000\n", File.ReadAllText(file)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // replaced as it opened
                finally { Thread.Sleep(1); }
        })).ToArray();
        Parallel.For(0, answers.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => answers[i] = AgentViews.FileDiff(s, "big.txt", maxLines: 50, startLine: 1 + i % 20 * 50));
        stop.Cancel();
        Task.WaitAll(readers);
        Assert.All(answers, a => Assert.Equal(file, Saved(a)));
        Assert.Contains("+new 3000\n", File.ReadAllText(file));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "*.tmp"));
    }

    [Fact]
    public void ReportsWrittenAtOnce_AllSucceed()
    {
        Put("L/a.txt", "one\n");
        Put("R/a.txt", "two\n");
        var s = Done(new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache));
        var results = new HtmlReportResult[4];
        Parallel.For(0, results.Length, i => results[i] = HtmlReport.Write(s));
        Assert.All(results, r => Assert.True(File.Exists(r.IndexPath)));
    }

    // ---- C2: names a case-sensitive tree tells apart ----

    /// <summary>Makes a directory case-sensitive (Windows 10+, no admin), or false where it can't be.</summary>
    private static bool CaseSensitive(string dir)
    {
        Directory.CreateDirectory(dir);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("fsutil", $"file setCaseSensitiveInfo \"{dir}\" enable")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    [Fact]
    public void FilesNamedAlikeButForCase_EachGetTheirOwnSavedDiff_AndAnAmbiguousNameIsAnswered()
    {
        if (!OperatingSystem.IsWindows() || !CaseSensitive(P("L")) || !CaseSensitive(P("R"))) return;
        string Many(string tag) => string.Concat(Enumerable.Range(1, 60).Select(i => i % 3 == 0 ? $"{tag} {i}\n" : $"line {i}\n"));
        foreach (var name in new[] { "Foo.c", "foo.c" })
        {
            Put($"L/{name}", Many("old"));
            Put($"R/{name}", Many("new " + name));
        }
        var s = Done(new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache));
        var upper = Saved(AgentViews.FileDiff(s, "Foo.c", maxLines: 5));
        var lower = Saved(AgentViews.FileDiff(s, "foo.c", maxLines: 5));
        Assert.NotEqual(upper, lower, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("+new Foo.c 3\n", File.ReadAllText(upper));
        Assert.Contains("+new foo.c 3\n", File.ReadAllText(lower));

        var ambiguous = AgentViews.FileDiff(s, "FOO.C");
        Assert.Contains("differs only in case", ambiguous);
        Assert.Contains("Foo.c", ambiguous);
        Assert.Contains("foo.c", ambiguous);
    }

    [Fact]
    public void ANameButForCase_OfOneFile_StillFindsIt()
    {
        string[] paths = ["src/Main.cs", "src/util.cs"];
        Assert.Equal("src/Main.cs", AgentViews.ByCase(paths, p => p, "SRC/main.CS"));
        Assert.Null(AgentViews.CaseAmbiguous(paths, "SRC/main.CS"));
        Assert.Null(AgentViews.ByCase(["a/X.c", "a/x.c"], p => p, "a/x.C"));
        Assert.NotNull(AgentViews.CaseAmbiguous(["a/X.c", "a/x.c"], "a/x.C"));
    }
}
