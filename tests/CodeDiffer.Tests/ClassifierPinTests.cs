using System.Security.Cryptography;
using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The verdict store keeps a pair's reason and noise across releases, keyed by <see cref="VerdictStore.ClassifierVersion"/>,
/// not the build. So the classifier is pinned here two ways: its answers over generated pairs (every name and extension its
/// tables know, small files and ones past the head sniff and a read buffer, every encoding it tells apart, read whole and
/// streamed, size-changed too, and the reasons' and noise's stored numbers), and its source. Answers changed: the version
/// must be bumped. Source changed with the same answers: someone decides the change can't move a verdict (else bumps).
/// </summary>
public sealed class ClassifierPinTests
{
    /// <summary>What decides a pair's reason and noise (the stored verdict), and what feeds it: the tables and rules.</summary>
    private static readonly string[] Sources =
    [
        "src/CodeDiffer.Core/Compare/ReasonClassifier.cs",
        "src/CodeDiffer.Core/Compare/TextInspector.cs",
        "src/CodeDiffer.Core/Compare/NoiseFinder.cs",
        "src/CodeDiffer.Core/Compare/CommentSyntax.cs",
        "src/CodeDiffer.Core/Model/FileType.cs",
    ];

    /// <summary>A classifier version's answers, and a source that gives them. Answers changed: add a line with the next
    /// version (and bump <see cref="VerdictStore.ClassifierVersion"/> to it). Only the source changed, and no pair anywhere
    /// could be classified differently: add a line with the same version and answers. Never edit an earlier line.</summary>
    private static readonly (int Version, string Answers, string Source)[] Pins =
    [
        (1, "26AF27F50BD8C2B5", "125B4CEE867D6029"),
    ];

    [Fact]
    public void TheClassifiersAnswers_AreThoseOfItsVersion()
    {
        var (answers, reasons, noise) = Answers();

        // The pairs reach every reason a modified pair can get, and every kind of noise.
        Assert.Superset(new HashSet<ChangeReason> { ChangeReason.Content, ChangeReason.Eol, ChangeReason.Whitespace, ChangeReason.Encoding, ChangeReason.Binary }, reasons);
        Assert.Equal(ChangeNoise.Stamps | ChangeNoise.Numbers | ChangeNoise.Comments, noise);

        var source = SourceDigest();
        Assert.Equal(VerdictStore.ClassifierVersion, Pins[^1].Version);
        Assert.All(Pins.GroupBy(p => p.Answers), g => Assert.Single(g.Select(p => p.Version).Distinct()));
        var mine = Pins.Where(p => p.Version == VerdictStore.ClassifierVersion).ToList();
        Assert.True(mine.Any(p => p.Answers == answers),
            $"The classifier's answers changed (answers {answers}, source {source}): bump VerdictStore.ClassifierVersion and add it to Pins.");
        Assert.True(mine.Any(p => p.Answers == answers && p.Source == source),
            $"The classifier's source changed but not its answers here (source {source}). If no pair anywhere could be classified " +
            "differently, add a Pins line with this version, these answers and this source; else bump VerdictStore.ClassifierVersion.");
    }

    private static (string Digest, HashSet<ChangeReason> Reasons, ChangeNoise Noise) Answers()
    {
        using var t = new TempDir();
        var whole = new ReasonClassifier();
        var streamed = new ReasonClassifier(maxClassifyBytes: 64); // the Large path
        var capped = new ReasonClassifier(maxClassifyBytes: 64, maxStreamBytes: 32 * 1024); // past the streamed read's cap
        var log = new StringBuilder();
        // The numbers a store keeps for a reason and noise.
        foreach (var r in Enum.GetValues<ChangeReason>()) log.Append($"{r}={(int)r} ");
        foreach (var z in Enum.GetValues<ChangeNoise>()) log.Append($"{z}={(int)z} ");
        log.Append('\n');
        var reasons = new HashSet<ChangeReason>();
        var noise = ChangeNoise.None;
        int n = 0;
        foreach (var (leftName, rightName, left, right) in Pairs())
        {
            var a = t.PutBytes($"L/{n}/{leftName}", left);
            var b = t.PutBytes($"R/{n}/{rightName}", right);
            foreach (var c in (ReasonClassifier[])[whole, streamed, capped])
            {
                var r = c.Classify(a, left.Length, b, right.Length, out var z);
                log.Append($"{n} {leftName} {rightName} {(int)r}:{r} {(int)z}:{z}");
                reasons.Add(r);
                noise |= z;
                if (left.Length != right.Length)
                {
                    var rr = c.ClassifyResized(a, left.Length, b, right.Length, out _, out var zr);
                    log.Append($" | {(rr is { } x ? $"{(int)x}:{x}" : "same")} {(int)zr}:{zr}");
                }
                log.Append('\n');
            }
            n++;
        }
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(log.ToString())))[..16], reasons, noise);
    }

    private static string SourceDigest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodeDiffer.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var all = new StringBuilder();
        foreach (var s in Sources)
            all.Append(s).Append('\n').Append(File.ReadAllText(Path.Combine(dir.FullName, s)).Replace("\r\n", "\n")).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all.ToString())))[..16];
    }

    /// <summary>Every name the tables know (as "f" + an extension, or the file name itself), a few they don't, and
    /// some binary ones.</summary>
    private static List<string> Names()
        => CommentSyntax.Known.Concat(FileTypes.Known).Concat([".unknownext", ""])
            .Select(k => k.StartsWith('.') || k.Length == 0 ? "f" + k : k)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();

    private static readonly string[] Lines =
    [
        "int main(void) {",
        "    return x + 1; // add one",
        "/* a block comment */ int y = 2;",
        "# a hash comment with 42 in it",
        "-- a dash comment",
        "<!-- markup comment --> <a href=\"x\">link</a>",
        "s = \"not // a comment\";",
        "path = 'c:\\\\temp\\\\#notes'",
        "$Id: file.c 1234 2026-01-02 alice $",
        "$Revision: 17 $",
        "$Date: 2026/01/02 03:04:05 $",
        "version=1.2.3 build 456 on 2026-03-04 12:00:01",
        "id: A1B2C3, count 7, addr 0xDEADBEEF",
        "x = 1.5e-3 + -2.0E+4 - .5;",
        "\tindented\twith\ttabs",
        "  two  inner   spaces  ",
        "page\fbreak and\u00a0nbsp",
        "temperature 25°C, café, naïve",
        "\u0093smart quotes\u0094 and\u0085next", // C1 in Latin-1: what cp1252 text is read as
        "#define LONG_MACRO(a) \\",
        "    ((a) * 2)",
        "\"\"\"docstring line\"\"\"",
        "var p = @\"c:\\temp\\\"\" x  y\";",
        "var r = \"\"\"raw \"text\"  here\"\"\";",
        "Write-Host \"a `\"quoted`\"  word\" # ps comment",
        "<# a ps block #>",
        // A quote after a backtick (PowerShell) or backslash (a C# verbatim string, batch): an escape or not puts the
        // " + " after it in a string or between tokens, so taking its spaces out is content or whitespace.
        "$a = \"x `\" y\" + b",
        "p = @\"c:\\dir\\\" + q;",
        "set x=\"a\\\" + b",
        "--[[ a lua block ]]",
        "{- a haskell block -} (* an ml block *)",
        "REM a batch remark",
        ":: a batch label",
        "; an ini comment",
        "' a vb comment",
        "% a tex comment",
        "it's a 'quoted' word, don't",
        "key = value ; trailing comment",
        "",
        "end",
    ];

    private static readonly string[] Comments =
        ["// new comment", "# new comment", "-- new comment", "REM new comment", "<!-- new comment -->", "; new comment", "' new comment", "% new comment"];

    private static readonly string[] Single =
        [.. Comments, "@REM new comment", ":: new comment", "/*\n a c comment\n*/", "--[[\n a lua comment\n]]", "<#\n a ps comment\n#>"];

    /// <summary>Edits that each turn on one rule: a form feed for a space, spacing inside an exponent, a last line of
    /// only spaces added, hex words in a stamp.</summary>
    private static readonly (string, string)[] Edits =
    [
        ("x = 1\n", "x =\f1\n"),
        ("x = 2.0E+4;\n", "x = 2.0E + 4;\n"),
        ("x\ny\n", "x\ny\n  "),
        ("id = 1b4e28ba-2fa1-11d2-883f-0016d3cca427\n", "id = 6fa459ea-ee8a-3ca4-894e-db77e160355e\n"),
        ("commit a1b2c3d4e5f6 2026-10-24\n", "commit f6e5d4c3b2a1 2026-10-25\n"),
        ("rev 0x1a2b3c\n", "rev 0x4d5e6f\n"),
    ];

    /// <summary>A fixed set of pairs: a base document per name, and the same with one to three edits of every kind;
    /// some past the head sniff (8 KB) and the streamed read's buffer (64 KB), some renamed.</summary>
    private static IEnumerable<(string LeftName, string RightName, byte[] Left, byte[] Right)> Pairs()
    {
        uint seed = 12345;
        int Next(int max) { seed = seed * 1664525 + 1013904223; return (int)((seed >> 8) % (uint)max); }
        var names = Names();
        for (int i = 0; i < names.Count * 2 + 200; i++)
        {
            var name = names[i % names.Count];
            var other = i % 11 == 5 ? names[Next(names.Count)] : name; // a rename
            int big = i % 13 == 0 ? 80 * 1024 : i % 13 == 7 ? 12 * 1024 : 0;
            var doc = new List<string>();
            for (int k = 0, len = 6 + Next(14); k < len || doc.Sum(l => l.Length + 1) < big; k++) doc.Add(Lines[Next(Lines.Length)]);
            int leftEnc = Next(10) switch { 0 => 3, 1 => 5, 2 => 6, 3 => 1, _ => 0 };
            var left = Encode(string.Join('\n', doc) + "\n", leftEnc);
            var lines = new List<string>(doc);
            string eol = "\n", tail = "\n";
            int enc = leftEnc;
            for (int m = 0, edits = 1 + Next(3); m < edits; m++)
            {
                int at = big > 0 && Next(2) == 0 ? lines.Count - 1 - Next(Math.Min(lines.Count, 20)) : Next(lines.Count);
                switch (Next(22))
                {
                    case 0: eol = "\r\n"; tail = "\r\n"; break;
                    case 1: lines[at] += "\r"; break;
                    case 2: lines[at] += "  "; break;
                    case 3: lines[at] = lines[at].StartsWith('\t') ? "    " + lines[at][1..] : "\t" + lines[at].TrimStart(); break;
                    case 4: lines[at] = lines[at].Replace(" ", "  "); break;
                    case 5: lines[at] = lines[at].Replace("comment", "remark").Replace("block", "blk"); break;
                    case 6: lines[at] = Bump(lines[at]); break;
                    case 7: lines[at] = lines[at].Replace("1234", "1299").Replace("17", "18").Replace("e-3", "e-4"); break;
                    case 8: lines[at] = lines[at].Replace('e', 'E'); break;
                    case 9: lines.Insert(at, ""); break;
                    case 10: lines.Insert(at, "added line " + at); break;
                    case 11: lines.RemoveAt(at); break;
                    case 12: lines.Insert(at, Comments[Next(Comments.Length)]); break;
                    case 13: tail = ""; break;
                    case 14: lines[at] = lines[at].Replace("`\"", "\\\"").Replace("\"\"", "\\\""); break; // how a quote is escaped
                    case 15: lines[at] = lines[at].Replace("  ", " ").Replace("( ", "(").Replace(" + ", "+"); break;
                    case 16: lines[at] += "\0"; break; // a NUL, past the head in a big file
                    case 17: enc = 1; break;
                    case 18: enc = 2; break;
                    case 19: enc = Next(3) switch { 0 => 5, 1 => 6, _ => 3 }; break;
                    case 20: enc = 0; break;
                    default: enc = 4; break;
                }
                if (lines.Count == 0) lines.Add("x");
            }
            var right = Encode(string.Join(eol, lines) + tail, enc);
            if (!left.AsSpan().SequenceEqual(right)) yield return (name, other, left, right);
        }

        // Each name's own rules, on every line: the escapes' spacing taken out, and the comments reworded.
        var all = string.Join('\n', Lines) + "\n";
        var marks = "x = 1\n" + string.Join("\nx = 1\n", Comments) + "\n<# a ps comment #>\n--[[ a lua comment ]]\n/* a c comment */\n";
        foreach (var name in names)
        {
            yield return (name, name, Encoding.UTF8.GetBytes(all), Encoding.UTF8.GetBytes(all.Replace("\" + ", "\"+")));
            yield return (name, name, Encoding.UTF8.GetBytes(marks), Encoding.UTF8.GetBytes(marks.Replace("comment", "remark")));
            // One way at a time, so a way read wrong isn't hidden behind another that's read as content anyway.
            foreach (var c in Single)
            {
                var doc = "x = 1\n" + c + "\nx = 1\n";
                yield return (name, name, Encoding.UTF8.GetBytes(doc), Encoding.UTF8.GetBytes(doc.Replace("comment", "remark")));
            }
            foreach (var (l, r) in Edits)
                yield return (name, name, Encoding.UTF8.GetBytes(l), Encoding.UTF8.GetBytes(r));
        }
    }

    private static string Bump(string s)
    {
        int d = s.IndexOfAny("0123456789".ToCharArray());
        return d < 0 ? s + " 9" : s[..d] + (char)('0' + (s[d] - '0' + 1) % 10) + s[(d + 1)..];
    }

    /// <summary>0 UTF-8, 1 UTF-8 with BOM, 2 UTF-16 LE with BOM, 3 Latin-1 (not UTF-8 where not ASCII; C1 bytes too),
    /// 4 UTF-8 with a NUL at the end, 5 UTF-16 BE with BOM, 6 UTF-32 LE with BOM.</summary>
    private static byte[] Encode(string s, int how) => how switch
    {
        1 => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(s)],
        2 => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(s)],
        3 => Encoding.Latin1.GetBytes(s),
        4 => [.. Encoding.UTF8.GetBytes(s), 0],
        5 => [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(s)],
        6 => [0xFF, 0xFE, 0x00, 0x00, .. Encoding.UTF32.GetBytes(s)],
        _ => Encoding.UTF8.GetBytes(s),
    };
}
