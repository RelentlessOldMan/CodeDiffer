using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Report;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Code review pass 11, tier 3: a patch's eol/encoding note is of the whole file, an edited rename whose only edit is its
/// encoding says so, a NUL a patch would see makes the compare say binary too, report and overlay directories are
/// checked before the compare, files that show alike are told apart, a compare id is an id before it is a folder,
/// a bad export path is an answer, and a reopened compare's export says it is of today's trees.
/// </summary>
public sealed class Round11Tier3Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r11c-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public Round11Tier3Tests() => Directory.CreateDirectory(_dir);

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

    private static CompareSession Done(CompareSession s)
    {
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Null(s.Error);
        return s;
    }

    private CompareSession Compare() => Done(new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache));

    /// <summary>About 70 KB of ASCII lines: more than the 64 KB a note used to look at.</summary>
    private static string Ascii(string eol = "\n") => string.Concat(Enumerable.Range(1, 7000).Select(i => $"line {i:D4}" + eol));

    // ---- notes judged on the whole file ----

    [Fact]
    public void AnEolNote_SeesLineEndingsPastTheFirst64KB()
    {
        Put("L/x.txt", Ascii() + "tail\n");
        Put("R/x.txt", Ascii() + "tail\r\n");
        var s = Compare();
        Assert.Equal(ChangeReason.Eol, s.Report!.Changes.Single(c => c.Status == ChangeStatus.Modified).Reason);
        Assert.Contains("a/x.txt (LF) b/x.txt (mixed)", AgentViews.FileDiff(s, "x.txt"));
    }

    [Fact]
    public void AnEncodingNote_SeesALegacyBytePastTheFirst64KB()
    {
        PutBytes("L/x.txt", [.. Encoding.ASCII.GetBytes(Ascii()), .. "caf"u8, 0xE9, (byte)'\n']);
        Put("R/x.txt", Ascii() + "café\n");
        var s = Compare();
        Assert.Equal(ChangeReason.Encoding, s.Report!.Changes.Single(c => c.Status == ChangeStatus.Modified).Reason);
        var diff = AgentViews.FileDiff(s, "x.txt");
        Assert.Contains("(a legacy code page, not UTF-8)", diff);
        Assert.Contains("(UTF-8/ASCII, no BOM)", diff);
    }

    // ---- an edited rename whose only edit is its encoding ----

    [Fact]
    public void ARenameThatOnlyGainedABom_SaysTheEncodingChanged()
    {
        var text = string.Concat(Enumerable.Range(1, 20).Select(i => $"some line {i}\n"));
        Put("L/a/r.txt", text);
        PutBytes("R/b/r.txt", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]);
        var s = Compare();
        Assert.Contains(s.Report!.Changes, c => c.Status == ChangeStatus.Renamed && !c.PureRename);
        Assert.Contains("Encoding differs too: a/a/r.txt (UTF-8/ASCII, no BOM) b/b/r.txt (UTF-8 BOM)", AgentViews.FileDiff(s, "b/r.txt"));
    }

    // ---- a NUL a patch would see ----

    [Fact]
    public void ANulPastTheHead_OfAFileAPatchReadsWhole_IsBinary()
    {
        var text = Encoding.ASCII.GetBytes(new string('a', 9999) + "\n");
        var withNul = (byte[])text.Clone();
        withNul[9000] = 0;
        PutBytes("L/n.txt", text);
        PutBytes("R/n.txt", withNul);
        // Read by their heads (as an 8-16 MB file is), and streamed for the rest.
        Assert.Equal(ChangeReason.Binary, new ReasonClassifier(maxClassifyBytes: 64).Classify(P("L/n.txt"), text.Length, P("R/n.txt"), withNul.Length));
        Assert.Equal(ChangeReason.Binary, new ReasonClassifier().Classify(P("L/n.txt"), text.Length, P("R/n.txt"), withNul.Length));
    }

    [Fact]
    public void AWideBomFile_ReadByItsHead_IsNotBinaryForItsNuls()
    {
        var l = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(new string('a', 6000) + "\n")).ToArray();
        var r = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(new string('a', 6000) + "b\n")).ToArray();
        PutBytes("L/w.txt", l);
        PutBytes("R/w.txt", r);
        Assert.NotEqual(ChangeReason.Binary, new ReasonClassifier(maxClassifyBytes: 64).Classify(P("L/w.txt"), l.Length, P("R/w.txt"), r.Length));
    }

    // ---- output directories checked before the compare ----

    [Fact]
    public void AReportOutDir_ThatIsAFileOrNotAReport_IsAProblemUpFront()
    {
        Put("afile", "x");
        Put("theirs/notes.txt", "keep me");
        Directory.CreateDirectory(P("empty"));
        Assert.Contains("is a file", HtmlReport.OutDirProblem(P("afile"), []));
        Assert.Contains("not a CodeDiffer report", HtmlReport.OutDirProblem(P("theirs"), []));
        Assert.Null(HtmlReport.OutDirProblem(P("empty"), []));
        Assert.Null(HtmlReport.OutDirProblem(P("new"), []));
    }

    [Fact]
    public void AnOverlayDir_OverlappingTheReportDir_IsRefusedUpFront()
    {
        foreach (var d in new[] { "B", "V1", "V2" }) Directory.CreateDirectory(P(d));
        Assert.Throws<ArgumentException>(() => MergeOverlay.CheckDir(P("out"), P("B"), P("V1"), P("V2"), P("out")));
        Assert.Throws<ArgumentException>(() => MergeOverlay.CheckDir(P("out"), P("B"), P("V1"), P("V2"), P("out/report")));
        Assert.Throws<ArgumentException>(() => MergeOverlay.CheckDir(P("out/merge"), P("B"), P("V1"), P("V2"), P("out")));
        MergeOverlay.CheckDir(P("merge"), P("B"), P("V1"), P("V2"), P("report"));
    }

    // ---- names that show alike ----

    [Fact]
    public void TwoNamesThatShowAlike_AreToldApartByTheirEscapedNames()
    {
        foreach (var n in new[] { "c\uD801.txt", "c\uD802.txt" })
        {
            Put("L/d/" + n, "one\ntwo\n");
            Put("R/d/" + n, "one\nTWO\n");
        }
        var s = Compare();
        var ambiguous = AgentViews.FileDiff(s, "d/c�.txt");
        Assert.Contains("escaped name", ambiguous);
        Assert.Contains(@"d/c\uD801.txt", ambiguous);
        Assert.Contains(@"d/c\uD802.txt", ambiguous);

        var one = AgentViews.FileDiff(s, @"d/c\uD802.txt");
        Assert.Contains("+TWO", one);
        Assert.DoesNotContain("not in compare", one);

        // A pattern copied from the listing (U+FFFD) matches them.
        Assert.True(AgentViews.Glob("d/c�.txt")("d/c\uD801.txt"));
        Assert.True(AgentViews.Glob("*�.txt")("d/c\uD802.txt"));

        // The HTML report tells them apart too (O8): by the same escaped names, never two rows of "c�.txt".
        var rep = CodeDiffer.Core.Report.HtmlReport.Write(s);
        var (_, files) = CodeDiffer.Core.Report.HtmlReport.ReadIndex(Path.Combine(Path.GetDirectoryName(rep.IndexPath)!, "data", "index.js"));
        var shown = files.Select(f => f.GetProperty("p").GetString()).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([@"d/c\uD801.txt", @"d/c\uD802.txt"], shown);
    }

    [Fact]
    public void EscapedNames_ReadBack()
    {
        const string name = "a\uD800b😀c\uDC01";
        Assert.Equal(@"a\uD800b" + "😀" + @"c\uDC01", ResultStore.Escaped(name));
        Assert.Equal(name, ResultStore.Unescaped(ResultStore.Escaped(name)));
        Assert.Equal(@"dirAx", ResultStore.Unescaped(@"dirAx")); // only surrogates are escaped
    }

    // ---- an id before a folder ----

    [Fact]
    public void ACompareId_IsAnIdBeforeAFolderOfThatName()
    {
        Put("L/a.txt", "1\n");
        Put("R/a.txt", "2\n");
        var id = Compare().Id;
        var folder = Path.Combine(Environment.CurrentDirectory, id);
        bool made = !Directory.Exists(folder);
        Directory.CreateDirectory(folder);
        try
        {
            var reopened = new SessionStore(resultsRoot: P("results")).Get(id);
            Assert.NotNull(reopened);
            Assert.Equal(id, reopened!.Id);
        }
        finally
        {
            if (made) Directory.Delete(folder);
        }
    }

    // ---- export ----

    [Fact]
    public void ABadExportPath_IsAnAnswer()
    {
        Put("L/a.txt", "1\n");
        Put("R/a.txt", "2\n");
        var s = Compare();
        Directory.CreateDirectory(P("adir"));
        foreach (var bad in new[] { "NUL", "CON", @"\", "a\0b", P("adir") })
            Assert.StartsWith("error:", AgentViews.Export(s, bad));
        Assert.StartsWith("changeset", AgentViews.Export(s, P("out/ok.patch")));
    }

    [Fact]
    public void AReopenedCompare_ExportSaysItIsOfTodaysTrees()
    {
        Put("L/a.txt", "1\n");
        Put("R/a.txt", "2\n");
        var id = Compare().Id;
        var reopened = (CompareSession)new SessionStore(resultsRoot: P("results")).Get(id)!;
        Assert.True(reopened.Reopened);
        Assert.Contains("saved compare from", AgentViews.Export(reopened, P("out/re.patch")));
    }
}
