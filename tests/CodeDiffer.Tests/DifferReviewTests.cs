using System.Text;
using CodeDiffer.Core.Compare;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The code-differ correctness review (2026-10-16): reason labels that under-claimed a change — a cp1252 file's
/// C1 bytes read as Latin-1, quote parity, line continuations, a macro's parameter list — whole and streamed alike.
/// </summary>
public sealed class DifferReviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-review-" + Guid.NewGuid().ToString("N"));

    public DifferReviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Classify(byte[] left, byte[] right, string ext, bool streamed)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        string l = Path.Combine(_dir, $"{id}.l{ext}"), r = Path.Combine(_dir, $"{id}.r{ext}");
        File.WriteAllBytes(l, left);
        File.WriteAllBytes(r, right);
        var c = streamed ? new ReasonClassifier(0) : new ReasonClassifier();
        return c.Classify(l, left.Length, r, right.Length).ToString().ToLowerInvariant();
    }

    private string Classify(string left, string right, string ext, bool streamed)
        => Classify(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right), ext, streamed);

    // ---- K1: quote parity, kept past a comment, a raw string's inner quotes and a triple-quoted string's ----

    [Theory]
    [InlineData(".c", "int w = 3; /* 3\" wide */ printf(\"%d , %d\", a, b);\n", "int w = 3; /* 3\" wide */ printf(\"%d, %d\", a, b);\n", "content")]
    [InlineData(".c", "int w = 3; // 3\" wide\nprintf(\"%d , %d\", a);\n", "int w = 3; // 3\" wide\nprintf(\"%d, %d\", a);\n", "content")]
    [InlineData(".cpp", "auto s = R\"(msg=\"Hello , world\")\";\n", "auto s = R\"(msg=\"Hello, world\")\";\n", "content")]
    [InlineData(".cpp", "auto s = R\"x(a)\" , b)x\";\n", "auto s = R\"x(a)\", b)x\";\n", "content")]
    [InlineData(".py", "s = \"\"\"say \"x , y\" ok\"\"\"\n", "s = \"\"\"say \"x, y\" ok\"\"\"\n", "content")]
    [InlineData(".py", "s = '''it's , here'''\n", "s = '''it's, here'''\n", "content")]
    [InlineData(".cs", "var s = \"\"\"say \"x , y\" ok\"\"\";\n", "var s = \"\"\"say \"x, y\" ok\"\"\";\n", "content")]
    [InlineData(".c", "/* don't */ s = \"a , b\";\n", "/* don't */ s = \"a, b\";\n", "content")]  // controls
    [InlineData(".c", "s = \"a\\\" , b\";\n", "s = \"a\\\", b\";\n", "content")]
    [InlineData(".c", "c = '\"'; s = \"a , b\";\n", "c = '\"'; s = \"a, b\";\n", "content")]
    [InlineData(".js", "let s = `a \"b , c\" d`;\n", "let s = `a \"b, c\" d`;\n", "content")]
    [InlineData(".c", "x = a / b; /* c */ y=1; // d\n", "x = a/b; /* c */ y = 1; // d\n", "whitespace")]
    [InlineData(".c", "p = \"/*\";  x=1; /**/ z=2;\n", "p = \"/*\"; x = 1; /**/ z = 2;\n", "whitespace")]
    [InlineData(".cpp", "auto s = R\"(a)\";  x=1;\n", "auto s = R\"(a)\"; x = 1;\n", "whitespace")]
    [InlineData(".py", "s = \"\"\"a\"\"\";  x=1\n", "s = \"\"\"a\"\"\"; x = 1\n", "whitespace")]
    [InlineData(".py", "s = r\"a b\";  x=1\n", "s = r\"a b\"; x = 1\n", "whitespace")]
    public void AQuoteInACommentOrInsideARawOrTripleString_LeavesTheStringsWhereTheyAre(string ext, string a, string b, string expected)
    {
        foreach (var streamed in new[] { false, true })
            Assert.Equal(expected, Classify(a, b, ext, streamed));
    }

    // ---- K2: a backslash-newline splices two lines into one ----

    [Theory]
    [InlineData("#define A foo\\\n    bar\n", "#define A foo\\\nbar\n", "content")]       // foo bar → foobar
    [InlineData("#define A foo \\\nbar\n", "#define A foo\\\nbar\n", "content")]
    [InlineData("x = \"abc\\\n  , def\";\n", "x = \"abc\\\n, def\";\n", "content")]      // the string goes on
    [InlineData("#define A foo   \\\n    bar\n", "#define A foo \\\n  bar\n", "whitespace")] // the \ re-aligned
    [InlineData("#define A(x) \\\n  do { x; } while (0)\n", "#define A(x) \\\n\tdo { x; } while (0)\n", "whitespace")]
    [InlineData("a = b + \\\n    c;\n", "a = b +\\\nc;\n", "whitespace")]
    [InlineData("a = \"x\" \\\n  \"y\";\n", "a = \"x\"\\\n\"y\";\n", "content")]                // "x" "y" → "x""y"
    public void ASpliceJoinsItsLines_TheSpacingAcrossItIsOneRun(string a, string b, string expected)
    {
        foreach (var streamed in new[] { false, true })
            Assert.Equal(expected, Classify(a, b, ".h", streamed));
        // A whitespace change shows line for line: each line, checked alone, is whitespace too.
        if (expected == "whitespace")
            Assert.NotNull(CodeDiffer.Core.Diff.LineDiffer.WhitespaceAligned(a.Split('\n'), b.Split('\n'), StringRules.Escape));
    }

    [Fact]
    public void Splices_ReadTheSameWholeAndStreamed_AndBothIsEachReading()
    {
        var rng = new Random(2026);
        const string alphabet = "ab\"'+(\\\\\\\n# ";
        string Text(int n) => new([.. Enumerable.Range(0, n).Select(_ => alphabet[rng.Next(alphabet.Length)])]);
        string Respace(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (c == ' ') { if (rng.Next(3) > 0) sb.Append(' ', rng.Next(1, 3)); continue; } // a run kept, widened, or gone
                if (rng.Next(6) == 0) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }
        for (int n = 0; n < 3000; n++)
        {
            var l = Text(rng.Next(1, 20));
            var r = Respace(l);
            bool both = TextInspector.SameButWhitespace(l, r, StringRules.Both);
            bool each = TextInspector.SameButWhitespace(l, r, StringRules.Escape) && TextInspector.SameButWhitespace(l, r, StringRules.Literal);
            Assert.True(both == each, $"[{l}] / [{r}]: both {both}, each {each}");
            if (n % 10 == 0 && l != r) // the classifier is only ever asked about files that differ
                foreach (var ext in new[] { ".h", ".txt" })
                    Assert.True(Classify(l, r, ext, false) == Classify(l, r, ext, true), $"[{l}] / [{r}] {ext}: whole and streamed differ");
        }
    }

    // ---- K3: a macro's parameter list, an include's file name ----

    [Theory]
    [InlineData("#define F(x) x\n", "#define F (x) x\n", "content")]        // function-like turned object-like
    [InlineData("#  define F(x) x\n", "#  define F\t(x) x\n", "content")]
    [InlineData("#include <a.h>\n", "#include <a.h >\n", "content")]        // another file name
    [InlineData("#include <a.h>\n", "#include < a.h>\n", "content")]
    [InlineData("#define A 1\n", "#define  A   1\n", "whitespace")]          // controls: spacing only
    [InlineData("#define F(x) (x)\n", "#define F(x)  ( x )\n", "whitespace")]
    [InlineData("#include <a.h>\n", "#include  <a.h>\n", "whitespace")]
    [InlineData("x = F (y);\n", "x = F(y);\n", "whitespace")]                // a call, not a #define
    public void APreprocessorLinesSpacing_ThatChangesItsMeaning_IsContent(string a, string b, string expected)
    {
        foreach (var streamed in new[] { false, true })
            Assert.Equal(expected, Classify(a, b, ".h", streamed));
    }

    // ---- P1: a coarse line diff (past the work cap: one wide replace) says so wherever it is shown ----

    /// <summary>Two text files, every line different: past the line diff's work cap, so one replace over the lot.</summary>
    internal static void PutCoarsePair(string left, string right)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(left)!);
        Directory.CreateDirectory(Path.GetDirectoryName(right)!);
        File.WriteAllText(left, string.Concat(Enumerable.Range(0, 15_000).Select(i => $"left {i}\n")));
        File.WriteAllText(right, string.Concat(Enumerable.Range(0, 15_000).Select(i => $"right {i}\n")));
    }

    [Fact]
    public void ACoarseDiff_IsFlaggedCoarse_InGetFileDiff_AndTheHtmlReport()
    {
        string l = Path.Combine(_dir, "L"), r = Path.Combine(_dir, "R");
        PutCoarsePair(Path.Combine(l, "big.txt"), Path.Combine(r, "big.txt"));
        var s = new CodeDiffer.Core.Sessions.SessionStore(resultsRoot: Path.Combine(_dir, "res"))
            .Start(l, r, new CompareOptions { Cache = CodeDiffer.Core.Ledger.CacheMode.Off });
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));

        var head = CodeDiffer.Core.Sessions.AgentViews.FileDiff(s, "big.txt").Split('\n')[0];
        Assert.Contains("coarse", head);
        var rep = CodeDiffer.Core.Report.HtmlReport.Write(s);
        var (_, files) = CodeDiffer.Core.Report.HtmlReport.ReadIndex(Path.Combine(Path.GetDirectoryName(rep.IndexPath)!, "data", "index.js"));
        Assert.True(files.Single().TryGetProperty("co", out _));
    }

    // ---- K4: a legacy file's bytes 0x80–0x9F are cp1252 punctuation, not Latin-1 controls ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACp1252FileConvertedAsIfLatin1_IsContent_NotTheSameText(bool streamed)
    {
        // “Total € 5” in cp1252, "converted" byte for byte to U+0093 / U+0080 / U+0094: other characters, not a re-save.
        byte[] cp1252 = [0x93, .. "Total "u8, 0x80, .. " 5"u8, 0x94, (byte)'\n'];
        var wrong = Encoding.UTF8.GetBytes("\u0093Total \u0080 5\u0094\n");
        Assert.Equal("content", Classify(cp1252, wrong, ".txt", streamed));
        Assert.Equal("content", Classify(wrong, cp1252, ".txt", streamed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALatin1FileReSavedAsUtf8_IsStillEncoding(bool streamed)
    {
        byte[] latin = [.. "caf"u8, 0xE9, (byte)'\n'];
        Assert.Equal("encoding", Classify(latin, Encoding.UTF8.GetBytes("café\n"), ".txt", streamed));
    }
}
