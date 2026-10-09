using System.Security.Cryptography;
using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Port;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.Verify;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Review round 10, tier 2: a space between two operator characters is not whitespace; line endings are counted in the
/// file's own encoding and an encoding change alongside is said; verify wants canonical hunks, checks whitespace and
/// run-rule hunks and a rename's reason; a one-sided conflict renders; context is clamped; the port normalizes over a
/// mixed base and carries the change's conversion to one line-ending style; equally edited renames pair by name.
/// </summary>
public sealed class Round10Tier2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r10b-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };
    private static readonly string Ten = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));

    public Round10Tier2Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string rel, string text) => PutBytes(rel, new UTF8Encoding(false).GetBytes(text));

    private void PutBytes(string rel, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), bytes);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private FileDelta Record(string path, ChangeReason reason, IReadOnlyList<Hunk> hunks, IReadOnlyList<RunHunk>? runs = null, string? from = null)
        => new(path, reason, Sha(P("B/" + (from ?? path))), Sha(P("V/" + path)), new FileInfo(P("B/" + (from ?? path))).Length,
            new FileInfo(P("V/" + path)).Length, hunks, runs ?? []);

    // ---- whitespace ----

    [Theory]
    [InlineData("y = a - -b;", "y = a --b;")]
    [InlineData("y = a + +b;", "y = a ++b;")]
    [InlineData("z = y / *p;", "z = y /*p;")]
    [InlineData("if (a > = b)", "if (a >= b)")]
    public void ASpaceBetweenTwoOperatorCharacters_IsNotWhitespace(string left, string right)
    {
        Assert.NotEqual(TextInspector.WhitespaceKey(left), TextInspector.WhitespaceKey(right));
        Put("L/a.c", left + "\n");
        Put("R/a.c", right + "\n");
        Assert.Equal(ChangeReason.Content, new ReasonClassifier().Classify(P("L/a.c"), left.Length + 1, P("R/a.c"), right.Length + 1));
        // Streamed (the large-file path), the same verdict.
        Assert.Equal(ChangeReason.Content, new ReasonClassifier(maxClassifyBytes: 4).Classify(P("L/a.c"), left.Length + 1, P("R/a.c"), right.Length + 1));
    }

    [Theory]
    [InlineData("x=1;", "x = 1;")]
    [InlineData("f(a,b);", "f( a, b );")]
    [InlineData("y = a - b;", "y = a-b;")]
    public void SpacingRoundPunctuation_IsStillWhitespace(string left, string right)
    {
        Put("L/a.c", left + "\n");
        Put("R/a.c", right + "\n");
        Assert.Equal(ChangeReason.Whitespace, new ReasonClassifier().Classify(P("L/a.c"), left.Length + 1, P("R/a.c"), right.Length + 1));
        Assert.Equal(ChangeReason.Whitespace, new ReasonClassifier(maxClassifyBytes: 4).Classify(P("L/a.c"), left.Length + 1, P("R/a.c"), right.Length + 1));
    }

    // ---- line endings and encoding in a patch's notes ----

    [Fact]
    public void Utf16LineEndings_AreCountedInUtf16_AndAnEncodingChangeAlongsideIsSaid()
    {
        var u16 = new UnicodeEncoding(false, true);
        PutBytes("L/w.txt", [.. u16.GetPreamble(), .. u16.GetBytes("a\r\nb\r\n")]);
        PutBytes("R/w.txt", [.. u16.GetPreamble(), .. u16.GetBytes("a\nb\n")]);
        Put("L/e.txt", "a\r\nb\r\n");
        PutBytes("R/e.txt", [.. u16.GetPreamble(), .. u16.GetBytes("a\nb\n")]);
        var r = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        Assert.All(r.Changes, c => Assert.Equal(ChangeReason.Eol, c.Reason));

        var w = new StringWriter { NewLine = "\n" };
        PatchWriter.WriteChange(w, r.Changes.Single(c => c.RelativePath == "w.txt"), P("L"), P("R"), new PatchOptions(), new PatchStats());
        Assert.Contains("a/w.txt (CRLF) b/w.txt (LF)", w.ToString());
        Assert.DoesNotContain("encoding too", w.ToString());

        var e = new StringWriter { NewLine = "\n" };
        PatchWriter.Write(e, r, P("L"), P("R"));
        Assert.Contains("a/e.txt (CRLF) b/e.txt (LF); encoding too: a (UTF-8/ASCII, no BOM) b (UTF-16 LE)", e.ToString());
    }

    // ---- verify: canonical hunks ----

    private static readonly string[] Abcd = ["a", "b", "c", "d"];

    public static TheoryData<string[], Hunk[], string> NotCanonical => new()
    {
        { ["a", "X", "Y", "d"], [new(HunkOp.Replace, 2, 1, 2, 1), new(HunkOp.Replace, 3, 1, 3, 1)], "abuts" },
        { ["a", "X", "Y", "d"], [new(HunkOp.Replace, 1, 4, 1, 4)], "wider than the change" },
        { ["a", "c", "d"], [new(HunkOp.Delete, 2, 1, 99, 0)], "newStart should be 2" },
        { ["a", "c", "d"], [new(HunkOp.Delete, 2, 1, 1, 0)], "newStart should be 2" },
        { ["a", "b", "c", "d"], [new(HunkOp.Insert, 1, 0, 2, 0)], "doesn't fit" },
    };

    [Theory]
    [MemberData(nameof(NotCanonical))]
    public void HunksThatRebuildButAreNotCanonical_AreAProblem(string[] variant, Hunk[] hunks, string why)
        => Assert.Contains(why, HunkApplier.Problem(Abcd, variant, hunks));

    [Fact]
    public void CodeDiffersOwnHunks_AreCanonical()
    {
        var rnd = new Random(7);
        for (int t = 0; t < 300; t++)
        {
            var a = Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => ((char)('a' + rnd.Next(4))).ToString()).ToList();
            var b = Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => ((char)('a' + rnd.Next(4))).ToString()).ToList();
            var h = LineDiffer.Diff(a, b);
            Assert.Null(HunkApplier.Problem(a, b, h));
        }
    }

    // ---- verify: whitespace, run rules, rename reasons ----

    [Fact]
    public void Verify_AWhitespaceRecordsHunksMustChangeOnlyWhitespace()
    {
        Put("B/w.c", "int x=1;\nfoo();\n");
        Put("V/w.c", "int x = 1;\nbar();\n");
        var lying = new DeltaManifest(1, [], [], [], [Record("w.c", ChangeReason.Whitespace, [new Hunk(HunkOp.Replace, 1, 2, 1, 2)])], null);
        var cc = DeltaTreeCrossCheck.Run(P("B"), P("V"), lying);
        Assert.False(cc.Ok);
        Assert.Contains("more than whitespace", Assert.Single(cc.Files).Problem);

        Put("V/w.c", "int x = 1;\nfoo();\n");
        var honest = new DeltaManifest(1, [], [], [], [Record("w.c", ChangeReason.Whitespace, [new Hunk(HunkOp.Replace, 1, 1, 1, 1)])], null);
        Assert.True(DeltaTreeCrossCheck.Run(P("B"), P("V"), honest).Ok);
    }

    [Fact]
    public void Verify_RunRuleHunksAreExpandedAndChecked()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"line {i}").ToArray();
        Put("B/g.txt", string.Join('\n', lines) + "\n");
        for (int i = 0; i < 100; i += 20) lines[i] += " /*mut*/";
        Put("V/g.txt", string.Join('\n', lines) + "\n");

        var right = Record("g.txt", ChangeReason.Content, [], [new RunHunk(HunkOp.Replace, 20, 1, 100, 1)]);
        var cc = DeltaTreeCrossCheck.Run(P("B"), P("V"), new DeltaManifest(1, [], [], [], [right], null));
        Assert.True(cc.Ok, Assert.Single(cc.Files).Problem);
        Assert.Equal(1, cc.Checked);

        var wrong = right with { RunHunks = [new RunHunk(HunkOp.Replace, 10, 1, 100, 1)] };
        var bad = DeltaTreeCrossCheck.Run(P("B"), P("V"), new DeltaManifest(1, [], [], [], [wrong], null));
        Assert.False(bad.Ok);
    }

    [Fact]
    public void Verify_ARenamesReasonIsCompared()
    {
        Put("B/a.c", Ten);
        Put("V/b.c", Ten.Replace("line 3", "line three"));
        var report = new DirectoryComparer(NoCache).Compare(P("B"), P("V"));
        var ren = Assert.Single(report.Changes, c => c.Status == ChangeStatus.Renamed);
        var op = new RenameOp("a.c", "b.c", ren.SimilarityMilli!.Value);
        var hunks = new[] { new Hunk(HunkOp.Replace, 3, 1, 3, 1) };

        var truth = new DeltaManifest(1, [], [], [op], [Record("b.c", ChangeReason.Content, hunks, from: "a.c")], null);
        Assert.True(DeltaFileOpsCheck.Run(report, truth, baseDir: P("B"), variantDir: P("V")).Ok);
        var lie = truth with { Modified = [Record("b.c", ChangeReason.Whitespace, hunks, from: "a.c")] };
        var fo = DeltaFileOpsCheck.Run(report, lie, baseDir: P("B"), variantDir: P("V"));
        Assert.False(fo.Ok);
        Assert.Contains("manifest similarity", Assert.Single(fo.WrongSimilarity));
        Assert.Contains("whitespace", fo.WrongSimilarity[0]);
    }

    // ---- views and context ----

    [Fact]
    public void AOneSidedConflict_Renders()
    {
        Put("B/a.c", Ten);
        Put("V1/n.c", Ten);                                 // v1 renames a.c to n.c...
        Put("V2/a.c", Ten);
        Put("V2/n.c", "something else entirely\n");         // ...where v2 adds an unrelated n.c
        var s = new SessionStore(save: false).Start3(P("B"), P("V1"), P("V2"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromSeconds(60)));
        Assert.Null(s.Error);
        var view = ThreeWayViews.FileDiff(s, "a.c");
        Assert.Contains("path collision", ThreeWayViews.ListFiles(s, status: "conflict"));
        Assert.Contains("(no change of v2's at this path)", view);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void AnyContext_WritesHunkHeadersGitCanRead(int context)
    {
        var w = new StringWriter { NewLine = "\n" };
        UnifiedDiff.Write(w, "a.c", "a.c", Ten, Ten.Replace("line 5", "five"), context);
        var headers = w.ToString().Split('\n').Where(l => l.StartsWith("@@")).ToList();
        var expect = context < 0 ? "@@ -5 +5 @@" : "@@ -1,10 +1,10 @@";
        Assert.Equal([expect], headers);
    }

    // ---- port ----

    [Fact]
    public void Port_AMixedBaseOntoACrlfTarget_WritesCrlf()
    {
        Put("L/f.txt", "a\r\nb\nc\r\nd\r\ne\r\n");
        Put("R/f.txt", "a\r\nb\nc\r\nd\r\nNEW\ne\r\n");
        Put("C/f.txt", "a\r\nb\r\nc\r\nd\r\ne\r\n");
        var report = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        var f = ChangePorter.Run(report, P("L"), P("R"), P("C"), write: true).Files.Single();
        Assert.Equal(PortStatus.Clean, f.Status);
        Assert.Equal("a\r\nb\r\nc\r\nd\r\nNEW\r\ne\r\n", File.ReadAllText(P("C/f.txt")));
    }

    [Fact]
    public void Port_ACoversionToOneStyle_IsCarried_TheTargetsOwnLinesToo()
    {
        Put("L/f.txt", "a\r\nb\r\nc\r\nd\r\ne\r\n");
        Put("R/f.txt", "a\nb\nc\nd\nE\n");                  // converted to LF, and e edited
        Put("C/f.txt", "a\r\nB\r\nc\r\nd\r\ne\r\n");        // the target's own edit, still CRLF
        var report = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        var f = ChangePorter.Run(report, P("L"), P("R"), P("C"), write: true).Files.Single();
        Assert.Equal(PortStatus.Clean, f.Status);
        Assert.Contains("as the change converts them", f.Note);
        Assert.Equal("a\nB\nc\nd\nE\n", File.ReadAllText(P("C/f.txt")));
    }

    // ---- renames ----

    [Fact]
    public void EquallyEditedRenames_PairByName()
    {
        var t = string.Concat(Enumerable.Range(1, 20).Select(i => $"shared {i}\n"));
        Put("L/a/g.c", t + "g\n");
        Put("L/b/f.c", t + "f\n");
        Put("R/c/f.c", t + "f2\n");
        Put("R/c/g.c", t + "g2\n");
        var r = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        var pairs = r.Changes.Where(c => c.Status == ChangeStatus.Renamed).Select(c => $"{c.RenamedFrom} -> {c.RelativePath}").Order().ToList();
        Assert.Equal(["a/g.c -> c/g.c", "b/f.c -> c/f.c"], pairs);
    }
}
