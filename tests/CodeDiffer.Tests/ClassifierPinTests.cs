using System.Security.Cryptography;
using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The verdict store keeps a pair's reason and noise across releases, keyed by <see cref="VerdictStore.ClassifierVersion"/>,
/// not the build. So the classifier's answers are pinned here, over a few hundred generated pairs (every reason and noise
/// kind, read whole and streamed, size-changed too): a change to any of them fails this until the version is bumped, so
/// an older store's verdicts are never trusted by a classifier that would answer differently.
/// </summary>
public sealed class ClassifierPinTests
{
    /// <summary>Each classifier version's digest of the answers. Answers changed: add the next version with the new
    /// digest (and bump <see cref="VerdictStore.ClassifierVersion"/> to it); never edit an earlier line.</summary>
    private static readonly (int Version, string Digest)[] Pins =
    [
        (1, "661F57410CA0ED0B"),
    ];

    [Fact]
    public void TheClassifiersAnswers_AreThoseOfItsVersion()
    {
        using var t = new TempDir();
        var whole = new ReasonClassifier();
        var streamed = new ReasonClassifier(maxClassifyBytes: 64); // the Large path, on small files
        var log = new StringBuilder();
        var reasons = new HashSet<ChangeReason>();
        var noise = ChangeNoise.None;
        int n = 0;
        foreach (var (ext, left, right) in Pairs())
        {
            var a = t.PutBytes($"L/{n}/f{ext}", left);
            var b = t.PutBytes($"R/{n}/f{ext}", right);
            foreach (var c in (ReasonClassifier[])[whole, streamed])
            {
                var r = c.Classify(a, left.Length, b, right.Length, out var z);
                log.Append($"{n} {ext} {r} {z}");
                reasons.Add(r);
                noise |= z;
                if (left.Length != right.Length)
                    log.Append($" | {c.ClassifyResized(a, left.Length, b, right.Length, out _, out var zr)?.ToString() ?? "same"} {zr}");
                log.Append('\n');
            }
            n++;
        }

        // The pairs reach every reason a modified pair can get, and every kind of noise.
        Assert.Superset(new HashSet<ChangeReason> { ChangeReason.Content, ChangeReason.Eol, ChangeReason.Whitespace, ChangeReason.Encoding, ChangeReason.Binary }, reasons);
        Assert.Equal(ChangeNoise.Stamps | ChangeNoise.Numbers | ChangeNoise.Comments, noise);

        Assert.Equal(Pins.Length, Pins.Select(p => p.Version).Distinct().Count());
        Assert.Equal(Pins.Length, Pins.Select(p => p.Digest).Distinct().Count());
        Assert.Equal(VerdictStore.ClassifierVersion, Pins[^1].Version);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(log.ToString())))[..16];
        Assert.True(digest == Pins[^1].Digest,
            $"The classifier's answers changed (digest {digest}): bump VerdictStore.ClassifierVersion and add it to Pins with this digest.");
    }

    private static readonly string[] Exts =
        [".c", ".cs", ".py", ".sh", ".sql", ".xml", ".html", ".ini", ".yaml", ".lua", ".bat", ".txt", ".md", ".csv", ".json", ".log", "", ".dat"];

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
        "version=1.2.3 build 456 on 2026-03-04 12:00:01",
        "id: A1B2C3, count 7",
        "\tindented\twith\ttabs",
        "  two  inner   spaces  ",
        "temperature 25°C, café, naïve",
        "#define LONG_MACRO(a) \\",
        "    ((a) * 2)",
        "\"\"\"docstring line\"\"\"",
        "key = value ; trailing comment",
        "",
        "end",
    ];

    /// <summary>A fixed set of pairs: a base document per type, and the same with one to three edits of every kind.</summary>
    private static IEnumerable<(string Ext, byte[] Left, byte[] Right)> Pairs()
    {
        uint seed = 12345;
        int Next(int max) { seed = seed * 1664525 + 1013904223; return (int)((seed >> 8) % (uint)max); }
        for (int i = 0; i < 360; i++)
        {
            var ext = Exts[i % Exts.Length];
            var doc = new List<string>();
            for (int k = 0, len = 6 + Next(14); k < len; k++) doc.Add(Lines[Next(Lines.Length)]);
            var left = Encode(string.Join('\n', doc) + "\n", Next(8) == 0 ? 3 : 0);
            var lines = new List<string>(doc);
            string eol = "\n", tail = "\n";
            int enc = 0;
            for (int m = 0, edits = 1 + Next(3); m < edits; m++)
            {
                int at = Next(lines.Count);
                switch (Next(17))
                {
                    case 0: eol = "\r\n"; tail = "\r\n"; break;
                    case 1: lines[at] += "\r"; break;
                    case 2: lines[at] += "  "; break;
                    case 3: lines[at] = lines[at].StartsWith('\t') ? "    " + lines[at][1..] : "\t" + lines[at].TrimStart(); break;
                    case 4: lines[at] = lines[at].Replace(" ", "  "); break;
                    case 5: lines[at] = lines[at].Replace("comment", "remark"); break;
                    case 6: lines[at] = Bump(lines[at]); break;
                    case 7: lines[at] = lines[at].Replace("1234", "1299").Replace("17", "18"); break;
                    case 8: lines[at] = lines[at].Replace('e', 'E'); break;
                    case 9: lines.Insert(at, ""); break;
                    case 10: lines.Insert(at, "added line " + at); break;
                    case 11: lines.RemoveAt(at); break;
                    case 12: lines.Insert(at, ext is ".py" or ".sh" or ".ini" or ".yaml" ? "# new comment" : "// new comment"); break;
                    case 13: tail = ""; break;
                    case 14: enc = 1; break;
                    case 15: enc = 2; break;
                    default: enc = 4; break;
                }
                if (lines.Count == 0) lines.Add("x");
            }
            var right = Encode(string.Join(eol, lines) + tail, enc);
            if (!left.AsSpan().SequenceEqual(right)) yield return (ext, left, right);
        }
    }

    private static string Bump(string s)
    {
        int d = s.IndexOfAny("0123456789".ToCharArray());
        return d < 0 ? s + " 9" : s[..d] + (char)('0' + (s[d] - '0' + 1) % 10) + s[(d + 1)..];
    }

    /// <summary>0 UTF-8, 1 UTF-8 with BOM, 2 UTF-16 LE with BOM, 3 Latin-1 (not UTF-8 where not ASCII), 4 UTF-8 with a NUL.</summary>
    private static byte[] Encode(string s, int how) => how switch
    {
        1 => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(s)],
        2 => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(s)],
        3 => Encoding.Latin1.GetBytes(s),
        4 => [.. Encoding.UTF8.GetBytes(s), 0],
        _ => Encoding.UTF8.GetBytes(s),
    };
}
