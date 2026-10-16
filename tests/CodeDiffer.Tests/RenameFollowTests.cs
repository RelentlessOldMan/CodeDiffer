using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Port;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// M2/M3 (code-differ review): a rename the listing shows is not always one compare3 and apply should act on. Two files
/// that share only a licence header, two tiny tests, two one-line headers, or a removed file with two equally good
/// matches are listed as renames as before (the contract's metric), but held: merged and ported as a delete and an
/// add, so the other side's edit is a conflict, never written into an unrelated file.
/// </summary>
public sealed class RenameFollowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-follow-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CodeDiffer.Core.Ledger.CacheMode.Off };

    public RenameFollowTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel);

    private void Put(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), text);
    }

    private static string Licence => string.Concat(Enumerable.Range(0, 30).Select(i => $"/* Copyright line {i} of the licence */\n"));

    private static string Body(string name, string type, int step)
        => $"#include <stdio.h>\n\nint {name}(void)\n{{\n    return {step};\n}}\n\n" +
           string.Concat(Enumerable.Range(0, 5).Select(i => $"static {type} {name[0]}{i}(void)\n{{\n    return {i * step};\n}}\n\n"));

    private static string Code(int n, string tag = "")
        => string.Concat(Enumerable.Range(0, n).Select(i => $"    total += weigh(item_{i}, {i * 3}); // step {i}{tag}\n"));

    private FileChange Rename(CompareReport r) => r.Changes.Single(c => c.Status == ChangeStatus.Renamed);

    // ---- the false renames: still listed, but held ----

    public static TheoryData<string, string, string, string, string> FalseRenames() => new()
    {
        // name, base path, base text, v1 path, v1 text (v2 edits the base file in place)
        { "licence only", "src/alpha.c", Licence + Body("alpha", "int", 1), "src/beta.c", Licence + Body("beta", "long", 7) },
        { "tiny tests", "tests/t_puts.c", "#include <stdio.h>\n\nint main(void)\n{\n    puts(\"a\");\n    return 0;\n}\n",
          "tests/t_exit.c", "#include <stdlib.h>\n\nint run(void)\n{\n    exit(1);\n    return 0;\n}\n" },
        { "one-line header", "inc/x.h", "#pragma once\n", "inc/y.h", "#pragma once\n" },
    };

    [Theory]
    [MemberData(nameof(FalseRenames))]
    public void AFalseRename_IsListed_ButCompare3DoesNotFollowIt(string name, string from, string baseText, string to, string v1Text)
    {
        _ = name;
        Put("B/" + from, baseText);
        Put("V1/" + to, v1Text);
        Put("V2/" + from, baseText + "int added_by_v2(void) { return 42; }\n");

        var two = new DirectoryComparer(NoCache).Compare(P("B"), P("V1"));
        var ren = Rename(two);                                  // the listing is the contract's: still a rename
        Assert.Equal(from, ren.RenamedFrom);
        Assert.NotNull(ren.RenameHeld);

        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache);
        var e = r.Entries.Single(x => x.Path == from);
        Assert.Equal(Merge3Outcome.Conflict, e.Outcome);        // v1 deleted it, v2 edited it: said, not merged into `to`
        Assert.Equal("delete/modify", e.ConflictKind);
        Assert.Contains("not followed", e.Note);
        Assert.DoesNotContain(r.Entries, x => x.MergedPath == to && x.Outcome == Merge3Outcome.Merged);
    }

    [Fact]
    public void Apply_OfAFalseRename_LeavesTheTargetsEditedFile_AndSaysSo()
    {
        Put("L/alpha.c", Licence + Body("alpha", "int", 1));
        Put("R/beta.c", Licence + Body("beta", "long", 7));
        Put("T/alpha.c", Licence + Body("alpha", "int", 1) + "int target_only(void) { return 3; }\n");
        var report = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        var run = ChangePorter.Run(report, P("L"), P("R"), P("T"), write: true);

        Assert.True(File.Exists(P("T/alpha.c")));              // the target's edit is never deleted
        Assert.Contains("target_only", File.ReadAllText(P("T/alpha.c")));
        var removed = run.Files.Single(f => f.Path == "alpha.c");
        Assert.Equal(PortStatus.Conflict, removed.Status);
        Assert.Contains("not followed", removed.Note);
        Assert.Equal(PortStatus.Clean, run.Files.Single(f => f.Path == "beta.c").Status); // the new file is still added
    }

    [Fact]
    public void Apply_OfAFileMovedUnderADirectoryOfItsOwnName_MovesItOrLeavesItWhole()
    {
        // x → x/y: the file x must go before the directory x can be made. Either it is done, or nothing is lost.
        var text = "int main(void)\n{\n" + Code(20) + "}\n";
        Put("L/x", text);
        Put("R/x/y", text);
        Put("T/x", text);
        var report = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        var run = ChangePorter.Run(report, P("L"), P("R"), P("T"), write: true);

        var f = Assert.Single(run.Files);
        if (f.Status == PortStatus.Clean)
        {
            Assert.Equal(text, File.ReadAllText(P("T/x/y")));
            Assert.False(File.Exists(P("T/x")));
        }
        else
        {
            Assert.Equal(PortStatus.Conflict, f.Status);
            Assert.Equal(text, File.ReadAllText(P("T/x"))); // left whole, never half-moved
            Assert.False(File.Exists(P("T/x/y")));
        }
    }

    // ---- real renames are still followed ----

    [Fact]
    public void ARealEditedRename_IsStillFollowed_EvenToAnotherNameAndDirectory()
    {
        var text = "int main(void)\n{\n" + Code(40) + "}\n";
        Put("B/a/engine.c", text);
        Put("V1/b/motor.c", text.Replace("step 3\n", "step 3 (tuned)\n"));
        Put("V2/a/engine.c", text.Replace("step 30\n", "step 30 (v2)\n"));

        var ren = Rename(new DirectoryComparer(NoCache).Compare(P("B"), P("V1")));
        Assert.Null(ren.RenameHeld);
        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache);
        var e = r.Entries.Single(x => x.Path == "a/engine.c");
        Assert.Equal(Merge3Outcome.Merged, e.Outcome);
        Assert.Equal("b/motor.c", e.MergedPath);
    }

    [Fact]
    public void APureRenameOfARealFile_AndATinyOneKeepingItsName_AreFollowed()
    {
        Put("B/src/util.c", "int main(void)\n{\n" + Code(10) + "}\n");
        Put("V1/lib/helpers.c", "int main(void)\n{\n" + Code(10) + "}\n");
        Put("B/old/config.h", "#pragma once\n");
        Put("V1/new/config.h", "#pragma once\n");               // moved, same name: a real move
        var r = new DirectoryComparer(NoCache).Compare(P("B"), P("V1"));
        Assert.All(r.Changes.Where(c => c.Status == ChangeStatus.Renamed), c => Assert.Null(c.RenameHeld));
        Assert.Equal(2, r.Changes.Count(c => c.Status == ChangeStatus.Renamed));
    }

    // ---- M3: a tie is said, and held ----

    [Fact]
    public void ATiedRename_SaysWhichFileItTiedWith_AndIsHeld()
    {
        var one = "int main(void)\n{\n" + Code(20) + "}\n";
        Put("L/one.c", one);
        Put("R/two.c", one + "// two\n");
        Put("R/three.c", one + "// three\n");
        var r = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        var ren = Rename(r);
        Assert.Equal("one.c", ren.RenamedFrom);
        var other = ren.RelativePath == "two.c" ? "three.c" : "two.c";
        Assert.Contains("tied with " + other, ren.RenameHeld);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoFilesThatMatchTwoOthersAlike_AreBothHeld(bool edited)
    {
        // a.c and b.c are the same; each could be p.c or q.c. The greedy takes one pairing; neither is followed.
        var t = "int main(void)\n{\n" + Code(20) + "}\n";
        Put("L/a.c", t); Put("L/b.c", t);
        Put("R/p.c", edited ? t + "// p\n" : t); Put("R/q.c", edited ? t + "// q\n" : t);
        var r = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        var renames = r.Changes.Where(c => c.Status == ChangeStatus.Renamed).ToList();
        Assert.Equal(2, renames.Count);
        Assert.All(renames, c => Assert.Contains("tied with", c.RenameHeld));
    }

    // ---- kept when saved, shown in the listings ----

    [Fact]
    public void AHeldRename_IsSavedAndReopened_AndShownInTheListing()
    {
        Put("L/x.h", "#pragma once\n");
        Put("R/y.h", "#pragma once\n");
        var store = new SessionStore(resultsRoot: P("res"));
        var s = store.Start(P("L"), P("R"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Contains("not followed", AgentViews.ListFiles(s));

        var reopened = (CompareSession)new SessionStore(resultsRoot: P("res")).Get(s.Id)!;
        Assert.True(reopened.Wait(TimeSpan.FromMinutes(1)));
        Assert.NotNull(Rename(reopened.Report!).RenameHeld);
    }

    // ---- the follow test itself ----

    [Theory]
    [InlineData("/* licence */\n/* more */\nint a;\n", 1)]                    // the leading comment block is boilerplate
    [InlineData("// header\n//\n#include <a.h>\nint a;\n{\n}\n", 2)]            // an #include is code; braces are not lines
    [InlineData("#!/bin/sh\n# comment\n\necho hi\n", 1)]
    [InlineData("/*\n licence\n text\n*/\nx = 1\n", 1)]                         // inside a block comment, lines without a star
    [InlineData("x = 1\n// a later comment is kept\n", 2)]
    public void SignificantLines_DropTheLeadingCommentBlock_AndLinesWithoutALetterOrDigit(string text, int want)
        => Assert.Equal(want, RenameDetector.SignificantLines(text).Count());
}
