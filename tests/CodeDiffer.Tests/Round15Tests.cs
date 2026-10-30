using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Port;
using CodeDiffer.Core.Report;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Code review pass 15: an apostrophe that isn't a quote, or a raw string, never puts a change in a real string out of it;
/// a rename's verdict is the same both ways; apply stopped in a file's merge says what it did; verify pairs a last line
/// without a newline as the engine does; a temp name fits a Samba share's 255 bytes; one report directory spelled two
/// ways is one lock.
/// </summary>
public sealed class Round15Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r15-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public Round15Tests() => Directory.CreateDirectory(_dir);

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

    private string Classify(string leftName, string left, string rightName, string right, bool streamed)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        Put($"{id}/l/{leftName}", left);
        Put($"{id}/r/{rightName}", right);
        string l = P($"{id}/l/{leftName}"), r = P($"{id}/r/{rightName}");
        var c = streamed ? new ReasonClassifier(0) : new ReasonClassifier();
        return c.Classify(l, new FileInfo(l).Length, r, new FileInfo(r).Length).ToString().ToLowerInvariant();
    }

    // ---- an apostrophe, a raw string ----

    [Theory]
    [InlineData(".rs", "fn f(x: &'static str) { println!(\"can't a= b\"); }\n", "fn f(x: &'static str) { println!(\"can't a=b\"); }\n")]
    [InlineData(".c", "/* it's */ puts(\"don't a= b\");\n", "/* it's */ puts(\"don't a=b\");\n")]
    [InlineData(".rs", "let p = r\"C:\\\"; let s = \"a= b\";\n", "let p = r\"C:\\\"; let s = \"a=b\";\n")]
    [InlineData(".rs", "let p = r#\"C:\\\"#; let s = \"a= b\";\n", "let p = r#\"C:\\\"#; let s = \"a=b\";\n")]
    [InlineData(".cpp", "auto p = R\"(C:\\)\"; puts(\"a= b\");\n", "auto p = R\"(C:\\)\"; puts(\"a=b\");\n")]
    [InlineData(".go", "p := `C:\\`; s := \"a= b\"\n", "p := `C:\\`; s := \"a=b\"\n")]
    [InlineData(".dart", "var p = r'C:\\'; var s = 'a= b';\n", "var p = r'C:\\'; var s = 'a=b';\n")]
    [InlineData(".m", "p = 'C:\\'; s = 'a= b';\n", "p = 'C:\\'; s = 'a=b';\n")]
    [InlineData(".cs", "var p = \"\"\"C:\\\"\"\"; var s = \"a= b\";\n", "var p = \"\"\"C:\\\"\"\"; var s = \"a=b\";\n")]
    public void AChangeInARealString_IsContent(string ext, string left, string right)
    {
        Assert.Equal("content", Classify("x" + ext, left, "x" + ext, right, streamed: false));
        Assert.Equal("content", Classify("x" + ext, left, "x" + ext, right, streamed: true));
        Assert.False(TextInspector.SameButWhitespace(left, right, TextInspector.RulesFor("x" + ext)));
    }

    [Theory]
    [InlineData(".rs", "fn f<'a>(x: &'a str) {  g(x); }\n", "fn f<'a>(x: &'a str) { g(x); }\n")]
    [InlineData(".c", "if (c == '\\'' || c == '\"') {\n", "if (c=='\\''||c=='\"') {\n")]
    [InlineData(".rs", "let p = r\"C:\\\\\";  x=1;\n", "let p = r\"C:\\\\\"; x = 1;\n")] // a raw string not ending in a backslash
    public void AReformatBesideThem_IsStillWhitespace(string ext, string left, string right)
    {
        Assert.Equal("whitespace", Classify("x" + ext, left, "x" + ext, right, streamed: false));
        Assert.Equal("whitespace", Classify("x" + ext, left, "x" + ext, right, streamed: true));
    }

    [Fact]
    public void ARenameAcrossLanguages_ReadsTheSameBothWays()
    {
        const string c = "if (c == '\\'' || c == '\"') {\n", t = "if (c=='\\''||c=='\"') {\n";
        foreach (var streamed in new[] { false, true })
            Assert.Equal(Classify("x.c", c, "x.txt", t, streamed), Classify("x.txt", c, "x.c", t, streamed));
        Assert.Equal(StringRules.Both, TextInspector.RulesFor("a.c", "a.txt"));
        Assert.Equal(StringRules.CSharp, TextInspector.RulesFor("a.cs", "b.CS"));
    }

    // ---- apply stopped in a merge ----

    [Fact]
    public void ApplyStoppedInAFilesMerge_SaysWhatItDid()
    {
        const int n = 200_000;
        var b = Enumerable.Range(0, n).Select(i => $"line {i}").ToList();
        string Tree(string side, IEnumerable<string> lines)
        {
            Put($"{side}/f.txt", string.Concat(lines.Select(l => l + "\n")));
            return P(side);
        }
        var l = Tree("L", b);
        var r = Tree("R", b.Select((x, i) => i % 3 == 0 ? x + " v1" : x));
        var t = Tree("T", b.Select((x, i) => i % 3 == 1 ? x + " v2" : x));
        var before = File.ReadAllBytes(P("T/f.txt"));
        var report = new DirectoryComparer(NoCache).Compare(l, r);
        using var stop = new CancellationTokenSource();
        stop.CancelAfter(300);
        var result = ChangePorter.Run(report, l, r, t, write: true, ct: stop.Token); // never throws: a report
        if (result.Cancelled)
        {
            Assert.Equal(PortStatus.NotReached, result.Files[0].Status);
            Assert.Equal(before, File.ReadAllBytes(P("T/f.txt")));
        }
    }

    // ---- verify reads a last line without a newline as the engine does ----

    [Theory]
    [InlineData("b;\nb;\n", "a;\nb;\n", "a;\nb;\na;")]
    [InlineData("};\na;\n};", "a;\n};", "a;\na;\n")]
    public void VerifyPairsALastLineAsTheEngine(string b, string v1, string v2)
        => Assert.Equal(Engine(b, v1, v2), Verify(b, v1, v2));

    [Fact]
    public void VerifyPairsLinesAsTheEngine_Fuzz()
    {
        string[] alphabet = ["a;", "b;", "c;", "a;", "}", "};", "x"];
        var rng = new Random(15);
        for (int it = 0; it < 60_000; it++)
        {
            var b = Enumerable.Range(0, rng.Next(1, 7)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToList();
            List<string> Edit()
            {
                var v = new List<string>(b);
                for (int k = rng.Next(1, 4); k > 0; k--)
                {
                    int op = rng.Next(3);
                    if (op == 0 || v.Count == 0) v.Insert(rng.Next(v.Count + 1), alphabet[rng.Next(alphabet.Length)]);
                    else if (op == 1) v.RemoveAt(rng.Next(v.Count));
                    else v[rng.Next(v.Count)] = alphabet[rng.Next(alphabet.Length)];
                }
                return v;
            }
            string Text(List<string> lines) => lines.Count == 0 ? "" : string.Join("\n", lines) + (rng.Next(3) != 0 ? "\n" : "");
            string tb = Text(b), t1 = Text(Edit()), t2 = Text(Edit());
            var eng = Engine(tb, t1, t2);
            var ver = Verify(tb, t1, t2);
            Assert.True(eng.SequenceEqual(ver), $"b={tb.Replace("\n", "/")} v1={t1.Replace("\n", "/")} v2={t2.Replace("\n", "/")}");
        }
    }

    private static List<MergeRegion> Engine(string b, string v1, string v2)
        => ThreeWayMerger.Regions(ChangePorter.Lines(b, false), ChangePorter.Lines(v1, false), ChangePorter.Lines(v2, false));

    private static List<MergeRegion> Verify(string b, string v1, string v2)
        => ThreeWayMerger.Regions(UnifiedDiff.DiffLines(b), UnifiedDiff.DiffLines(v1), UnifiedDiff.DiffLines(v2));

    // ---- a name on a Samba share ----

    [Fact]
    public void ANameBesideAWideName_FitsInBytesToo()
    {
        var name = new string('\u4E2D', 80) + ".c"; // 242 bytes of UTF-8: a name ext4 takes
        var beside = Path.GetFileName(TempNames.Beside(Path.Combine(_dir, name), ".codediffer.tmp"));
        Assert.True(TempNames.Fits(beside), $"{beside.Length} units, {Encoding.UTF8.GetByteCount(beside)} bytes");
        Assert.Equal(P(name) + ".x", TempNames.Beside(P(name), ".x"));         // what still fits keeps its name

        for (int reserve = 0; reserve < 260; reserve += 7)
        {
            var fit = TempNames.Fit(new string('\u4E2D', 200), reserve);
            Assert.True(Encoding.UTF8.GetByteCount(fit) <= Math.Max(0, TempNames.MaxName - reserve));
            Assert.True(fit.Length >= (TempNames.MaxName - reserve) / 3 - 1);   // cut no further than it must
        }
        var pairs = TempNames.Fit(string.Concat(Enumerable.Repeat("\U0001F600", 100)), 20);
        Assert.True(Encoding.UTF8.GetByteCount(pairs) <= TempNames.MaxName - 20);
        Assert.Equal(0, pairs.Length % 2);                                        // never half a character
    }

    // ---- one report directory, two spellings ----

    [Fact]
    public void ReportsToOneDirectorySpelledTwoWays_WaitTheirTurn()
    {
        for (int i = 0; i < 60; i++)
        {
            Put($"L/f{i:D2}.c", string.Concat(Enumerable.Range(0, 200).Select(k => $"int f{i}_{k}() {{ return {k}; }}\n")));
            Put($"R/f{i:D2}.c", string.Concat(Enumerable.Range(0, 200).Select(k => $"int f{i}_{k}() {{ return {k + 1}; }}\n")));
        }
        var s = new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Null(s.Error);
        var d = Path.GetFullPath(P("rep"));
        Directory.CreateDirectory(d);
        string[] spellings = [d, @"\\?\" + d];
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        for (int round = 0; round < 5; round++)
            Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                try { HtmlReport.Write(s, new HtmlReportOptions { OutDir = spellings[i % 2] }); }
                catch (Exception ex) { errors.Add(ex.GetType().Name + ": " + ex.Message); }
            });
        Assert.Empty(errors);
    }

    [Fact]
    public void NormalizedLines_AreTheLinesOfTheNormalizedText()
    {
        var rnd = new Random(11);
        foreach (var fixedCase in new[] { "", "\n", "\r", "\r\n", "a\r\r\nb", "a\n\rb\r", "\r\n\r\n", "x" })
            Assert.Equal(LineText.SplitLines(TextInspector.NormalizeEol(fixedCase)), LineText.NormalizedLines(fixedCase));
        for (int n = 0; n < 100_000; n++)
        {
            var s = new string([.. Enumerable.Range(0, rnd.Next(16)).Select(_ => "ab\r\n"[rnd.Next(4)])]);
            Assert.Equal(LineText.SplitLines(TextInspector.NormalizeEol(s)), LineText.NormalizedLines(s));
        }
    }

    [Fact]
    public void SameButWhitespace_ComparedAPieceAtATime_IsTheTwoKeysCompared()
    {
        var rnd = new Random(7);
        var alpha = "ab_1 \t\u000b\u000c\n\r\"'`\\@$#-+*/=<>.;(){}\u00e9";
        for (int n = 0; n < 100_000; n++)
        {
            var rules = (StringRules)rnd.Next(5);
            string R() => new([.. Enumerable.Range(0, rnd.Next(24)).Select(_ => alpha[rnd.Next(alpha.Length)])]);
            var a = R();
            var b = rnd.Next(3) == 0 ? a.Replace(" ", rnd.Next(2) == 0 ? "  " : "").Replace("\t", " ") : R();
            Assert.Equal(TextInspector.WhitespaceKey(a, rules) == TextInspector.WhitespaceKey(b, rules), TextInspector.SameButWhitespace(a, b, rules));
        }
    }
}
