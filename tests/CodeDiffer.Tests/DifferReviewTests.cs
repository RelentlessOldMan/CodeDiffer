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
