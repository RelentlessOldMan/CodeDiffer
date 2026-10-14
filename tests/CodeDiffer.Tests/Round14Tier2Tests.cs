using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Review round 14, tier 2: strings are read as the file's language reads them (a reformat of C beside an escaped quote
/// is whitespace, a change in a C# verbatim string or a PowerShell string is content), the same whole and streamed;
/// names written beside a file never pass 255 characters; exports at once never collide; an overlay a prune took files
/// out of is known to be so.
/// </summary>
public sealed class Round14Tier2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r14b-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public Round14Tier2Tests() => Directory.CreateDirectory(_dir);

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

    private string Classify(string left, string right, string ext, bool streamed)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        Put($"c/{id}.l{ext}", left);
        Put($"c/{id}.r{ext}", right);
        string l = P($"c/{id}.l{ext}"), r = P($"c/{id}.r{ext}");
        var c = streamed ? new ReasonClassifier(0) : new ReasonClassifier();
        return c.Classify(l, new FileInfo(l).Length, r, new FileInfo(r).Length).ToString().ToLowerInvariant();
    }

    // ---- A2: the file's own reading of a backslash ----

    [Theory]
    [InlineData(".c", "if (c == '\\'' || c == '\"') {\n", "if (c=='\\''||c=='\"') {\n", "whitespace")]          // C: escapes
    [InlineData(".txt", "if (c == '\\'' || c == '\"') {\n", "if (c=='\\''||c=='\"') {\n", "content")]           // not known: either reading
    [InlineData(".cs", "var p = @\"C:\\temp\\\"; Log(\"a, b\");\n", "var p = @\"C:\\temp\\\"; Log(\"a,b\");\n", "content")] // verbatim
    [InlineData(".cs", "var p = @\"C:\\temp\\\";  x=1;\n", "var p = @\"C:\\temp\\\"; x = 1;\n", "whitespace")]
    [InlineData(".cs", "var q = @\"say \"\"hi\"\"\";  x=1;\n", "var q = @\"say \"\"hi\"\"\"; x = 1;\n", "whitespace")] // "" in verbatim
    [InlineData(".cs", "var q = @\"a \"\" , b\";\n", "var q = @\"a \"\" ,b\";\n", "content")]
    [InlineData(".cs", "var q = \"a\\\" , b\";\n", "var q = \"a\\\" ,b\";\n", "content")]                         // a regular string escapes
    [InlineData(".cs", "if (c == '\\'' || c == '\"') {\n", "if (c=='\\''||c=='\"') {\n", "whitespace")]
    [InlineData(".ps1", "Join-Path \"C:\\Temp\\\" \"a, b\"\n", "Join-Path \"C:\\Temp\\\" \"a,b\"\n", "content")]
    [InlineData(".ps1", "$s = \"say `\"hi`\"\";  $x=1\n", "$s = \"say `\"hi`\"\"; $x = 1\n", "whitespace")]         // backtick escapes
    [InlineData(".ps1", "$s = \"a`\" , b\"\n", "$s = \"a`\" ,b\"\n", "content")]
    [InlineData(".bat", "set D=\"C:\\x\\\" & echo \"a, b\"\n", "set D=\"C:\\x\\\" & echo \"a,b\"\n", "content")]
    public void StringsAreReadAsTheFilesLanguageReadsThem(string ext, string left, string right, string want)
    {
        Assert.Equal(want, Classify(left, right, ext, streamed: false));
        Assert.Equal(want, Classify(left, right, ext, streamed: true));
        var rules = TextInspector.RulesFor("x" + ext);
        Assert.Equal(want == "whitespace", TextInspector.SameButWhitespace(left, right, rules));
        string[] a = [left.TrimEnd('\n')], b = [right.TrimEnd('\n')];
        Assert.Equal(want == "whitespace", HunkApplier.Problem(a, b, [new Hunk(HunkOp.Replace, 1, 1, 1, 1)], whitespaceOnly: true, rules) is null);
        Assert.Equal(want == "whitespace", LineDiffer.WhitespaceAligned(a, b, rules) is not null);
    }

    [Fact]
    public void BothReadings_InOnePass_AreBothKeysEqual()
    {
        // Both: a space kept where either reading puts it in a string is the same verdict as both readings' keys equal.
        var rng = new Random(14);
        const string alphabet = "ab\"'`\\@$ ,=\n";
        for (int n = 0; n < 4000; n++)
        {
            var l = Random(rng, alphabet, rng.Next(1, 24));
            var r = Respace(rng, l);
            bool both = TextInspector.SameButWhitespace(l, r, StringRules.Both);
            bool each = TextInspector.SameButWhitespace(l, r, StringRules.Escape) && TextInspector.SameButWhitespace(l, r, StringRules.Literal);
            Assert.True(both == each, $"{Show(l)} / {Show(r)}: both {both}, each {each}");
        }
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".c")]
    [InlineData(".cs")]
    [InlineData(".ps1")]
    [InlineData(".bat")]
    public void WholeAndStreamed_ReadStringsAlike(string ext)
    {
        var rng = new Random(ext.GetHashCode(StringComparison.Ordinal) & 0xFFFF);
        const string alphabet = "ab\"'`\\@$ ,=\n";
        var rules = TextInspector.RulesFor("x" + ext);
        for (int n = 0; n < 150; n++)
        {
            var l = Random(rng, alphabet, rng.Next(1, 30)) + "\n";
            var r = Respace(rng, l);
            if (l == r) continue;
            var whole = Classify(l, r, ext, streamed: false);
            var streamed = Classify(l, r, ext, streamed: true);
            Assert.True(whole == streamed, $"{ext} {Show(l)} / {Show(r)}: whole {whole}, streamed {streamed}");
            Assert.Equal(whole == "whitespace", TextInspector.SameButWhitespace(l, r, rules)
                                                && TextInspector.EndsInBlankLine(l) == TextInspector.EndsInBlankLine(r));
        }
    }

    private static string Random(Random rng, string alphabet, int length)
        => new(Enumerable.Range(0, length).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());

    /// <summary>The text with a few spaces put in or taken out.</summary>
    private static string Respace(Random rng, string s)
    {
        var sb = new StringBuilder(s);
        for (int k = rng.Next(1, 4); k > 0; k--)
        {
            int at = rng.Next(sb.Length + 1);
            if (at < sb.Length && sb[at] == ' ' && rng.Next(2) == 0) sb.Remove(at, 1);
            else sb.Insert(Math.Min(at, sb.Length), ' ');
        }
        return sb.ToString();
    }

    private static string Show(string s) => s.Replace("\n", "\\n");

    // ---- C1: names beside a file ----

    [Fact]
    public void ANameBesideALongName_IsStillAName()
    {
        var dir = P("long");
        var path = Path.Combine(dir, new string('n', 250) + ".c");
        var beside = TempNames.Beside(path, ".codediffer-tmp");
        Assert.True(Path.GetFileName(beside).Length <= TempNames.MaxName);
        Assert.Equal(dir, Path.GetDirectoryName(beside));
        Assert.Equal(beside, TempNames.Beside(path, ".codediffer-tmp")); // the same every time: a leftover is found
        Assert.Equal(Path.Combine(dir, "a.c.codediffer-tmp"), TempNames.Beside(Path.Combine(dir, "a.c"), ".codediffer-tmp"));
        Assert.True(Path.GetFileName(TempNames.Unique(path)).Length < 40);

        var astral = new string('x', 238) + "\U0001F600" + "tail";        // a surrogate pair where the name is cut
        var fit = TempNames.Fit(astral, 17);
        Assert.True(fit.Length <= TempNames.MaxName - 17);
        Assert.False(char.IsHighSurrogate(fit[^1]));
    }

    // ---- C2: exports at once ----

    [Fact]
    public void ExportsAtOnce_EachWriteTheWholePatch()
    {
        for (int i = 0; i < 40; i++)
        {
            Put($"L/f{i:D2}.c", $"int f{i}() {{ return {i}; }}\n");
            Put($"R/f{i:D2}.c", $"int f{i}() {{ return {i + 1}; }}\n");
        }
        var s = new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Null(s.Error);
        var file = P("out/all.patch");
        var answers = new string[12];
        Parallel.For(0, answers.Length, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i => answers[i] = AgentViews.Export(s, file));
        Assert.All(answers, a => Assert.DoesNotContain("error", a));
        Assert.Equal(40, File.ReadAllText(file).Split('\n').Count(l => l.StartsWith("diff --git ", StringComparison.Ordinal)));
        Assert.Equal(["all.patch"], Directory.GetFiles(P("out")).Select(Path.GetFileName).ToArray()); // no temp left
    }

    // ---- C3: an overlay a prune raced ----

    [Fact]
    public void AnOverlayMissingFiles_IsNotIntact()
    {
        foreach (var d in new[] { "B", "V1", "V2" })
            for (int i = 0; i < 3; i++) Put($"{d}/f{i}.c", "x\n");
        for (int i = 0; i < 3; i++) Put($"V2/f{i}.c", $"x from v2 {i}\n");
        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache);
        var o = MergeOverlay.Write(r, P("B"), P("V1"), P("V2"), P("overlay"));
        Assert.Equal(3, o.FromV2);
        Assert.True(MergeOverlay.Intact(o));

        File.Delete(Directory.EnumerateFiles(Path.Combine(o.Dir, "files"), "*", SearchOption.AllDirectories).First());
        Assert.False(MergeOverlay.Intact(o));                            // a file taken away
        Directory.Delete(o.Dir, recursive: true);
        Assert.False(MergeOverlay.Intact(o));                            // all of it
    }
}
