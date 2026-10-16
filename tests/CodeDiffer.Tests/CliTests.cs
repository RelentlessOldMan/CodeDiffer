using System.Text;
using System.Text.RegularExpressions;

namespace CodeDiffer.Tests;

/// <summary>
/// The codediffer command (CodeDiffer.Cli.exe) run as a child process, as a user runs it: every command, its usage errors
/// (exit 64), its error exits (2), done-but-incomplete (3) and conflicts (1); and that a command that only reads never
/// writes into the trees it reads. Each test has its own fixture trees and its own results / ledger / overflow dirs (set
/// in the child's environment), so nothing touches %LOCALAPPDATA%\CodeDiffer.
/// </summary>
public sealed class CliTests : IDisposable
{
    private readonly TempDir _t = new();
    private string State => _t.P("state");

    public void Dispose() => _t.Dispose();

    private ExeResult Cli(params string[] args) => ExeRunner.Run(ExeRunner.Exe("CodeDiffer.Cli.exe"), _t.Root, State, args);

    private static string Id(ExeResult r)
    {
        var m = Regex.Match(r.Stdout + r.Stderr, @"\(id ([0-9a-f]{4})\)");
        Assert.True(m.Success, r.ToString());
        return m.Groups[1].Value;
    }

    /// <summary>L vs R: one file of each status, and a modified file for each reason.</summary>
    private void TwoWay()
    {
        _t.Put("L/same.txt", "a\nb\nc\n");
        _t.Put("R/same.txt", "a\nb\nc\n");
        _t.Put("L/content.txt", "one\ntwo\nthree\n");
        _t.Put("R/content.txt", "one\nTWO\nthree\n");
        _t.Put("L/eol.txt", "x\ny\n");
        _t.Put("R/eol.txt", "x\r\ny\r\n");
        _t.Put("L/ws.txt", "p  q\nr\n");
        _t.Put("R/ws.txt", "p q\nr\n");
        _t.PutBytes("L/blob.bin", [0, 1, 2, (byte)'b', (byte)'i', (byte)'n']);
        _t.PutBytes("R/blob.bin", [0, 1, 3, (byte)'b', (byte)'i', (byte)'n']);
        _t.Put("L/enc.txt", "hello\nworld\n");
        _t.PutBytes("R/enc.txt", [0xEF, 0xBB, 0xBF, .. Encoding.ASCII.GetBytes("hello\nworld\n")]);
        _t.Put("L/gone.txt", "gone\n");
        _t.Put("R/fresh.txt", "fresh\n");
        _t.Put("L/sub/old.txt", TempDir.Lines(1, 30, "rename line"));
        _t.Put("R/sub/moved.txt", TempDir.Lines(1, 30, "rename line"));
    }

    /// <summary>B, V1, V2: one path for each 3-way outcome, and the conflict kinds content, add/add and delete/modify.</summary>
    private void ThreeWay()
    {
        var merge = TempDir.Lines(1, 20);
        _t.Put("B/merge.txt", merge);
        _t.Put("V1/merge.txt", merge.Replace("line 2\n", "line 2 v1\n"));
        _t.Put("V2/merge.txt", merge.Replace("line 18\n", "line 18 v2\n"));
        _t.Put("B/conf.txt", "c1\nc2\nc3\n");
        _t.Put("V1/conf.txt", "c1\nV1\nc3\n");
        _t.Put("V2/conf.txt", "c1\nV2\nc3\n");
        _t.Put("B/only1.txt", "o\n");
        _t.Put("V1/only1.txt", "o1\n");
        _t.Put("V2/only1.txt", "o\n");
        _t.Put("B/only2.txt", "o\n");
        _t.Put("V1/only2.txt", "o\n");
        _t.Put("V2/only2.txt", "o2\n");
        _t.Put("B/agree.txt", "g\n");
        _t.Put("V1/agree.txt", "G\n");
        _t.Put("V2/agree.txt", "G\n");
        _t.Put("V1/addadd.txt", "aa1\n");
        _t.Put("V2/addadd.txt", "aa2\n");
        _t.Put("B/delmod.txt", "dm\n");
        _t.Put("V2/delmod.txt", "dm changed\n");
        _t.Put("B/del2.txt", "d\n");
        _t.Put("V1/del2.txt", "d\n");
        foreach (var d in new[] { "B", "V1", "V2" }) _t.Put($"{d}/keep.txt", "k\n");
    }

    private void AssertUnchanged(Dictionary<string, SortedDictionary<string, string>> before)
    {
        foreach (var (tree, snap) in before) Assert.Equal(snap, TempDir.Snapshot(_t.P(tree)));
    }

    private Dictionary<string, SortedDictionary<string, string>> Snap(params string[] trees)
        => trees.ToDictionary(t => t, t => TempDir.Snapshot(_t.P(t)));

    // ---------------------------------------------------------------- version, help, usage

    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Version_PrintsTheVersion(string arg)
    {
        var r = Cli(arg);
        Assert.Equal(0, r.ExitCode);
        Assert.Matches(@"^codediffer \d+\.\d+\.\d+", r.Stdout.Trim());
        Assert.Equal("", r.Stderr);
    }

    [Theory]
    [InlineData]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_PrintsTheUsage(params string[] args)
    {
        var r = Cli(args);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("usage:", r.Stdout);
        foreach (var cmd in new[] { "compare ", "compare3 ", "report ", "results ", "diff ", "apply ", "apply-overlay ", "blockdiff ", "verify " })
            Assert.Contains("codediffer " + cmd, r.Stdout);
        Assert.Contains("exit codes:", r.Stdout);
    }

    [Fact]
    public void Diff_OfACoarseDiff_SaysSoOnStderr()
    {
        DifferReviewTests.PutCoarsePair(_t.P("C/L/big.txt"), _t.P("C/R/big.txt"));
        var r = Cli("diff", _t.P("C/L/big.txt"), _t.P("C/R/big.txt"));
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("coarse", r.Stderr);
        Assert.Contains("@@ -1,15000 +1,15000 @@", r.Stdout);
    }

    [Fact]
    public void UnknownCommand_IsAUsageError()
    {
        var r = Cli("frobnicate", "x");
        Assert.Equal(64, r.ExitCode);
        Assert.Contains("unknown command: frobnicate", r.Stderr);
        Assert.Contains("usage:", r.Stdout);
    }

    [Theory]
    // compare
    [InlineData("compare")]
    [InlineData("compare", "L")]
    [InlineData("compare", "L", "R", "--bogus")]
    [InlineData("compare", "L", "R", "extra")]
    [InlineData("compare", "L", "R", "--threads")]
    [InlineData("compare", "L", "R", "--threads", "0")]
    [InlineData("compare", "L", "R", "--threads", "many")]
    [InlineData("compare", "L", "R", "--threads", "--no-cache")]
    [InlineData("compare", "L", "R", "-U", "3")]
    [InlineData("compare", "L", "R", "--literal")]
    [InlineData("compare", "L", "R", "--patch", "-U", "-1")]
    [InlineData("compare", "L", "R", "--patch", "-U", "x")]
    [InlineData("compare", "L", "R", "--large")]
    [InlineData("compare", "L", "R", "--include-identical")]
    [InlineData("compare", "L", "R", "--max-diffs", "5")]
    [InlineData("compare", "L", "R", "--out", "rep")]
    [InlineData("compare", "L", "R", "--html", "--no-save")]
    [InlineData("compare", "L", "R", "--html", "--max-diffs", "-1")]
    [InlineData("compare", "L", "R", "--html", "--out", "L/rep")]
    [InlineData("compare", "L", "R", "--html", "--out", "R")]
    [InlineData("compare", "L", "R", "--no-chache")]
    // compare3
    [InlineData("compare3", "L", "R")]
    [InlineData("compare3", "L", "R", "R", "--threads", "0")]
    [InlineData("compare3", "L", "R", "R", "--merge-dir", "m")]
    [InlineData("compare3", "L", "R", "R", "--merge-out")]
    [InlineData("compare3", "L", "R", "R", "--large")]
    [InlineData("compare3", "L", "R", "R", "--html", "--no-save")]
    [InlineData("compare3", "L", "R", "R", "--html", "--out", "R/rep")]
    // apply, apply-overlay
    [InlineData("apply", "L", "R")]
    [InlineData("apply", "L", "R", "T", "--bogus")]
    [InlineData("apply", "L", "R", "T", "--threads", "0")]
    [InlineData("apply-overlay", "L")]
    [InlineData("apply-overlay", "--write", "L")]
    [InlineData("apply-overlay", "L", "--write")]
    [InlineData("apply-overlay", "L", "R", "--threads", "-2")]
    [InlineData("apply-overlay", "L", "R", "--bogus")]
    // diff, blockdiff
    [InlineData("diff", "L/a.txt")]
    [InlineData("diff", "L/a.txt", "R/a.txt", "--bogus")]
    [InlineData("diff", "L/a.txt", "R/a.txt", "-U", "-3")]
    [InlineData("diff", "L", "R")]
    [InlineData("diff", "/dev/null", "/dev/null")]
    [InlineData("diff", "NUL", "/dev/null")]
    [InlineData("blockdiff", "L/a.txt")]
    [InlineData("blockdiff", "L/a.txt", "R/a.txt", "extra")]
    // verify, report, results
    [InlineData("verify")]
    [InlineData("report")]
    [InlineData("report", "abcd", "--bogus")]
    [InlineData("report", "abcd", "--max-diffs")]
    [InlineData("results", "--max", "0")]
    [InlineData("results", "--max", "x")]
    [InlineData("results", "--bogus")]
    [InlineData("results", "--prune", "--kep", "1")]
    [InlineData("results", "--prune", "--keep")]
    [InlineData("results", "--prune", "--keep", "-1")]
    [InlineData("results", "--prune", "--keep", "--yes")]
    [InlineData("results", "--prune", "--older-than", "-1")]
    [InlineData("results", "--prune", "--older-than", "soon")]
    [InlineData("results", "--prune", "--older-than", "99999")]
    [InlineData("results", "--prune", "extra")]
    public void UsageError_Is64_AndRunsNothing(params string[] args)
    {
        _t.Put("L/a.txt", "a\n");
        _t.Put("R/a.txt", "b\n");
        var before = Snap("L", "R");
        var r = Cli(args);
        Assert.True(r.ExitCode == 64, r.ToString());
        Assert.NotEqual("", r.Stderr.Trim());
        AssertUnchanged(before);
        // Nothing was compared, so nothing was saved, and no report was written.
        Assert.False(Directory.Exists(Path.Combine(State, "results")) &&
                     Directory.EnumerateDirectories(Path.Combine(State, "results")).Any(d => !Path.GetFileName(d).StartsWith('.')), r.ToString());
        Assert.False(Directory.Exists(_t.P("rep")));
        Assert.False(Directory.Exists(_t.P("m")));
    }

    [Fact]
    public void ValuedFlag_MissingItsValue_SaysSo()
    {
        _t.Dir("L");
        _t.Dir("R");
        var r = Cli("compare", "L", "R", "--threads");
        Assert.Equal(64, r.ExitCode);
        Assert.Contains("--threads needs a value", r.Stderr);
        r = Cli("compare", "L", "R", "--no-chache");
        Assert.Contains("unknown argument '--no-chache'", r.Stderr);
        Assert.Contains("usage: codediffer compare", r.Stderr);
    }

    [Fact(Skip = "BUG: `compare L R --no-cache --rehash` is accepted (exit 0, runs with cache off, --rehash silently dropped) though the " +
                 "usage gives them as alternatives `[--no-cache | --rehash]`; every other conflicting/orphan flag (-U without --patch, " +
                 "--large without --html, --html with --no-save) is refused with 64")]
    public void NoCacheWithRehash_IsAUsageError()
    {
        _t.Put("L/a.txt", "a\n");
        _t.Put("R/a.txt", "b\n");
        var r = Cli("compare", "L", "R", "--no-cache", "--rehash");
        Assert.Equal(64, r.ExitCode);
    }

    // ---------------------------------------------------------------- compare

    [Fact]
    public void Compare_ListsEveryStatusAndReason_PrintsTheSummary_AndSaves()
    {
        TwoWay();
        var before = Snap("L", "R");
        var r = Cli("compare", "L", "R");
        Assert.True(r.ExitCode == 0, r.ToString());

        var changes = r.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("    ")).Select(l => l.Trim()).ToList();
        Assert.Equal(
            new[]
            {
                "A fresh.txt",
                "D gone.txt",
                "M blob.bin [binary]",
                "M content.txt [content]",
                "M enc.txt [encoding]",
                "M eol.txt [eol]",
                "M ws.txt [whitespace]",
                "R sub/old.txt -> sub/moved.txt",
            },
            changes.OrderBy(c => c, StringComparer.Ordinal));
        Assert.DoesNotContain(changes, c => c.Contains("same.txt"));

        Assert.Contains("compare: L  vs  R", r.Stdout);
        Assert.Matches(@"(?m)^  files      9\r?$", r.Stdout);
        Assert.Matches(@"(?m)^  identical  1   \(hidden\)\r?$", r.Stdout);
        Assert.Matches(@"(?m)^  added      1\r?$", r.Stdout);
        Assert.Matches(@"(?m)^  removed    1\r?$", r.Stdout);
        Assert.Matches(@"(?m)^  renamed    1   pure 1 · edited 0\r?$", r.Stdout);
        Assert.Matches(@"(?m)^  modified   5   content 1 . eol 1 . whitespace 1 . encoding 1 . binary 1\r?$", r.Stdout);
        Assert.Matches(@"(?m)^  elapsed    \d+:\d\d  \(threads \d+, cache on\)", r.Stdout);
        Assert.Matches(@"(?m)^  content    \d+ same-size pair\(s\)", r.Stdout);
        Assert.Matches(@"(?m)^  saved      .+  \(id [0-9a-f]{4}\)", r.Stdout);
        Assert.DoesNotContain("timing", r.Stderr);
        AssertUnchanged(before);

        // Saved: listed by `results`, under the isolated root.
        var id = Id(r);
        var list = Cli("results");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("1 saved compare(s)", list.Stdout);
        Assert.Contains(id, list.Stdout);
        Assert.Contains(Path.Combine(State, "results"), list.Stdout);
    }

    [Fact]
    public void Compare_NoSave_ListsTheSameChanges()
    {
        TwoWay();
        var r = Cli("compare", "L", "R", "--no-save");
        var changes = r.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("    ")).Select(l => l.Trim()).ToList();
        Assert.Equal(8, changes.Count);
        Assert.Contains("M blob.bin [binary]", changes);
        Assert.Contains("M content.txt [content]", changes);
        Assert.Contains("M enc.txt [encoding]", changes);
        Assert.Contains("M eol.txt [eol]", changes);
        Assert.Contains("M ws.txt [whitespace]", changes);
        Assert.Contains("D gone.txt", changes);
        Assert.Contains("A fresh.txt", changes);
        Assert.Contains("R sub/old.txt -> sub/moved.txt", changes);
    }

    [Fact]
    public void Compare_IdenticalTrees_ListNothing()
    {
        foreach (var d in new[] { "L", "R" })
        {
            _t.Put($"{d}/a.txt", "same\n");
            _t.Put($"{d}/deep/b.txt", "also same\n");
        }
        var r = Cli("compare", "L", "R", "--no-cache");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.DoesNotContain(r.Stdout.Split('\n'), l => l.StartsWith("    "));
        Assert.Matches(@"(?m)^  files      2\r?$", r.Stdout);
        Assert.Matches(@"(?m)^  identical  2 ", r.Stdout);
        Assert.Matches(@"(?m)^  added      0\r?$", r.Stdout);
        Assert.Matches(@"(?m)^  removed    0\r?$", r.Stdout);
        Assert.Matches(@"(?m)^  renamed    0\r?$", r.Stdout);
        Assert.Matches(@"(?m)^  modified   0 ", r.Stdout);
        Assert.Contains("cache off", r.Stdout);
    }

    [Fact]
    public void Compare_RedirectedOutput_KeepsNonAsciiPaths()
    {
        _t.Dir("L");
        _t.Put("R/日本.txt", "x\n");
        var r = Cli("compare", "L", "R", "--no-save");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("A 日本.txt", r.Stdout);
    }

    [Fact]
    public void Compare_NoSave_SavesNothing()
    {
        TwoWay();
        var r = Cli("compare", "L", "R", "--no-save");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.DoesNotContain("saved", r.Stdout);
        var list = Cli("results");
        Assert.Contains("0 saved compare(s)", list.Stdout);
    }

    [Fact]
    public void Compare_Timings_GoToStderr()
    {
        TwoWay();
        var r = Cli("compare", "L", "R", "--timings", "--no-save");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Matches(@"(?m)^  timing     walk\s+[\d,]+ ms", r.Stderr);
        Assert.DoesNotContain("timing", r.Stdout);
    }

    [Fact]
    public void Compare_Rehash_And_FastStat_Run()
    {
        TwoWay();
        Assert.Equal(0, Cli("compare", "L", "R", "--no-save").ExitCode);
        var r = Cli("compare", "L", "R", "--rehash", "--fast-stat", "--threads", "2", "--no-save");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("(threads 2, cache rehash)", r.Stdout);
        Assert.Matches(@"(?m)^  modified   5 ", r.Stdout);
    }

    [Fact]
    public void Compare_MissingDirectory_IsError2()
    {
        _t.Dir("L");
        var r = Cli("compare", "L", "nowhere");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("error:", r.Stderr);
        Assert.Contains("nowhere", r.Stderr);
        Assert.Equal("", r.Stdout);

        _t.Put("afile.txt", "x\n");
        r = Cli("compare", "afile.txt", "L");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("is not a directory", r.Stderr);
    }

    [Fact]
    public void Compare_UnreadableFile_IsIncomplete3()
    {
        if (!OperatingSystem.IsWindows()) return; // an exclusive open is a lock only on Windows
        _t.Put("L/locked.txt", "left side\n");
        _t.Put("R/locked.txt", "rite side\n"); // same size: the content must be read
        _t.Put("L/ok.txt", "ok\n");
        _t.Put("R/ok.txt", "ok\n");
        ExeResult r;
        using (new FileStream(_t.P("L/locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            r = Cli("compare", "L", "R", "--no-cache");
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.Contains("locked.txt [unreadable]", r.Stdout);
        Assert.Contains("could not be read", r.Stderr);
    }

    [Fact]
    public void Compare_Patch_IsGitApplyable_OntoACopyOfTheLeftTree()
    {
        TwoWay();
        var before = Snap("L", "R");
        var r = Cli("compare", "L", "R", "--patch", "--no-save");
        Assert.True(r.ExitCode == 0, r.ToString());
        // The patch on stdout, the summary on stderr.
        Assert.StartsWith("# Binary files a/blob.bin and b/blob.bin differ", r.Stdout);
        Assert.Contains("diff --git a/content.txt b/content.txt", r.Stdout);
        Assert.Contains("-two\n+TWO\n", r.Stdout);
        Assert.Contains("rename from sub/old.txt\nrename to sub/moved.txt", r.Stdout);
        Assert.Contains("deleted file mode", r.Stdout);
        Assert.Contains("new file mode", r.Stdout);
        Assert.DoesNotContain("\r\n", r.Stdout);
        Assert.DoesNotContain("compare: L  vs  R", r.Stdout);
        Assert.Contains("compare: L  vs  R", r.Stderr);
        Assert.Contains("patch: ", r.Stderr);
        AssertUnchanged(before);

        if (ExeRunner.Git() is not { } git) return; // no git on PATH: the patch's shape is checked above
        var patch = _t.Put("changes.patch", r.Stdout);
        TempDir.Copy(_t.P("L"), _t.P("copy"));
        var check = ExeRunner.Run(git, _t.P("copy"), State, ["-c", "core.autocrlf=false", "apply", "--check", patch]);
        Assert.True(check.ExitCode == 0, check.ToString());
        var apply = ExeRunner.Run(git, _t.P("copy"), State, ["-c", "core.autocrlf=false", "apply", patch]);
        Assert.True(apply.ExitCode == 0, apply.ToString());
        // What the patch carries is now as on the right; what it describes in '#' lines stays as on the left.
        foreach (var f in new[] { "content.txt", "ws.txt", "fresh.txt", "sub/moved.txt", "same.txt" })
            Assert.Equal(File.ReadAllBytes(_t.P("R/" + f)), File.ReadAllBytes(_t.P("copy/" + f)));
        Assert.False(File.Exists(_t.P("copy/gone.txt")));
        Assert.False(File.Exists(_t.P("copy/sub/old.txt")));
        Assert.Equal(File.ReadAllBytes(_t.P("L/blob.bin")), File.ReadAllBytes(_t.P("copy/blob.bin")));
    }

    [Fact]
    public void Compare_PatchLiteral_CarriesTheEolChange()
    {
        _t.Put("L/eol.txt", "x\ny\n");
        _t.Put("R/eol.txt", "x\r\ny\r\n");
        var r = Cli("compare", "L", "R", "--patch", "--literal", "-U", "1", "--no-save");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("diff --git a/eol.txt b/eol.txt", r.Stdout);
        Assert.Contains("+x\r\n", r.Stdout);
        var plain = Cli("compare", "L", "R", "--patch", "--no-save");
        Assert.DoesNotContain("diff --git", plain.Stdout);
        Assert.Contains("# Line endings differ", plain.Stdout);
    }

    [Fact]
    public void Compare_Html_WritesTheReport_OutsideTheTrees()
    {
        TwoWay();
        var before = Snap("L", "R");
        var r = Cli("compare", "L", "R", "--html", "--out", "report", "--max-diffs", "3", "--include-identical");
        Assert.True(r.ExitCode == 0, r.ToString());
        var index = _t.P("report/index.html");
        Assert.True(File.Exists(index), r.ToString());
        Assert.Contains("index.html", r.Stdout);
        Assert.Contains("past --max-diffs", r.Stdout);
        Assert.Contains("<html", File.ReadAllText(index), StringComparison.OrdinalIgnoreCase);
        AssertUnchanged(before);
    }

    [Fact]
    public void Compare_HtmlOutInsideATree_IsRefused_BeforeComparing()
    {
        TwoWay();
        var before = Snap("L", "R");
        var r = Cli("compare", "L", "R", "--html", "--out", "L/report");
        Assert.Equal(64, r.ExitCode);
        Assert.Contains("overlap", r.Stderr);
        Assert.False(Directory.Exists(_t.P("L/report")));
        AssertUnchanged(before);
    }

    // ---------------------------------------------------------------- compare3

    [Fact]
    public void Compare3_ListsBothSidesTouched_ExitsOneOnConflicts()
    {
        ThreeWay();
        var before = Snap("B", "V1", "V2");
        var r = Cli("compare3", "B", "V1", "V2");
        Assert.True(r.ExitCode == 1, r.ToString());
        var lines = r.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains(lines, l => l.StartsWith("  CF addadd.txt ") && l.Contains("add/add"));
        Assert.Contains(lines, l => l.StartsWith("  CF conf.txt ") && l.Contains("content"));
        Assert.Contains(lines, l => l.StartsWith("  CF delmod.txt ") && l.Contains("delete/modify"));
        Assert.Contains(lines, l => l.StartsWith("  MG merge.txt "));
        Assert.Contains(lines, l => l.StartsWith("  AG agree.txt "));
        // One-sided changes only with --all.
        Assert.DoesNotContain(lines, l => l.Contains("only1.txt") && l.StartsWith("  v1"));
        Assert.DoesNotContain(lines, l => l.Contains("only2.txt") && l.StartsWith("  v2"));
        Assert.DoesNotContain(lines, l => l.Contains("keep.txt"));
        Assert.Matches(@"(?m)^  v1 only\s+1 ", r.Stdout); // only1.txt
        Assert.Matches(@"(?m)^  v2 only\s+2 ", r.Stdout); // only2.txt, and v2's delete of del2.txt
        Assert.Matches(@"(?m)^  agreed\s+1 ", r.Stdout);
        Assert.Matches(@"(?m)^  merged\s+1 ", r.Stdout);
        Assert.Matches(@"(?m)^  CONFLICT\s+3 ", r.Stdout);
        Assert.Contains("saved:", r.Stdout);
        AssertUnchanged(before);
    }

    [Fact]
    public void Compare3_All_ListsOneSidedChangesToo()
    {
        ThreeWay();
        var r = Cli("compare3", "B", "V1", "V2", "--all", "--no-save");
        Assert.True(r.ExitCode == 1, r.ToString());
        var lines = r.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains(lines, l => l.StartsWith("  v1 only1.txt "));
        Assert.Contains(lines, l => l.StartsWith("  v2 only2.txt "));
        Assert.Contains(lines, l => l.StartsWith("  v2 del2.txt "));
        Assert.DoesNotContain(lines, l => l.Contains("keep.txt"));
        Assert.DoesNotContain("saved:", r.Stdout);
    }

    [Fact]
    public void Compare3_NoConflicts_ExitsZero()
    {
        var merge = TempDir.Lines(1, 20);
        _t.Put("B/m.txt", merge);
        _t.Put("V1/m.txt", merge.Replace("line 1\n", "line 1 v1\n"));
        _t.Put("V2/m.txt", merge.Replace("line 20\n", "line 20 v2\n"));
        var r = Cli("compare3", "B", "V1", "V2", "--no-save");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("MG m.txt", r.Stdout);
    }

    [Fact]
    public void Compare3_MissingDirectory_IsError2()
    {
        _t.Dir("B");
        _t.Dir("V1");
        var r = Cli("compare3", "B", "V1", "nowhere");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("error:", r.Stderr);
    }

    [Fact]
    public void Compare3_MergeOut_WritesTheOverlay()
    {
        ThreeWay();
        var before = Snap("B", "V1", "V2");
        var r = Cli("compare3", "B", "V1", "V2", "--merge-out", "overlay", "--no-save");
        Assert.True(r.ExitCode == 1, r.ToString()); // the conflicts still make it exit 1
        Assert.Contains("merge overlay:", r.Stdout);
        Assert.True(File.Exists(_t.P("overlay/OVERLAY.txt")));
        Assert.Equal("del2.txt", File.ReadAllText(_t.P("overlay/deletes.txt")).Trim());
        var conflicts = File.ReadAllText(_t.P("overlay/conflicts.txt"));
        Assert.Contains("conf.txt", conflicts);
        Assert.Contains("addadd.txt", conflicts);
        Assert.Contains("delmod.txt", conflicts);
        // files\: v2's one-sided change, the clean merge, and the text conflicts with diff3 markers.
        Assert.Equal("o2\n", _t.Read("overlay/files/only2.txt"));
        Assert.Equal(TempDir.Lines(1, 20).Replace("line 2\n", "line 2 v1\n").Replace("line 18\n", "line 18 v2\n"), _t.Read("overlay/files/merge.txt"));
        var conf = _t.Read("overlay/files/conf.txt");
        Assert.Contains("<<<<<<< v1", conf);
        Assert.Contains("||||||| base", conf);
        Assert.Contains("=======", conf);
        Assert.Contains(">>>>>>> v2", conf);
        Assert.False(File.Exists(_t.P("overlay/files/only1.txt"))); // v1's own change is already in v1
        Assert.False(File.Exists(_t.P("overlay/files/delmod.txt"))); // not merged: v1's version stays
        AssertUnchanged(before);
    }

    [Fact]
    public void Compare3_MergeOut_InsideATreeOrNotEmpty_IsRefused()
    {
        ThreeWay();
        var before = Snap("B", "V1", "V2");
        var r = Cli("compare3", "B", "V1", "V2", "--merge-out", "V1/overlay", "--no-save");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("inside the v1 tree", r.Stderr);
        AssertUnchanged(before);

        _t.Put("busy/file.txt", "x\n");
        r = Cli("compare3", "B", "V1", "V2", "--merge-out", "busy", "--no-save");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("not empty", r.Stderr);
        Assert.Single(Directory.GetFileSystemEntries(_t.P("busy")));
    }

    [Fact]
    public void Compare3_Html_WritesTheReport()
    {
        ThreeWay();
        var r = Cli("compare3", "B", "V1", "V2", "--html", "--out", "report3");
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.True(File.Exists(_t.P("report3/index.html")), r.ToString());
    }

    // ---------------------------------------------------------------- apply

    private void PortTrees()
    {
        var f = TempDir.Lines(1, 10, "l");
        _t.Put("L/f.txt", f);
        _t.Put("R/f.txt", f.Replace("l 3\n", "L3 changed\n"));
        _t.Put("T/f.txt", f.Replace("l 9\n", "target 9\n"));
        _t.Put("L/c.txt", "q1\nq2\n");
        _t.Put("R/c.txt", "q1\nR\n");
        _t.Put("T/c.txt", "q1\nT\n");
    }

    [Fact]
    public void Apply_DryRun_ReportsAndChangesNothing()
    {
        PortTrees();
        var before = Snap("L", "R", "T");
        var r = Cli("apply", "L", "R", "T");
        Assert.True(r.ExitCode == 1, r.ToString()); // c.txt conflicts
        Assert.Contains("dry run", r.Stdout);
        Assert.Contains("1 would apply", r.Stdout);
        Assert.Contains("conflict         c.txt", r.Stdout);
        Assert.Contains("run again with --write", r.Stdout);
        AssertUnchanged(before);
    }

    [Fact]
    public void Apply_Write_AppliesCleanFiles_LeavesConflictedOnesUntouched()
    {
        PortTrees();
        var before = Snap("L", "R");
        var r = Cli("apply", "L", "R", "T", "--write");
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains("APPLIED", r.Stdout);
        Assert.Equal(TempDir.Lines(1, 10, "l").Replace("l 3\n", "L3 changed\n").Replace("l 9\n", "target 9\n"), _t.Read("T/f.txt"));
        Assert.Equal("q1\nT\n", _t.Read("T/c.txt"));
        AssertUnchanged(before);

        // Again: the done file is "already" there.
        r = Cli("apply", "L", "R", "T", "--write");
        Assert.Contains("1 already there", r.Stdout);
    }

    [Fact]
    public void Apply_NoConflicts_ExitsZero()
    {
        var f = TempDir.Lines(1, 10, "l");
        _t.Put("L/f.txt", f);
        _t.Put("R/f.txt", f.Replace("l 3\n", "L3\n"));
        _t.Put("T/f.txt", f);
        var r = Cli("apply", "L", "R", "T", "--write", "--no-cache", "--threads", "1");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Equal(f.Replace("l 3\n", "L3\n"), _t.Read("T/f.txt"));
    }

    [Fact]
    public void Apply_MissingDirectory_IsError2()
    {
        _t.Dir("L");
        _t.Dir("R");
        var r = Cli("apply", "L", "nowhere", "T");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("error:", r.Stderr);
    }

    // ---------------------------------------------------------------- apply-overlay

    [Fact]
    public void ApplyOverlay_DryRun_Write_ThenRefusesASecondApplyWithoutAgain()
    {
        ThreeWay();
        Assert.Equal(1, Cli("compare3", "B", "V1", "V2", "--merge-out", "overlay", "--no-save").ExitCode);
        TempDir.Copy(_t.P("V1"), _t.P("T"));
        var target = Snap("T");

        var dry = Cli("apply-overlay", "overlay", "T");
        Assert.True(dry.ExitCode == 0, dry.ToString());
        Assert.Contains("dry run", dry.Stdout);
        Assert.Contains("1 would be deleted", dry.Stdout);
        Assert.Contains("4 would be written", dry.Stdout);
        Assert.Contains("nothing was changed", dry.Stdout);
        AssertUnchanged(target);

        var write = Cli("apply-overlay", "overlay", "T", "--write");
        Assert.True(write.ExitCode == 0, write.ToString());
        Assert.Contains("APPLIED", write.Stdout);
        Assert.False(File.Exists(_t.P("T/del2.txt")));
        Assert.Equal("o2\n", _t.Read("T/only2.txt"));
        Assert.Equal("o1\n", _t.Read("T/only1.txt"));
        Assert.Contains("<<<<<<< v1", _t.Read("T/conf.txt"));
        Assert.Equal("aa1\n", _t.Read("V1/addadd.txt")); // the overlay went onto the copy, never v1

        // A conflict resolved since would be overwritten: refused without --again.
        _t.Put("T/conf.txt", "c1\nresolved\nc3\n");
        var again = Cli("apply-overlay", "overlay", "T", "--write");
        Assert.Equal(2, again.ExitCode);
        Assert.Contains("already applied", again.Stderr);
        Assert.Equal("c1\nresolved\nc3\n", _t.Read("T/conf.txt"));

        var forced = Cli("apply-overlay", "overlay", "T", "--write", "--again");
        Assert.True(forced.ExitCode == 0, forced.ToString());
        Assert.Contains("<<<<<<< v1", _t.Read("T/conf.txt"));
    }

    [Fact]
    public void ApplyOverlay_NotAnOverlay_IsError2()
    {
        _t.Put("notoverlay/x.txt", "x\n");
        _t.Dir("T");
        var r = Cli("apply-overlay", "notoverlay", "T");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("error:", r.Stderr);
        r = Cli("apply-overlay", "nowhere", "T");
        Assert.Equal(2, r.ExitCode);
    }

    // ---------------------------------------------------------------- diff, blockdiff

    [Fact]
    public void Diff_TwoFiles_IsAUnifiedDiff()
    {
        _t.Put("a/f.txt", "one\ntwo\nthree\n");
        _t.Put("b/f.txt", "one\nTWO\nthree\n");
        var before = Snap("a", "b");
        var r = Cli("diff", "a/f.txt", "b/f.txt");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Equal("diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,3 +1,3 @@\n one\n-two\n+TWO\n three\n", r.Stdout);
        r = Cli("diff", "a/f.txt", "b/f.txt", "-U", "0");
        Assert.Contains("@@ -2 +2 @@\n-two\n+TWO\n", r.Stdout);
        Assert.DoesNotContain(" one\n", r.Stdout);
        AssertUnchanged(before);
    }

    [Fact]
    public void Diff_AbsentSide_IsAnAddOrADelete()
    {
        _t.Put("b/new.txt", "new\n");
        var r = Cli("diff", "/dev/null", "b/new.txt");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("new file mode", r.Stdout);
        Assert.Contains("+new\n", r.Stdout);
        r = Cli("diff", "b/new.txt", "NUL");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("deleted file mode", r.Stdout);
        Assert.Contains("-new\n", r.Stdout);
    }

    [Fact]
    public void Diff_MissingFile_IsError2()
    {
        _t.Put("b/new.txt", "new\n");
        var r = Cli("diff", "a/missing.txt", "b/new.txt");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("not found", r.Stderr);
        Assert.Equal("", r.Stdout);
    }

    [Fact]
    public void BlockDiff_ChangedAndIdentical()
    {
        var bytes = new byte[300_000];
        new Random(7).NextBytes(bytes);
        _t.PutBytes("a/big.bin", bytes);
        var changed = (byte[])bytes.Clone();
        for (int i = 150_000; i < 150_100; i++) changed[i] ^= 0xFF;
        _t.PutBytes("b/big.bin", changed);
        _t.PutBytes("b/same.bin", bytes);
        var before = Snap("a", "b");

        var r = Cli("blockdiff", "a/big.bin", "b/big.bin");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("size       300000 -> 300000 bytes", r.Stdout);
        Assert.Matches(@"changed    \d+ region\(s\)", r.Stdout);
        Assert.Matches(@"(?m)^    REP old\[\d+,\+\d+\) -> new\[\d+,\+\d+\)", r.Stdout);

        r = Cli("blockdiff", "a/big.bin", "b/same.bin");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("identical  (no blocks changed)", r.Stdout);

        r = Cli("blockdiff", "a/big.bin", "b/missing.bin");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("both arguments must be existing files", r.Stderr);
        AssertUnchanged(before);
    }

    // ---------------------------------------------------------------- report, results

    [Fact]
    public void Report_ForASavedCompare_WritesTheHtml()
    {
        TwoWay();
        var id = Id(Cli("compare", "L", "R"));
        var before = Snap("L", "R");
        var r = Cli("report", id, "--out", "rep");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.StartsWith($"compare {id} ", r.Stdout);
        Assert.True(File.Exists(_t.P("rep/index.html")));
        Assert.Contains("8 file(s)", r.Stdout);
        AssertUnchanged(before);

        // By the result directory too, into its default place (inside the result).
        var dir = Directory.GetDirectories(Path.Combine(State, "results")).Single(d => d.EndsWith("-" + id));
        r = Cli("report", dir);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains(".html", r.Stdout);
    }

    [Fact]
    public void Report_OfAnIncompleteCompare_Exits3_AndSaysSo()
    {
        if (!OperatingSystem.IsWindows()) return;
        _t.Put("I/L/a.c", "one\n");
        _t.Put("I/R/a.c", "two\n");
        File.WriteAllText(@"\\?\" + _t.P("I/L/x."), "left\n"); // legal on NTFS, unopenable by plain path: unreadable
        File.WriteAllText(@"\\?\" + _t.P("I/R/x."), "right\n");
        try
        {
            var c = Cli("compare", "I/L", "I/R");
            Assert.True(c.ExitCode == 3, c.ToString());
            var r = Cli("report", Id(c), "--out", "rep-i");
            Assert.True(r.ExitCode == 3, r.ToString());
            Assert.Contains("is incomplete", r.Stderr);
            Assert.True(File.Exists(_t.P("rep-i/index.html")));
        }
        finally
        {
            File.Delete(@"\\?\" + _t.P("I/L/x."));
            File.Delete(@"\\?\" + _t.P("I/R/x."));
        }
    }

    [Fact]
    public void Renames_ArePureOrEdited_AndAReorderedFileIsNot100PercentSimilar()
    {
        var lines = Enumerable.Range(0, 40).Select(i => $"rename line {i}\n").ToList();
        _t.Put("N/L/pure.txt", string.Concat(lines));
        _t.Put("N/R/pure2.txt", string.Concat(lines));
        _t.Put("N/L/order.txt", string.Concat(lines.Select(l => l.Replace("rename", "order"))));
        _t.Put("N/R/order2.txt", string.Concat(Enumerable.Reverse(lines).Select(l => l.Replace("rename", "order")))); // same lines, other order
        var r = Cli("compare", "N/L", "N/R", "--no-save");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("renamed    2   pure 1 · edited 1", r.Stdout);
        Assert.Contains("R order.txt -> order2.txt  (100% similar, but edited)", r.Stdout);
        Assert.Contains("R pure.txt -> pure2.txt\n", r.Stdout.Replace("\r", "")); // a pure one, as before: unmarked

        var p = Cli("compare", "N/L", "N/R", "--no-save", "--patch");
        Assert.Contains("similarity index 99%\nrename from order.txt", p.Stdout);   // git keeps 100% for identical
        Assert.Contains("similarity index 100%\nrename from pure.txt", p.Stdout);
    }

    [Fact]
    public void Report_UnknownId_IsError2()
    {
        var r = Cli("report", "zzzz");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("no saved compare 'zzzz'", r.Stderr);
    }

    [Fact]
    public void Report_Compare3_SaysSo()
    {
        ThreeWay();
        var r3 = Cli("compare3", "B", "V1", "V2");
        var id = Regex.Match(r3.Stdout, @"reopen later by id ([0-9a-f]{4})").Groups[1].Value;
        Assert.NotEqual("", id);
        var r = Cli("report", id, "--out", "rep3");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.StartsWith($"compare3 {id} ", r.Stdout);
        Assert.True(File.Exists(_t.P("rep3/index.html")));
    }

    [Fact]
    public void Results_ListsNewestFirst_AndPrunes_DryRunUnlessYes()
    {
        TwoWay();
        var ids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            // A result is named by the second it started in: each its own second, so "newest first" is well defined
            // (two in one second are ordered by their random id; see ResultsInOneSecond_ListNewestFirst).
            var done = DateTime.UtcNow;
            if (i > 0) Assert.True(SpinWait.SpinUntil(() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond > done.Ticks / TimeSpan.TicksPerSecond, 3000));
            ids.Add(Id(Cli("compare", "L", "R", "--no-cache")));
        }

        var list = Cli("results");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("3 saved compare(s)", list.Stdout);
        var at = ids.Select(id => list.Stdout.IndexOf("  " + id + "  ", StringComparison.Ordinal)).ToList();
        Assert.All(at, a => Assert.True(a >= 0, list.Stdout));
        Assert.True(at[2] < at[1] && at[1] < at[0], list.Stdout); // newest first
        Assert.Contains("added 1, removed 1, modified 5, renamed 1", list.Stdout);
        Assert.Contains("1 saved compare(s)", Cli("results", "--max", "1").Stdout);

        var dry = Cli("results", "--prune", "--keep", "1");
        Assert.True(dry.ExitCode == 0, dry.ToString());
        Assert.Contains("would delete 2 saved compare(s)", dry.Stdout);
        Assert.Contains("run again with --yes", dry.Stdout);
        Assert.Contains("3 saved compare(s)", Cli("results").Stdout);

        var yes = Cli("results", "--prune", "--keep", "1", "--yes");
        Assert.True(yes.ExitCode == 0, yes.ToString());
        Assert.Contains("deleted 2 of 2", yes.Stdout);
        var after = Cli("results").Stdout;
        Assert.Contains("1 saved compare(s)", after);
        Assert.Contains(ids[2], after);
        Assert.DoesNotContain(ids[0], after);

        var nothing = Cli("results", "--prune", "--keep", "1", "--yes");
        Assert.True(nothing.ExitCode == 0, nothing.ToString());
        Assert.Contains("nothing to prune", nothing.Stdout);
        // --older-than: the one left is minutes old, not over a day.
        Assert.Contains("nothing to prune", Cli("results", "--prune", "--keep", "0", "--older-than", "1").Stdout);
        Assert.Contains("deleted 1 of 1", Cli("results", "--prune", "--keep", "0", "--yes").Stdout);
        Assert.Contains("0 saved compare(s)", Cli("results").Stdout);
    }

    [Fact]
    public void ResultsInOneSecond_ListNewestFirst()
    {
        _t.Put("L/a.txt", "a\n");
        _t.Put("R/a.txt", "b\n");
        var root = _t.P("inproc-results");
        var store = new CodeDiffer.Core.Sessions.SessionStore(resultsRoot: root);
        var started = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            var s = store.Start(_t.P("L"), _t.P("R"), new CodeDiffer.Core.Compare.CompareOptions { Cache = CodeDiffer.Core.Ledger.CacheMode.Off });
            Assert.True(s.Wait(TimeSpan.FromSeconds(30)));
            started.Add(s.Id);
        }
        var listed = CodeDiffer.Core.Sessions.ResultStore.List(root).Select(c => c.Id).ToList();
        started.Reverse();
        Assert.Equal(started, listed);
    }

    [Fact]
    public void StatCheck_ALocalTree_AllMatch_AndNoNameIsPrinted()
    {
        _t.Put("T/secret-name.c", "x\n");
        _t.Put("T/sub/other.h", "y\n");
        var r = Cli("statcheck", "T");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("2 files listed, 2 checked", r.Stdout);
        Assert.Matches(@"MATCH: listing = handle \(cacheable\)\s+2", r.Stdout);
        Assert.DoesNotContain("DIFFERS", r.Stdout);
        Assert.DoesNotContain("secret", r.Stdout);
        Assert.Equal(64, Cli("statcheck").ExitCode);
    }

    // ---------------------------------------------------------------- verify

    [Fact]
    public void Verify_MissingFile_IsError2()
    {
        var r = Cli("verify", "nowhere-delta.json");
        Assert.Equal(2, r.ExitCode);
        Assert.Contains("error:", r.Stderr);
    }

    [Theory]
    [InlineData("{garbled")]
    [InlineData("")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"_meta\":{\"deltaKind\":\"symbol\"}}")]
    [InlineData("{\"_meta\":{\"deltaKind\":7}}")]
    [InlineData("{\"_meta\":{\"manifestVersion\":\"9.9\"}}")]
    [InlineData("{\"_meta\":{\"manifestVersion\":999}}")]
    [InlineData("{\"_meta\":{\"deltaKind\":\"conflict-3way\",\"manifestVersion\":999}}")]
    public void Verify_GarbledOrUnsupportedDelta_IsError2(string json)
    {
        _t.Put("delta.json", json);
        var r = Cli("verify", "delta.json");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("error:", r.Stderr);
    }

    // The kind is peeked from the head of the file when _meta comes first; otherwise the whole file is parsed. Either
    // way it routes the same (a bad flag gets the usage of the verify it was routed to).
    [Theory]
    [InlineData("{\"_meta\":{\"deltaKind\":\"conflict-3way\"}}", true)]
    [InlineData("{\"_meta\":{\"x\":{\"deltaKind\":\"diff\"},\"y\":[1,{\"a\":2}],\"deltaKind\":\"conflict-3way\"}}", true)]
    [InlineData("﻿{\"_meta\":{\"deltaKind\":\"conflict-3way\"}}", true)]
    [InlineData("{\"fileOps\":{},\"_meta\":{\"deltaKind\":\"conflict-3way\"}}", true)]
    [InlineData("{\"_meta\":{\"deltaKind\":\"diff\"}}", false)]
    [InlineData("{\"_meta\":{\"manifestVersion\":1}}", false)]
    [InlineData("{\"fileOps\":{},\"_meta\":{\"deltaKind\":\"diff\"}}", false)]
    public void Verify_RoutesOnDeltaKind_WhereverMetaStands(string json, bool conflict)
    {
        _t.Put("delta.json", json);
        var r = Cli("verify", "delta.json", "--bogus", "B");
        Assert.True(r.ExitCode == 64, r.ToString());
        Assert.Contains(conflict ? "--v1 DIR" : "--variant DIR", r.Stderr);
        Assert.DoesNotContain(conflict ? "--variant DIR" : "--v1 DIR", r.Stderr);
    }

    [Fact]
    public void Verify_HalfGivenTreeFlags_IsAUsageError()
    {
        _t.Put("delta.json", "{\"_meta\":{\"manifestVersion\":1}}");
        _t.Dir("B");
        var r = Cli("verify", "delta.json", "--base", "B");
        Assert.Equal(64, r.ExitCode);
        Assert.Contains("need both --base and --variant", r.Stderr);
        r = Cli("verify", "delta.json", "--bogus", "B");
        Assert.Equal(64, r.ExitCode);
    }
}
