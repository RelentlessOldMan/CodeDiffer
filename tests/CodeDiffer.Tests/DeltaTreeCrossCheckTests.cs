using System.Text;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Verify;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The tree cross-check: build a base tree + a variant tree with a CodeSpawner-style content edit
/// (marker appended to odd lines), then assert CodeDiffer's own differ reproduces the manifest's hunks —
/// and that a tampered manifest hunk is caught.
/// </summary>
public sealed class DeltaTreeCrossCheckTests : IDisposable
{
    private readonly string _base = NewTempDir();
    private readonly string _variant = NewTempDir();

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"codediffer-xc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); } catch { }
        try { Directory.Delete(_variant, true); } catch { }
    }

    private static void Write(string root, string rel, string text)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    // A content edit on odd (1-based) lines → five 1-line replaces, matching CodeSpawner's Coalesce.
    private FileDelta BuildContentFile(string rel, out string baseText, out string variantText)
    {
        var baseLines = Enumerable.Range(1, 10).Select(i => $"line {i}").ToArray();
        var varLines = (string[])baseLines.Clone();
        for (int i = 0; i < varLines.Length; i += 2)
            varLines[i] += " /*mut*/";
        baseText = string.Join('\n', baseLines) + "\n";
        variantText = string.Join('\n', varLines) + "\n";

        var hunks = new[] { 1, 3, 5, 7, 9 }.Select(l => new Hunk(HunkOp.Replace, l, 1, l, 1)).ToList();
        return new FileDelta(rel, ChangeReason.Content, "ob", "nb", 80, 90, hunks, []);
    }

    [Fact]
    public void MatchingManifest_CrossCheckPasses()
    {
        var f = BuildContentFile("src/a.c", out var b, out var v);
        Write(_base, "src/a.c", b);
        Write(_variant, "src/a.c", v);

        var manifest = new DeltaManifest(1, [], [], [], [f], DiffTruthSha: null);
        var result = DeltaTreeCrossCheck.Run(_base, _variant, manifest);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Reconstructed);
        Assert.Equal(1, result.ExactMatches); // distinct lines => unambiguous, so exact match too
    }

    [Fact]
    public void TamperedHunk_CrossCheckFails()
    {
        var good = BuildContentFile("a.c", out var b, out var v);
        Write(_base, "a.c", b);
        Write(_variant, "a.c", v);

        // Claim a wrong hunk set (say only one line changed) — differ will disagree.
        var tampered = good with { Hunks = [new Hunk(HunkOp.Replace, 1, 1, 1, 1)] };
        var manifest = new DeltaManifest(1, [], [], [], [tampered], DiffTruthSha: null);

        var result = DeltaTreeCrossCheck.Run(_base, _variant, manifest);
        Assert.False(result.Ok); // a wrong hunk set cannot rebuild the variant
        Assert.Equal(0, result.Reconstructed);
    }

    [Fact]
    public void FileOps_CodeDiffersOwnCompare_IsCheckedAgainstTheManifest()
    {
        var f = BuildContentFile("src/a.c", out var b, out var v);
        Write(_base, "src/a.c", b);
        Write(_variant, "src/a.c", v);
        Write(_base, "eol.txt", "a\nb\n");
        Write(_variant, "eol.txt", "a\r\nb\r\n");
        Write(_base, "gone.c", "bye\n");
        Write(_variant, "new.c", "hi\n");
        var moved = string.Concat(Enumerable.Range(1, 20).Select(i => $"moved {i}\n"));
        Write(_base, "old/m.c", moved);
        Write(_variant, "new/m.c", moved);
        var report = new CodeDiffer.Core.Compare.DirectoryComparer(new CodeDiffer.Core.Compare.CompareOptions { Cache = CodeDiffer.Core.Ledger.CacheMode.Off })
            .Compare(_base, _variant);
        var eol = new FileDelta("eol.txt", ChangeReason.Eol, "o", "n", 4, 6, [], []);
        var faithful = new DeltaManifest(1, ["new.c"], ["gone.c"], [new RenameOp("old/m.c", "new/m.c", 1000)], [f, eol], DiffTruthSha: null);

        var ok = DeltaFileOpsCheck.Run(report, faithful);
        Assert.True(ok.Ok);
        Assert.Equal(5, ok.Matched);

        // A manifest CodeDiffer disagrees with fails, naming each difference.
        var wrong = faithful with
        {
            Added = ["new.c", "phantom.c"],
            Modified = [f, eol with { Reason = ChangeReason.Content }],
            Renamed = [new RenameOp("old/m.c", "new/m.c", 900)],
        };
        var bad = DeltaFileOpsCheck.Run(report, wrong);
        Assert.False(bad.Ok);
        Assert.Equal((1, 0, 1, 1), (bad.MissingCount, bad.ExtraCount, bad.WrongReasonCount, bad.WrongSimilarityCount));
        Assert.Equal("added phantom.c", Assert.Single(bad.Missing));
        Assert.Contains("manifest content, CodeDiffer eol", Assert.Single(bad.WrongReason));
    }

    [Fact]
    public void NonContentReasons_AreSkipped_NotSilentlyPassed()
    {
        Write(_base, "x.bin", "whatever");
        Write(_variant, "x.bin", "whatever2");
        var binary = new FileDelta("x.bin", ChangeReason.Binary, "o", "n", 8, 9, [], []);
        var manifest = new DeltaManifest(1, [], [], [], [binary], DiffTruthSha: null);

        var result = DeltaTreeCrossCheck.Run(_base, _variant, manifest);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Checked);
        Assert.True(result.Ok); // nothing checked, nothing mismatched
    }
}
