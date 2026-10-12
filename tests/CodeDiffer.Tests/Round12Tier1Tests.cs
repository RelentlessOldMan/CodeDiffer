using System.Runtime.InteropServices;
using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.ThreeWay;
using CodeDiffer.Core.Verify;
using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Review round 12, tier 1: a file identity tells volumes apart; a quote after an escaped backslash ends its string; a
/// 3-way manifest's widened, split or loose regions fail and a top-of-file insert passes; a hunkless reason carrying
/// hunks fails, and so does a replace keeping a line it replaces; an overlay deletes the file named, not a lookalike; a
/// corrupt CodeCompass manifest is no cache.
/// </summary>
public sealed class Round12Tier1Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r12a-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public Round12Tier1Tests() => Directory.CreateDirectory(_dir);

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

    private static string Lines(int n, Func<int, string?>? edit = null)
        => string.Concat(Enumerable.Range(1, n).Select(i => (edit?.Invoke(i) ?? $"line {i}") + "\n"));

    private ConflictCrossCheckResult Check(IReadOnlyList<Conflict> conflicts, IReadOnlyList<CleanMerge> clean)
        => ConflictTreeCrossCheck.Run(P("B"), P("V1"), P("V2"), new ConflictManifest(1, "V1", "V2", conflicts, clean, ConflictTruthSha: null));

    // ---- C1: file identity ----

    [Fact]
    public void TheFileInformationStruct_IsTheNativeSize()
        => Assert.Equal(52, Marshal.SizeOf<RealPath.ByHandleFileInformation>());

    [Fact]
    public void TwoVolumeRoots_AreNotOneDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        var roots = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
        if (roots.Count < 2) return; // one fixed drive: nothing to tell apart
        Assert.NotEqual(RealPath.Identity(roots[0]), RealPath.Identity(roots[1]));
        Assert.False(RealPath.InsideByIdentity(Path.Combine(roots[1], "cd-nothing-here"), roots[0]));
        Assert.True(RealPath.InsideByIdentity(P("x"), _dir));
    }

    // ---- A1: a quote after an escaped backslash ----

    [Theory]
    [InlineData("var s = dir + \"\\\\\" + name + \", \" + ext;", "var s = dir + \"\\\\\" + name + \",\" + ext;", ChangeReason.Content)]
    [InlineData("var s = \"\\\\\\\"\" + \", \";", "var s = \"\\\\\\\"\" + \",\";", ChangeReason.Content)] // \\\" : the quote escaped
    [InlineData("var s = dir + \"\\\\\" + name;", "var s = dir+\"\\\\\"+name;", ChangeReason.Whitespace)]
    public void AQuoteAfterAnEscapedBackslash_EndsItsString_WholeOrStreamed(string left, string right, ChangeReason want)
    {
        Put("w/l.c", "int a;\n" + left + "\n");
        Put("w/r.c", "int a;\n" + right + "\n");
        long ll = new FileInfo(P("w/l.c")).Length, rl = new FileInfo(P("w/r.c")).Length;
        Assert.Equal(want, new ReasonClassifier().Classify(P("w/l.c"), ll, P("w/r.c"), rl));
        Assert.Equal(want, new ReasonClassifier(maxClassifyBytes: 1).Classify(P("w/l.c"), ll, P("w/r.c"), rl));
    }

    // ---- B1 / B4 / B5: the 3-way fallback ----

    private void Trees(string b, string v1, string v2)
    {
        Put("B/f.txt", b);
        Put("V1/f.txt", v1);
        Put("V2/f.txt", v2);
    }

    [Fact]
    public void AConflictWidenedOverALineNobodyChanged_Fails()
    {
        Trees(Lines(10), Lines(10, i => i == 2 ? "v1" : null), Lines(10, i => i == 2 ? "v2" : null));
        Assert.True(Check([new Conflict("f.txt", 2, 1, HunkOp.Replace, 2, 1, HunkOp.Replace, 2, 1)], []).Ok);
        var r = Check([new Conflict("f.txt", 2, 2, HunkOp.Replace, 2, 2, HunkOp.Replace, 2, 2)], []);
        Assert.False(r.Ok);
        Assert.Contains(r.UnsoundRegions, u => u.Contains("f.txt:2,2") && u.Contains("wider than the change"));
    }

    [Fact]
    public void ACleanRegionWidenedOrSplit_Fails()
    {
        Trees(Lines(10), Lines(10, i => i is 2 or 3 ? $"v1 {i}" : null), Lines(10));
        Assert.True(Check([], [new CleanMerge("f.txt", "v1", HunkOp.Replace, 2, 2, 2, 2)]).Ok);
        var wide = Check([], [new CleanMerge("f.txt", "v1", HunkOp.Replace, 2, 4, 2, 4)]);
        Assert.False(wide.Ok);
        Assert.Contains(wide.UnsoundRegions, u => u.Contains("wider than the change"));
        var split = Check([], [new CleanMerge("f.txt", "v1", HunkOp.Replace, 2, 1, 2, 1), new CleanMerge("f.txt", "v1", HunkOp.Replace, 3, 1, 3, 1)]);
        Assert.False(split.Ok);
        Assert.Contains(split.UnsoundRegions, u => u.Contains("one change is one region"));
    }

    [Fact]
    public void AConflictAmongRepeatedLines_PassesWhicheverWayEachSideIsAligned()
    {
        // b=[A,B] v1=[B,A] v2=[A]: a minimal diff of v1 may keep A or B; diff3 conflicts with one choice.
        Trees("A\nB\n", "B\nA\n", "A\n");
        var mine = ThreeWayMerger.Merge("f.txt", "A\nB\n", "B\nA\n", "A\n");
        Assert.NotEmpty(mine.Conflicts);
        var r = Check(mine.Conflicts, mine.CleanMerges);
        Assert.Empty(r.UnsoundRegions);
    }

    [Fact]
    public void AnInsertAtTheTopOfAFile_Reconstructs()
    {
        Trees(Lines(5), "top\n" + Lines(5), Lines(5, i => i == 4 ? "v2" : null));
        var r = Check([], [new CleanMerge("f.txt", "v1", HunkOp.Insert, 0, 0, 1, 1), new CleanMerge("f.txt", "v2", HunkOp.Replace, 4, 1, 4, 1)]);
        Assert.True(r.Reconstructs, string.Join("; ", r.Files));
        Assert.True(r.Ok, string.Join("; ", r.UnsoundRegions));
    }

    // ---- B2 / B3: 2-way canonical hunks ----

    [Fact]
    public void AnEolRecordCarryingHunks_Fails()
    {
        Put("b/f.c", "a\nb\n");
        Put("v/f.c", "a\r\nb\r\n");
        var f = new FileDelta("f.c", ChangeReason.Eol, "", "", 0, 0, [new Hunk(HunkOp.Replace, 1, 1, 1, 1)], []);
        var r = DeltaTreeCrossCheck.Run(P("b"), P("v"), new DeltaManifest(1, [], [], [], [f], DiffTruthSha: null));
        var c = r.Files.Single();
        Assert.True(c.Checked);
        Assert.False(c.Reconstructs);
        Assert.Contains("the contract gives it none", c.Problem);
    }

    [Fact]
    public void AReplaceKeepingALineItReplaces_IsNotCanonical()
    {
        string[] old = ["a", "b", "c", "d"];
        string[] @new = ["a", "X", "b", "c", "Y", "d"]; // two inserts
        Assert.Null(HunkApplier.Problem(old, @new, [new Hunk(HunkOp.Insert, 1, 0, 2, 1), new Hunk(HunkOp.Insert, 3, 0, 5, 1)]));
        Assert.Contains("keeps a line it replaces", HunkApplier.Problem(old, @new, [new Hunk(HunkOp.Replace, 2, 2, 2, 4)]));
    }

    [Fact]
    public void ProblemNeverRefusesLineDiffersOwnHunks()
    {
        var rnd = new Random(12);
        for (int n = 0; n < 3000; n++)
        {
            var a = Enumerable.Range(0, rnd.Next(0, 14)).Select(_ => "abc"[rnd.Next(3)].ToString()).ToArray();
            var b = Enumerable.Range(0, rnd.Next(0, 14)).Select(_ => "abcd"[rnd.Next(4)].ToString()).ToArray();
            var hunks = LineDiffer.Diff(a, b);
            Assert.True(HunkApplier.Problem(a, b, hunks) is null, $"{string.Join("", a)} -> {string.Join("", b)}: {HunkApplier.Problem(a, b, hunks)}");
        }
    }

    // ---- C2: an overlay delete of a name UTF-8 can't hold ----

    [Fact]
    public void AnOverlayDeletesTheFileNamed_NotOneThatShowsAlike()
    {
        if (!OperatingSystem.IsWindows()) return; // an unpaired surrogate in a file name is an NTFS thing
        string lone = "c\uD801.txt", lookalike = "c�.txt";
        foreach (var t in new[] { "B", "V1", "V2" }) Put($"{t}/keep.txt", "k\n");
        Put("B/" + lone, "x\n");
        Put("V1/" + lone, "x\n");
        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache); // v2 deleted it
        var o = MergeOverlay.Write(r, P("B"), P("V1"), P("V2"), P("ov"));
        Assert.Equal(1, o.Deletes);
        Assert.Contains("\\uD801", File.ReadAllText(P("ov/deletes.txt")));

        Directory.CreateDirectory(P("T"));
        File.Copy(P("V1/keep.txt"), P("T/keep.txt"));
        File.Copy(P("V1/" + lone), P("T/" + lone));
        File.WriteAllText(P("T/" + lookalike), "bystander\n");
        var applied = OverlayApplier.Run(P("ov"), P("T"), write: true);
        Assert.Empty(applied.Failed);
        Assert.False(File.Exists(P("T/" + lone)));
        Assert.True(File.Exists(P("T/" + lookalike)));
    }

    // ---- A2: a corrupt CodeCompass manifest ----

    [Fact]
    public void AZeroFilledCodeCompassManifest_IsNoCache()
    {
        Put("tree/a.txt", "a\n");
        Put("tree2/a.txt", "b\n");
        var cc = P("cc");
        var ledger = Path.Combine(cc, LedgerFormat.RootKey(Path.GetFullPath(P("tree"))));
        Directory.CreateDirectory(ledger);
        File.WriteAllBytes(Path.Combine(ledger, "snapshot.manifest"), new byte[64]); // a torn write
        Assert.Equal(0, LedgerFormat.PeekVersion(ledger));
        var cache = HashCache.Open(P("tree"), CacheMode.On, cacheBaseDir: P("own"), codeCompassBaseDir: cc);
        Assert.NotNull(cache);
    }
}
