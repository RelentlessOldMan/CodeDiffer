using System.Security.Cryptography;
using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Report;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.Verify;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Review round 11, tier 2: a metadata record verifies, a path twice in a delta is refused, a streamed content verdict
/// doesn't read both files whole, a corrupt ledger is no cache, saved diffs at another context never collide, a
/// zero-context patch says what it needs, a report replace that fails part way is replaced next time, a whitespace-only
/// change is diffed line for line, and a space taken out of a string or a number is content.
/// </summary>
public sealed class Round11Tier2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r11b-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public Round11Tier2Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
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

    private static CompareSession Done(CompareSession s)
    {
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Null(s.Error);
        return s;
    }

    // ---- B5: metadata ----

    [Fact]
    public void AMetadataRecord_ExpectsItsFileUnchanged()
    {
        Put("B/m.txt", "same\n");
        Put("V/m.txt", "same\n");
        Put("B/c.txt", "one\n");
        Put("V/c.txt", "two\n");
        var meta = new FileDelta("m.txt", ChangeReason.Metadata, Sha(P("B/m.txt")), Sha(P("V/m.txt")), 5, 5, [], []);
        var content = new FileDelta("c.txt", ChangeReason.Content, Sha(P("B/c.txt")), Sha(P("V/c.txt")), 4, 4, [new Hunk(HunkOp.Replace, 1, 1, 1, 1)], []);
        var manifest = new DeltaManifest(1, [], [], [], [content, meta], null);
        var fo = DeltaFileOpsCheck.Run(new DirectoryComparer(NoCache).Compare(P("B"), P("V")), manifest, baseDir: P("B"), variantDir: P("V"));
        Assert.True(fo.Ok, string.Join("; ", fo.Missing.Concat(fo.Extra).Concat(fo.WrongReason)));
        Assert.Equal(1, fo.MetadataCount);
        Assert.Equal(2, fo.Matched);

        Put("V/m.txt", "changed after all\n"); // a "metadata" file whose content differs is not unchanged
        var bad = DeltaFileOpsCheck.Run(new DirectoryComparer(NoCache).Compare(P("B"), P("V")), manifest, baseDir: P("B"), variantDir: P("V"));
        Assert.False(bad.Ok);
        Assert.Contains(bad.Missing, m => m.Contains("unchanged m.txt"));
        Assert.Contains(bad.Extra, x => x == "modified m.txt");
    }

    // ---- B6: a path twice ----

    private static string Rec(string path, string reason)
        => $$"""{"path":"{{path}}","reason":"{{reason}}","oldSha":"a","newSha":"b","oldSize":1,"newSize":1,"hunks":[]}""";

    [Theory]
    [InlineData("""{"modified":[R1,R2]}""")]
    [InlineData("""{"added":["x","x"]}""")]
    [InlineData("""{"removed":["x"],"renamed":[{"from":"x","to":"y","similarityMilli":1000}]}""")]
    [InlineData("""{"added":["y"],"renamed":[{"from":"x","to":"y","similarityMilli":1000}]}""")]
    [InlineData("""{"renamed":[{"from":"x","to":"y","similarityMilli":1000},{"from":"x","to":"z","similarityMilli":1000}]}""")]
    public void APathTwiceInADelta_IsRefused(string fileOps)
    {
        var json = """{"_meta":{"manifestVersion":1},"fileOps":""" + fileOps.Replace("R1", Rec("x", "content")).Replace("R2", Rec("x", "whitespace")) + "}";
        var ex = Assert.Throws<FormatException>(() => DeltaManifestParser.Parse(json));
        Assert.Contains("more than once", ex.Message);
    }

    // ---- A2: streamed verdicts ----

    [Fact]
    public void StreamedVerdicts_AreTheWholeReadsOnes_WithOrWithoutUtf8()
    {
        var latin1 = Encoding.Latin1;
        var utf8 = new UTF8Encoding(false);
        var cases = new (byte[] L, byte[] R, ChangeReason Want)[]
        {
            (utf8.GetBytes("A café\n"), utf8.GetBytes("B café\n"), ChangeReason.Content), // ASCII at the difference
            (latin1.GetBytes("x café\n"), utf8.GetBytes("y café\n"), ChangeReason.Content),
            (latin1.GetBytes("café x\n"), utf8.GetBytes("café x\n"), ChangeReason.Encoding), // the same text, re-saved
            (latin1.GetBytes("café x\n"), utf8.GetBytes("café y\n"), ChangeReason.Content),
            (latin1.GetBytes("ab\n"), utf8.GetBytes("ab\u00e9\n"), ChangeReason.Content),
        };
        var streamed = new ReasonClassifier(maxClassifyBytes: 1);
        var whole = new ReasonClassifier();
        int i = 0;
        foreach (var (l, r, want) in cases)
        {
            PutBytes($"s/{i}.l", l);
            PutBytes($"s/{i}.r", r);
            Assert.Equal(want, whole.Classify(P($"s/{i}.l"), l.Length, P($"s/{i}.r"), r.Length));
            Assert.Equal(want, streamed.Classify(P($"s/{i}.l"), l.Length, P($"s/{i}.r"), r.Length));
            i++;
        }
    }

    // ---- A8: a corrupt ledger ----

    [Fact]
    public void ALedgerWithAnOffsetPastTheEnd_IsNoCache_AndTheCompareRuns()
    {
        Put("L/a.txt", "one\n");
        Put("L/b.txt", "two\n");
        Put("R/a.txt", "one\n");
        Put("R/b.txt", "two!\n");
        var cache = new CompareOptions { Cache = CacheMode.On, CacheBaseDir = P("cache") };
        Assert.Single(new DirectoryComparer(cache).Compare(P("L"), P("R")).Changes, c => c.Status == ChangeStatus.Modified);

        var ledger = Path.Combine(P("cache"), LedgerFormat.RootKey(Path.GetFullPath(P("L"))));
        Assert.NotNull(LedgerFormat.TryRead(ledger));
        var basePath = Directory.GetFiles(ledger, "snapshot-*.base").Single();
        var bytes = File.ReadAllBytes(basePath);
        long offsOff = BitConverter.ToInt64(bytes, 12);
        BitConverter.GetBytes(long.MaxValue - 4).CopyTo(bytes, (int)offsOff + 8); // the first path's end: blobOff + it overflows
        File.WriteAllBytes(basePath, bytes);

        Assert.Null(LedgerFormat.TryRead(ledger));
        Assert.Single(new DirectoryComparer(cache).Compare(P("L"), P("R")).Changes, c => c.Status == ChangeStatus.Modified);
    }

    // ---- C3: saved diffs ----

    [Fact]
    public void SavedDiffsAtAnotherContext_NeverShareAName()
    {
        string Many(string tag) => string.Concat(Enumerable.Range(1, 60).Select(i => i % 3 == 0 ? $"{tag} {i}\n" : $"line {i}\n"));
        foreach (var name in new[] { "x", "x.U5" })
        {
            Put($"L/{name}", Many("old"));
            Put($"R/{name}", Many("new " + name));
        }
        var s = Done(new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache));
        static string Saved(string answer)
        {
            var line = answer.Split('\n').Single(l => l.Contains("full patch: "));
            return line[(line.IndexOf("full patch: ", StringComparison.Ordinal) + "full patch: ".Length)..];
        }
        var atFive = Saved(AgentViews.FileDiff(s, "x", context: 5, maxLines: 5));
        var named = Saved(AgentViews.FileDiff(s, "x.U5", maxLines: 5));
        Assert.NotEqual(atFive, named, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("+new x 3\n", File.ReadAllText(atFive));
        Assert.Contains("+new x.U5 3\n", File.ReadAllText(named));
    }

    // ---- C4: zero context ----

    [Fact]
    public void AZeroContextPatch_SaysItNeedsUnidiffZero()
    {
        Put("L/a.txt", "1\n2\n3\n");
        Put("R/a.txt", "1\ntwo\n3\n");
        var s = Done(new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache));
        var answer = AgentViews.Export(s, P("out/zero.patch"), context: 0);
        Assert.Contains("--unidiff-zero", answer);
        Assert.StartsWith("# written with no context lines", File.ReadAllText(P("out/zero.patch")));
        AgentViews.Export(s, P("out/three.patch"));
        Assert.DoesNotContain("unidiff-zero", File.ReadAllText(P("out/three.patch")));
    }

    // ---- C5: report replace ----

    [Fact]
    public void AReportReplaceThatFailsPartWay_IsReplacedNextTime()
    {
        Put("L/a.txt", "1\n");
        Put("R/a.txt", "2\n");
        var s = Done(new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache));
        var opt = new HtmlReportOptions { OutDir = P("report") };
        HtmlReport.Write(s, opt);
        Put("report/data/ro.txt", "x");
        File.SetAttributes(P("report/data/ro.txt"), FileAttributes.ReadOnly);
        HtmlReport.Write(s, opt); // a read-only file is no obstacle
        Assert.False(File.Exists(P("report/data/ro.txt")));

        if (!OperatingSystem.IsWindows()) return; // an open file blocks a delete only on Windows
        Put("report/full/held.txt", "x");
        using (new FileStream(P("report/full/held.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<Exception>(() => HtmlReport.Write(s, opt));
        Assert.True(File.Exists(P("report/" + HtmlReport.Marker)));
        HtmlReport.Write(s, opt);
        Assert.True(File.Exists(P("report/index.html")));
    }

    // ---- A7: whitespace hunks ----

    [Fact]
    public void AWhitespaceOnlyChangeAmongRepeatedLines_IsDiffedLineForLine()
    {
        string[] old = ["x", "  x", "y"], @new = ["  x", "x", "y"];
        var hunks = LineDiffer.WhitespaceAligned(old, @new)!;
        Assert.Equal([new Hunk(HunkOp.Replace, 1, 2, 1, 2)], hunks);
        Assert.Null(HunkApplier.Problem(old, @new, hunks, whitespaceOnly: true));
        Assert.NotNull(HunkApplier.Problem(old, @new, LineDiffer.Diff(old, @new), whitespaceOnly: true)); // Myers' pairing
        Assert.Null(LineDiffer.WhitespaceAligned(["x", "y"], ["x", "z"])); // content: Myers as ever

        var w = new StringWriter { NewLine = "\n" };
        UnifiedDiff.Write(w, "f", "f", "x\n  x\ny\n", "  x\nx\ny\n");
        Assert.Contains("-x\n-  x\n+  x\n+x\n", w.ToString());
    }

    // ---- A3: a space out of a string or a number ----

    [Theory]
    [InlineData("s.split(\" \");", "s.split(\"\");", ChangeReason.Content)]
    [InlineData("c = ' ';", "c = '';", ChangeReason.Content)]
    [InlineData("print(\"Total: \" + n);", "print(\"Total:\" + n);", ChangeReason.Content)]
    [InlineData("w = L \"x\";", "w = L\"x\";", ChangeReason.Content)]
    [InlineData("y = 1 .5;", "y = 1.5;", ChangeReason.Content)]
    [InlineData("s = \"a\\\" b\";", "s = \"a\\\"b\";", ChangeReason.Content)] // an escaped quote doesn't end the string
    [InlineData("s = \"it's: \";", "s = \"it's:\";", ChangeReason.Content)] // another quote inside is text
    [InlineData("x = \"a\";", "x=\"a\";", ChangeReason.Whitespace)]
    [InlineData("f( \"a\" , 'b' );", "f(\"a\", 'b');", ChangeReason.Whitespace)]
    [InlineData("s = \"a  b\";", "s = \"a b\";", ChangeReason.Whitespace)] // a run's width is never kept
    [InlineData("// don't   \nx;", "// don't\nx;", ChangeReason.Whitespace)] // trailing spaces after an apostrophe
    [InlineData("a .b();", "a.b();", ChangeReason.Whitespace)]
    public void ASpaceOutOfAStringOrANumber_IsContent_WholeOrStreamed(string left, string right, ChangeReason want)
    {
        Put("w/l.c", "int a;\n" + left + "\n");
        Put("w/r.c", "int a;\n" + right + "\n");
        long ll = new FileInfo(P("w/l.c")).Length, rl = new FileInfo(P("w/r.c")).Length;
        Assert.Equal(want, new ReasonClassifier().Classify(P("w/l.c"), ll, P("w/r.c"), rl));
        Assert.Equal(want, new ReasonClassifier(maxClassifyBytes: 1).Classify(P("w/l.c"), ll, P("w/r.c"), rl));
    }
}
