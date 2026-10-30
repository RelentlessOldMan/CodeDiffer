using System.Text;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.ThreeWay;
using CodeDiffer.Core.Verify;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Code review pass 14, tier 1: verify passes my own 3-way decomposition wherever it is right (the merge's lines among
/// identical lines read as the merge reads them, and an insert both sides made told from one side's), and reads the
/// trees as the engine does (see also <see cref="DeltaTreeCrossCheckTests"/> and <see cref="InPlaceRunTests"/>).
/// </summary>
public sealed class Round14Tier1Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r14a-" + Guid.NewGuid().ToString("N"));

    public Round14Tier1Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Put(string side, string text)
    {
        var d = Path.Combine(_dir, side);
        Directory.CreateDirectory(d);
        File.WriteAllBytes(Path.Combine(d, "f.txt"), new UTF8Encoding(false).GetBytes(text));
        return d;
    }

    private static string Lines(string words) => string.Concat(words.Split('|').Select(w => w + "\n"));

    // Cases from a fuzz of verify against my own merges: each failed gate 2, as an edit in place written apart (the
    // side's hunks are a region's span, wider than its own diff, and pairing them in place moved a line across) or as
    // a decomposition that didn't rebuild v2 (an inserted line both sides hold read as v1's alone).
    [Theory]
    [InlineData("acc|acc|acc|acc", "acc3|acc|acc|acc", "u401|acc|acc|acc3")]
    [InlineData("x|x|x|x|x|x|x|x|u7735", "x|x|x|x|x|x|u7735|acc", "x|x|u7736|x|x2|x|x|x|u7735|acc")]
    [InlineData("u8325|u8326|x|x|x|x|x||||", "u8325|u8326|x|x|x|x|x|x1||", "u8325|u8326|x|x|x|x|x|x1||||")]
    [InlineData("acc|acc|acc|acc|acc|acc|x|x|acc|acc|acc|acc|acc|acc|acc|acc",
        "acc|acc|acc|acc|acc|acc|x|x|acc|acc|acc|acc|acc|acc|acc|acc|acc",
        "acc|u12256|acc|acc|acc|acc|x|x|u12257|acc|acc|acc|acc|acc|acc|acc|acc|acc")]
    public void MyOwnDecomposition_PassesVerify(string b, string v1, string v2)
    {
        string bt = Lines(b), t1 = Lines(v1), t2 = Lines(v2);
        var mine = ThreeWayMerger.Merge("f.txt", bt, t1, t2);
        Assert.True(mine.Conflicts.Count + mine.CleanMerges.Count > 0);
        var res = ConflictTreeCrossCheck.Run(Put("B", bt), Put("V1", t1), Put("V2", t2),
            new ConflictManifest(1, "V1", "V2", mine.Conflicts, mine.CleanMerges, ConflictTruthSha: null));
        Assert.True(res.Reconstructs);
        Assert.True(res.Ok, string.Join("; ", res.UnsoundRegions));
    }

    // ---- CodeSpawner 1.1.12 rename fixtures: a move less similar than the rename threshold ----

    [Fact]
    public void ARenameBelowTheThreshold_IsExpectedRemovedAndAdded()
    {
        // The contract grades renames down to ~0.3 and sets no threshold; the compare's is 50%, so a 30%-similar move is
        // a removal and an addition to it, and verify expects that (a rename at or above it must still be one).
        string Ten(string tag, int keep) => string.Concat(Enumerable.Range(0, 10).Select(i => i < keep ? $"line {i}\n" : $"{tag} {i}\n"));
        Put("B", ""); Put("V", "");
        File.WriteAllText(Path.Combine(_dir, "B", "a.txt"), Ten("a", 10));
        File.WriteAllText(Path.Combine(_dir, "V", "a2.txt"), Ten("b", 3));
        File.WriteAllText(Path.Combine(_dir, "B", "c.txt"), Ten("c", 10).Replace("line", "keep"));
        File.WriteAllText(Path.Combine(_dir, "V", "c2.txt"), Ten("d", 8).Replace("line", "keep"));
        var report = new CodeDiffer.Core.Compare.DirectoryComparer(new() { Cache = CodeDiffer.Core.Ledger.CacheMode.Off })
            .Compare(Path.Combine(_dir, "B"), Path.Combine(_dir, "V"));
        var manifest = new DeltaManifest(1, [], [], [new RenameOp("a.txt", "a2.txt", 300), new RenameOp("c.txt", "c2.txt", 800)], [], null);

        var fo = DeltaFileOpsCheck.Run(report, manifest);
        Assert.True(fo.Ok, string.Join("; ", fo.Missing.Concat(fo.Extra).Concat(fo.WrongSimilarity)));
        Assert.Equal(1, fo.BelowThresholdCount);

        var strict = DeltaFileOpsCheck.Run(report, manifest, renameThresholdMilli: 200); // a compare that pairs at 30%
        Assert.False(strict.Ok);
        Assert.Contains(strict.Missing, m => m == "renamed a.txt -> a2.txt");
    }

    [Fact]
    public void SimilarityRoundsHalfUp_AsTheContractPinsIt()
    {
        // 9 of 16 lines in common is 562.5: half up (integers), not half to even.
        var same = Enumerable.Range(0, 9).Select(i => $"s{i}").ToList();
        var a = same.Concat(Enumerable.Range(0, 7).Select(i => $"a{i}")).ToList();
        var b = same.Concat(Enumerable.Range(0, 7).Select(i => $"b{i}")).ToList();
        Assert.Equal(563, Similarity.MilliFromLines(a, b));
    }
}
