using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Noise: a content change that is only version-control stamps, numbers (in a file that isn't code) or comments. A tag
/// on the change, never a reason: the reason stays content. Found as the smallest set of kinds that explains it.
/// </summary>
public class NoiseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-noise-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static ChangeNoise Find(string path, string a, string b) => NoiseFinder.Find(path, path, a, b, TextInspector.RulesFor(path));

    [Fact]
    public void Stamps_ExpandedOtherwise_AreNoise()
    {
        Assert.Equal(ChangeNoise.Stamps, Find("src/a.c",
            "/* $Id: a.c,v 1.2 2020/01/01 10:00:00 sam Exp $ */\nint a;\n",
            "/* $Id: a.c,v 1.3 2021/02/02 11:00:00 kim Exp $ */\nint a;\n"));
        // Expanded on one side only, and two on one line.
        Assert.Equal(ChangeNoise.Stamps, Find("src/a.c",
            "// $Date$ by $Author$\nint a;\n",
            "// $Date: 2026-10-17 12:00:00 +0000 $ by $Author: kim $\nint a;\n"));
        Assert.Equal(ChangeNoise.Stamps, Find("tools/run.py", "# $Revision: 12 $\nx = 1\n", "# $Revision: 13 $\nx = 1\n"));
    }

    [Fact]
    public void ARealChange_BesideAStamp_IsNotNoise()
    {
        Assert.Equal(ChangeNoise.None, Find("src/a.c", "// $Revision: 1 $\nint a = 1;\n", "// $Revision: 2 $\nint a = 2;\n"));
        // A word that isn't a keyword is not a stamp.
        Assert.Equal(ChangeNoise.None, Find("docs/notes.txt", "$Owner: sam $\nhello\n", "$Owner: kim $\nhello\n"));
        // Blank lines only: a content change, said as such, not noise.
        Assert.Equal(ChangeNoise.None, Find("src/a.c", "int a;\nint b;\n", "int a;\n\nint b;\n"));
    }

    [Fact]
    public void Numbers_OnlyInFilesThatArentCode()
    {
        Assert.Equal(ChangeNoise.Numbers, Find("docs/notes.txt",
            "build 101 on 2026-10-01 at 12:30:01, v1.2.3, id 7f3a9c2\n",
            "build 1020 on 2026-10-17 at 09:05:44, v1.2.4.1, id 0be41d9\n"));
        Assert.Equal(ChangeNoise.Numbers, Find("cfg/app.json", "{ \"build\": 41 }\n", "{ \"build\": 42 }\n"));
        Assert.Equal(ChangeNoise.None, Find("src/a.c", "int n = 1;\n", "int n = 2;\n"));
        Assert.Equal(ChangeNoise.None, Find("docs/notes.txt", "built by sam 101\n", "built by kim 101\n"));
        // A word with a digit in it is a number only in its digits: a word swapped beside them is a change.
        Assert.Equal(ChangeNoise.Numbers, Find("cfg/ws.txt", "out D:/ws/agent07/bin\n", "out D:/ws/agent12/bin\n"));
        Assert.Equal(ChangeNoise.None, Find("cfg/ws.txt", "out D:/ws/agent07/bin\n", "out D:/ws/robot07/bin\n"));
        Assert.Equal(ChangeNoise.None, Find("cfg/ws.txt", "path=build2/x64-ship\n", "path=build2/x64-test\n"));
        Assert.Equal(ChangeNoise.None, Find("cfg/ws.txt", "root lane7.main\n", "root lane7.side\n"));
    }

    [Theory]
    [InlineData("src/a.c", "int a; // old note\n", "int a; // new note\n")]
    [InlineData("src/a.cpp", "int a;\n", "/* a block\n   over lines */\nint a;\n")]
    [InlineData("src/a.cs", "int a;\n// int b;\n// int c;\n", "int a;\n")]                 // commented-out code taken out
    [InlineData("src/a.c", "int/*x*/a;\n", "int a;\n")]                                   // a comment between tokens: a space
    [InlineData("tools/run.py", "x = 1  # one\n", "x = 1  # uno\n")]
    [InlineData("tools/run.ps1", "<# help\n#>\n$x = 1\n", "$x = 1\n")]
    [InlineData("db/q.sql", "select 1; -- a\n", "select 1; -- b\n")]
    [InlineData("proj/game.vcxproj", "<a>\n<!-- old -->\n</a>\n", "<a>\n<!-- new -->\n</a>\n")]
    [InlineData("cfg/app.ini", "; tuned\nx=1\n", "; retuned\nx=1\n")]
    [InlineData("tools/go.bat", "REM build it\necho hi\n", "REM build it all\necho hi\n")]
    [InlineData("CMakeLists.txt", "project(a) # old\n", "project(a) # new\n")]
    public void Comments_AreNoise(string path, string a, string b) => Assert.Equal(ChangeNoise.Comments, Find(path, a, b));

    [Theory]
    [InlineData("src/a.c", "s = \"http://x\";\n", "s = \"http://y\";\n")]                // in a string: not a comment
    [InlineData("src/a.c", "s = \"a\\\"//\"; f(1);\n", "s = \"a\\\"//\"; f(2);\n")]       // an escaped quote doesn't end it
    [InlineData("tools/run.py", "x = \"#1\"\n", "x = \"#2\"\n")]
    [InlineData("tools/run.sh", "echo ${#a}\n", "echo ${#b}\n")]                          // a # inside a word isn't one
    [InlineData("src/a.c", "int a = 1; // note\n", "int a = 2; // other note\n")]          // a comment beside a real change
    [InlineData("docs/notes.md", "<!-- x -->\nhello\n", "<!-- y -->\nhello\n")]           // prose: comments not known
    public void Not_Comments(string path, string a, string b) => Assert.Equal(ChangeNoise.None, Find(path, a, b));

    [Fact]
    public void AStampInAComment_IsAStamp()
        => Assert.Equal(ChangeNoise.Stamps, Find("src/a.c", "// $Revision: 1 $\nint a;\n", "// $Revision: 2 $\nint a;\n"));

    [Fact]
    public void TheSmallestSetIsKept()
    {
        // A stamp's date is a number too: stamps alone explain it, and stamps come first.
        Assert.Equal(ChangeNoise.Stamps, Find("docs/notes.txt", "$Date: 2020/01/01 $\n", "$Date: 2021/02/02 $\n"));
        // A stamp's author is not a number, and a build number is not a stamp: both.
        Assert.Equal(ChangeNoise.Stamps | ChangeNoise.Numbers, Find("docs/notes.txt",
            "$Author: sam $\nbuild 5\n", "$Author: kim $\nbuild 6\n"));
        Assert.Equal("stamps,numbers", NoiseTokens.List(ChangeNoise.Stamps | ChangeNoise.Numbers));
        Assert.Equal(ChangeNoise.Stamps | ChangeNoise.Numbers, NoiseTokens.ParseList("numbers, STAMPS, later"));
    }

    [Fact]
    public void TheFilter_HidesAFileOnlyWhenAllItsNoiseIs()
    {
        var both = new FileChange("a.txt", ChangeStatus.Modified, ChangeReason.Content, 1, 2, Noise: ChangeNoise.Stamps | ChangeNoise.Numbers);
        var real = new FileChange("b.txt", ChangeStatus.Modified, ChangeReason.Content, 1, 2);
        var eol = new FileChange("c.txt", ChangeStatus.Modified, ChangeReason.Eol, 1, 2);
        Assert.True(NoiseFilter.TryParse("stamps", out var stamps, out _));
        Assert.False(stamps!.Hides(both));
        Assert.True(NoiseFilter.TryParse("stamps, numbers", out var sn, out _));
        Assert.True(sn!.Hides(both));
        Assert.False(sn.Hides(eol));
        Assert.True(NoiseFilter.TryParse("all", out var all, out _));
        Assert.True(all!.Hides(eol));
        Assert.False(all.Hides(real));
        Assert.False(NoiseFilter.TryParse("typos", out _, out var err));
        Assert.Contains("unknown noise 'typos'", err);
    }

    private void Put(string rel, string text)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(text));
    }

    [Fact]
    public void ACompare_TagsNoise_SavesIt_AndListsWithout()
    {
        Put("L/src/a.c", "/* $Revision: 1 $ */\nint a;\n");
        Put("R/src/a.c", "/* $Revision: 2 $ */\nint a;\n");            // same size: stamps
        Put("L/src/b.c", "/* $Date: 2020/01/01 $ */\nint b;\n");
        Put("R/src/b.c", "/* $Date: 2026/10/17 12:00:00 $ */\nint b;\n"); // resized: stamps
        Put("L/src/c.c", "int c = 1;\n");
        Put("R/src/c.c", "int c = 2;\n");                                // real
        Put("L/docs/build.txt", "build 101\n");
        Put("R/docs/build.txt", "build 102\n");                          // numbers
        Put("L/src/d.c", "int d;\n");
        Put("R/src/d.c", "int d;\r\n");                                  // eol

        var results = Path.Combine(_dir, "results");
        var s = new SessionStore(resultsRoot: results).Start(Path.Combine(_dir, "L"), Path.Combine(_dir, "R"), new CompareOptions { Cache = CacheMode.Off });
        Assert.True(s.Wait(TimeSpan.FromSeconds(60)));
        Assert.Null(s.Error);
        var noise = s.Report!.Changes.ToDictionary(c => c.RelativePath, c => c.Noise);
        Assert.Equal(ChangeNoise.Stamps, noise["src/a.c"]);
        Assert.Equal(ChangeNoise.Stamps, noise["src/b.c"]);
        Assert.Equal(ChangeNoise.None, noise["src/c.c"]);
        Assert.Equal(ChangeNoise.Numbers, noise["docs/build.txt"]);
        Assert.All(s.Report.Changes, c => Assert.True(c.Reason is ChangeReason.Content or ChangeReason.Eol)); // reasons unchanged

        var back = Assert.IsType<CompareSession>(new SessionStore(resultsRoot: results).Get(s.Id));
        Assert.Equal(s.Report.Changes, back.Report!.Changes); // the tag is saved

        Assert.Contains("noise            3    stamps 2 · numbers 1 · comments 0", AgentViews.Summary(back));
        var list = AgentViews.ListFiles(back);
        Assert.Contains("src/a.c  [content, noise: stamps]", list);
        Assert.Contains("src/c.c  [content]", list);
        Assert.Contains("1 file(s) match", AgentViews.ListFiles(back, hideNoise: "all"));          // c.c
        Assert.Contains("2 file(s) match", AgentViews.ListFiles(back, hideNoise: "stamps, eol"));   // c.c, build.txt
        Assert.Contains("1 file(s) match", AgentViews.ListFiles(back, type: "code", hideNoise: "all"));
        Assert.StartsWith("error: unknown noise 'x'", AgentViews.ListFiles(back, hideNoise: "x"));
    }

    [Fact]
    public void ThreeWay_AConflictOnlyInStamps_IsKindStamps()
    {
        // Both stamp the header, and each makes a real change far from it: only the stamps collide.
        Put("B/src/a.c", "/* $Revision: 1 $ */\nint a;\nint b;\nint c;\nint d;\nint e;\n");
        Put("V1/src/a.c", "/* $Revision: 2 $ */\nint a;\nint b = 1;\nint c;\nint d;\nint e;\n");
        Put("V2/src/a.c", "/* $Revision: 3 $ */\nint a;\nint b;\nint c;\nint d;\nint e = 2;\n");
        // Both only stamp it: a stamps conflict that is noise.
        Put("B/src/b.c", "// $Id: b.c 1 $\nint b;\n");
        Put("V1/src/b.c", "// $Id: b.c 2 sam $\nint b;\n");
        Put("V2/src/b.c", "// $Id: b.c 3 kim $\nint b;\n");
        // The stamp line also changed beyond its stamp: a content conflict.
        Put("B/src/c.c", "// $Revision: 1 $ old\nint c;\n");
        Put("V1/src/c.c", "// $Revision: 2 $ new\nint c;\n");
        Put("V2/src/c.c", "// $Revision: 3 $ other\nint c;\n");

        var s = new SessionStore(resultsRoot: Path.Combine(_dir, "results"))
            .Start3(Path.Combine(_dir, "B"), Path.Combine(_dir, "V1"), Path.Combine(_dir, "V2"), new CompareOptions { Cache = CacheMode.Off });
        Assert.True(s.Wait(TimeSpan.FromSeconds(60)));
        Assert.Null(s.Error);
        var e = s.Report!.Entries.ToDictionary(x => x.Path);
        Assert.All(e.Values, x => Assert.Equal(Merge3Outcome.Conflict, x.Outcome));
        Assert.Equal("stamps", e["src/a.c"].ConflictKind);
        Assert.Equal(1, e["src/a.c"].ConflictRegions);
        Assert.Contains("only keyword stamps collide", e["src/a.c"].Note);
        Assert.Null(e["src/a.c"].Noise);                                   // real changes beside: not noise
        Assert.Equal("stamps", e["src/b.c"].ConflictKind);
        Assert.Equal(["stamps"], e["src/b.c"].Noise);
        Assert.Equal("content", e["src/c.c"].ConflictKind);
        Assert.Null(e["src/c.c"].Noise);

        Assert.Contains("CONFLICT         3    stamps 2 · content 1", ThreeWayViews.Summary(s));
        Assert.Contains("2 path(s) match", ThreeWayViews.ListFiles(s, hideNoise: "stamps"));
        // Written with markers, like a content conflict.
        var merged = TreeMerger.MergedText(e["src/a.c"], Path.Combine(_dir, "B"), Path.Combine(_dir, "V1"), Path.Combine(_dir, "V2"))!;
        Assert.Contains("<<<<<<< v1", merged);
        Assert.Contains("int b = 1;", merged);
        Assert.Contains("int e = 2;", merged);

        // Saved and read back with its kind, so it stays noise.
        var back = Assert.IsType<Compare3Session>(new SessionStore(resultsRoot: Path.Combine(_dir, "results")).Get(s.Id));
        Assert.Equal(["stamps"], back.Report!.Entries.Single(x => x.Path == "src/b.c").Noise);
    }

    [Fact]
    public void ThreeWay_APathIsNoise_WhenEverySideThatTouchedItIs()
    {
        Put("B/src/a.c", "/* $Revision: 1 $ */\nint a;\n");
        Put("V1/src/a.c", "/* $Revision: 2 $ */\nint a;\n");                // v1 only: stamps
        Put("V2/src/a.c", "/* $Revision: 1 $ */\nint a;\n");
        Put("B/src/b.c", "int b; // one\nint x;\nint y;\nint z;\n");
        Put("V1/src/b.c", "int b; // uno\nint x;\nint y;\nint z;\n");      // both: comments, merged
        Put("V2/src/b.c", "int b; // one\nint x;\nint y;\nint z; // last\n");
        Put("B/src/c.c", "int c; // one\n");
        Put("V1/src/c.c", "int c; // uno\n");                              // both, the same line: a conflict
        Put("V2/src/c.c", "int c; // eins\n");
        Put("B/src/d.c", "int d = 1;\n");
        Put("V1/src/d.c", "int d = 1;\r\n");                               // v1 eol, v2 a real change
        Put("V2/src/d.c", "int d = 2;\n");

        var s = new SessionStore(resultsRoot: Path.Combine(_dir, "results"))
            .Start3(Path.Combine(_dir, "B"), Path.Combine(_dir, "V1"), Path.Combine(_dir, "V2"), new CompareOptions { Cache = CacheMode.Off });
        Assert.True(s.Wait(TimeSpan.FromSeconds(60)));
        Assert.Null(s.Error);
        var e = s.Report!.Entries.ToDictionary(x => x.Path);
        Assert.Equal(["stamps"], e["src/a.c"].Noise);
        Assert.Equal(["comments"], e["src/b.c"].Noise);
        Assert.Equal(Merge3Outcome.Conflict, e["src/c.c"].Outcome);
        Assert.Null(e["src/c.c"].Noise);                                   // a conflict is never noise
        Assert.Null(e["src/d.c"].Noise);

        Assert.Contains("noise: 2 path(s) every side changed only in stamps 1 · numbers 0 · comments 1 · whitespace 0 · eol 0 · encoding 0",
            ThreeWayViews.Summary(s));
        var all = ThreeWayViews.ListFiles(s, hideNoise: "all");
        Assert.Contains("2 path(s) match", all);                           // c.c and d.c
        Assert.Contains("1 path(s) match", ThreeWayViews.ListFiles(s, hideNoise: "comments, stamps", status: "merged"));
        Assert.Contains("3 path(s) match", ThreeWayViews.ListFiles(s, hideNoise: "comments"));
        Assert.Contains("v1 content (noise: stamps)", ThreeWayViews.ListFiles(s));

        // The report's rows carry it for its noise chips.
        var rep = CodeDiffer.Core.Report.HtmlReport.Write(s);
        var (_, files) = CodeDiffer.Core.Report.HtmlReport.ReadIndex(Path.Combine(Path.GetDirectoryName(rep.IndexPath)!, "data", "index.js"));
        var nz = files.ToDictionary(f => f.GetProperty("p").GetString()!, f => f.TryGetProperty("nz", out var z) ? z.GetString() : null);
        Assert.Equal("stamps", nz["src/a.c"]);
        Assert.Equal("comments", nz["src/b.c"]);
        Assert.Null(nz["src/c.c"]);
        Assert.Null(nz["src/d.c"]);
    }
}
