using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Port;
using CodeDiffer.Core.ThreeWay;
using CodeDiffer.Core.Verify;
using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Review round 9, tier 1: a whole patch never deletes or writes through a link, nor carries an incomplete compare,
/// and leaves case-only path changes to do by hand; device names are skipped; a BOM alone is empty text; verify reads
/// a rename with edits from its old path and checks a side with no hunks; the port carries the change's re-encoding;
/// compare3 flags a file where the other side has a directory.
/// </summary>
public sealed class Round9Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r9-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };
    private static readonly string Ten = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));

    public Round9Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Through \\?\: a "nul" file is deleted as a file, not as the device.
        try { Directory.Delete(OperatingSystem.IsWindows() ? @"\\?\" + _dir : _dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string rel, string text) => PutBytes(rel, new UTF8Encoding(false).GetBytes(text));

    private void PutBytes(string rel, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), bytes);
    }

    private string Patch(CompareReport r)
    {
        var w = new StringWriter { NewLine = "\n" };
        PatchWriter.Write(w, r, P("L"), P("R"));
        return w.ToString();
    }

    // ---- patches ----

    [Fact]
    public void AnIncompleteCompare_IsNeverWrittenAsAPatch()
    {
        var incomplete = new CompareReport([new FileChange("sub/keep.txt", ChangeStatus.Removed, null, 3, 0)], 0, 1);
        var ex = Assert.Throws<ArgumentException>(() => PatchWriter.Write(new StringWriter(), incomplete, P("L"), P("R")));
        Assert.Contains("incomplete", ex.Message);
    }

    [Fact]
    public void AFileBehindALink_IsNeverDeletedOrAddedByAPatch()
    {
        Put("L/d/f.txt", "old\n");
        Put("R/n/new.txt", "new\n");
        var r = new CompareReport([
            new FileChange("d/f.txt", ChangeStatus.Removed, null, 4, 0, Unreadable: "behind a link: the right tree has a junction at d", BehindLink: true),
            new FileChange("n/new.txt", ChangeStatus.Added, null, 0, 4, Unreadable: "behind a link: the left tree has a junction at n", BehindLink: true)]);
        var patch = Patch(r);
        Assert.DoesNotContain("diff --git", patch);
        Assert.Contains("# d/f.txt: behind a link", patch);
        Assert.Contains("# n/new.txt: behind a link", patch);
    }

    [Fact]
    public void ACaseOnlyPathChange_IsLeftToDoByHand_AndTheRestStillApplies()
    {
        Put("L/Foo.c", Ten);
        Put("R/foo.c", Ten);
        Put("L/Bar.c", Ten.Replace("line", "bar"));
        Put("R/bar.c", Ten.Replace("line", "bar").Replace("bar 3", "bar three"));
        Put("L/other.txt", "a\n");
        Put("R/other.txt", "b\n");
        var r = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        var w = new StringWriter { NewLine = "\n" };
        var stats = PatchWriter.Write(w, r, P("L"), P("R"));
        var patch = w.ToString();
        Assert.DoesNotContain("rename from", patch);
        Assert.Contains("# Foo.c -> foo.c: another path in this patch differs from it only in case", patch);
        Assert.Contains("# Bar.c -> bar.c:", patch);
        Assert.Contains("diff --git a/other.txt b/other.txt", patch);
        Assert.Equal(2, stats.CaseFiles);
        Assert.Contains("2 case-only path change(s)", stats.Summary(false));
    }

    // ---- the engine ----

    [Fact]
    public void ADeviceName_IsSkippedAndSaid_NotACrash()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(TreeWalker.UsableName("nul"));
        Assert.False(TreeWalker.UsableName("NUL"));
        Assert.True(TreeWalker.UsableName("null.c"));
        Assert.True(TreeWalker.UsableName("a.c"));
        Put("L/keep.c", "a\n");
        Put("R/keep.c", "b\n");
        Directory.CreateDirectory(P("R"));
        File.WriteAllText(@"\\?\" + P("L/nul"), "left\n");
        File.WriteAllText(@"\\?\" + P("R/nul"), "right, longer\n");
        var r = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        Assert.Equal(2, r.SkippedNames);
        Assert.Equal("keep.c", Assert.Single(r.Changes, c => c.Status != ChangeStatus.Identical).RelativePath);
    }

    [Fact]
    public void AFileThatIsOnlyABom_IsEmptyText_AndNeverPairsAsARename()
    {
        byte[] utf8 = [0xEF, 0xBB, 0xBF], utf16 = [0xFF, 0xFE];
        PutBytes("L/a/Empty.cs", utf8);
        PutBytes("R/b/Unrelated.cs", utf8);
        PutBytes("L/c/U8.cs", utf8);
        PutBytes("R/d/U16.cs", utf16);
        var r = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        Assert.Equal(0, r.Count(ChangeStatus.Renamed));
        Assert.Equal(2, r.Count(ChangeStatus.Removed));
        Assert.Equal(2, r.Count(ChangeStatus.Added));
    }

    // ---- verify ----

    [Fact]
    public void Verify_ARenameWithEdits_IsCheckedFromItsOldPath()
    {
        Put("B/a.c", Ten);
        Put("V/b.c", Ten.Replace("line 3", "line three"));
        var manifest = new DeltaManifest(1, [], [], [new RenameOp("a.c", "b.c", 900)],
            [new FileDelta("b.c", ChangeReason.Content, "o", "n", 81, 85, [new Hunk(HunkOp.Replace, 3, 1, 3, 1)], [])], null);

        var tree = DeltaTreeCrossCheck.Run(P("B"), P("V"), manifest);
        Assert.True(tree.Ok);
        Assert.Equal(1, tree.Reconstructed);

        var report = new DirectoryComparer(NoCache).Compare(P("B"), P("V"));
        var ops = DeltaFileOpsCheck.Run(report, manifest);
        Assert.True(ops.Ok, string.Join("; ", ops.Missing.Concat(ops.Extra).Concat(ops.WrongSimilarity)));
    }

    [Fact]
    public void Verify3_AManifestLeavingOutOneSidesChanges_Fails()
    {
        Put("B/x.c", Ten);
        Put("V1/x.c", Ten.Replace("line 2", "line two"));
        Put("V2/x.c", Ten.Replace("line 8", "line eight"));
        var rel = "x.c";
        var m = ThreeWayMerger.Merge(rel, File.ReadAllText(P("B/x.c")), File.ReadAllText(P("V1/x.c")), File.ReadAllText(P("V2/x.c")));
        var full = new ConflictManifest(1, "base_v1", "base_v2", m.Conflicts, m.CleanMerges, ConflictTruthSha: null);
        Assert.True(ConflictTreeCrossCheck.Run(P("B"), P("V1"), P("V2"), full).Ok);

        var omitted = full with { CleanMerges = m.CleanMerges.Where(c => c.Side == "v2").ToList() };
        var cc = ConflictTreeCrossCheck.Run(P("B"), P("V1"), P("V2"), omitted);
        Assert.False(cc.Ok);
        Assert.False(cc.Files.Single().V1Reconstructs);
    }

    // ---- port ----

    [Theory]
    [InlineData(true)]   // left has a UTF-8 BOM, right drops it
    [InlineData(false)]  // left is UTF-16LE, right is UTF-8
    public void ThePort_CarriesTheChangesReEncoding_WithItsEdit(bool bom)
    {
        var leftBytes = bom ? [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Ten)] : (byte[])[0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Ten)];
        PutBytes("L/f.txt", leftBytes);
        Put("R/f.txt", Ten.Replace("line 5", "line five"));
        PutBytes("T/f.txt", leftBytes);
        var report = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        var f = ChangePorter.Run(report, P("L"), P("R"), P("T"), write: true).Files.Single();
        Assert.Equal(PortStatus.Clean, f.Status);
        Assert.Contains("re-encoded", f.Note);
        Assert.Equal(File.ReadAllBytes(P("R/f.txt")), File.ReadAllBytes(P("T/f.txt")));
    }

    // ---- compare3 ----

    [Fact]
    public void Compare3_AFileWhereTheOtherSideHasADirectory_IsAConflict()
    {
        // v2 turns directory D into a file D; v1 adds D/z.txt.
        Put("B/D/x.txt", "x\n");
        Put("V1/D/x.txt", "x\n");
        Put("V1/D/z.txt", "z\n");
        Put("V2/D", "now a file\n");
        // v2 turns file P into a directory P/a; v1 edits P.
        Put("B/P", Ten);
        Put("V1/P", Ten.Replace("line 1", "line one"));
        Put("V2/P/a", "a\n");
        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache);
        Assert.Equal(Merge3Outcome.Conflict, r.Entries.Single(e => e.Path == "D").Outcome);
        Assert.Equal(Merge3Outcome.Conflict, r.Entries.Single(e => e.Path == "D/z.txt").Outcome);
        Assert.Equal(Merge3Outcome.Conflict, r.Entries.Single(e => e.Path == "P/a").Outcome);
        Assert.Contains("a directory here", r.Entries.Single(e => e.Path == "P/a").Note);
    }
}
