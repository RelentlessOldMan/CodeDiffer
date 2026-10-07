using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// End-to-end rename resolution through the 2-way compare, against the CodeSpawner contract: pure
/// renames (identical bytes, sim 1000, binary too), edited renames scored by the similarity metric and
/// kept only above threshold, best-match among competitors, and the decoy case — a near-duplicate ADD
/// whose source still exists on both sides must NOT be mistaken for a rename.
/// </summary>
public sealed class RenameDetectionTests : IDisposable
{
    private readonly string _left = NewTempDir();
    private readonly string _right = NewTempDir();

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"codediffer-ren-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void WriteLeft(string rel, string text) => Write(_left, rel, Encoding.UTF8.GetBytes(text));
    private void WriteRight(string rel, string text) => Write(_right, rel, Encoding.UTF8.GetBytes(text));
    private void WriteLeft(string rel, byte[] bytes) => Write(_left, rel, bytes);
    private void WriteRight(string rel, byte[] bytes) => Write(_right, rel, bytes);

    private static void Write(string root, string rel, byte[] bytes)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private CompareReport Run(CompareOptions? o = null) => new DirectoryComparer(o).Compare(_left, _right);
    private static FileChange Single(CompareReport r, ChangeStatus s) => r.Changes.Single(c => c.Status == s);

    public void Dispose()
    {
        try { Directory.Delete(_left, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_right, recursive: true); } catch { /* best effort */ }
    }

    private const string TenLines = "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\n";

    [Fact]
    public void PureRename_IdenticalBytes_IsRenamedSim1000()
    {
        WriteLeft("src/old.c", TenLines);
        WriteRight("src/new.c", TenLines); // identical content, moved path

        var r = Run();
        var ren = Single(r, ChangeStatus.Renamed);
        Assert.Equal("src/new.c", ren.RelativePath);
        Assert.Equal("src/old.c", ren.RenamedFrom);
        Assert.Equal(1000, ren.SimilarityMilli);
        Assert.Equal(0, r.Count(ChangeStatus.Added));
        Assert.Equal(0, r.Count(ChangeStatus.Removed));
    }

    [Fact]
    public void PureRename_Binary_DetectedBySha()
    {
        var blob = new byte[] { 1, 2, 0, 3, 4, 0, 5 }; // NUL ⇒ binary; still a pure rename by hash
        WriteLeft("a/img.bin", blob);
        WriteRight("b/img.bin", blob);

        var ren = Single(Run(), ChangeStatus.Renamed);
        Assert.Equal("b/img.bin", ren.RelativePath);
        Assert.Equal("a/img.bin", ren.RenamedFrom);
        Assert.Equal(1000, ren.SimilarityMilli);
    }

    [Fact]
    public void EditedRename_AboveThreshold_IsRenamedWithGradedSimilarity()
    {
        WriteLeft("a.txt", TenLines);
        WriteRight("b.txt", "l1\nl2\nl3\nl4\nXX\nl6\nl7\nl8\nl9\nl10\n"); // 1 of 10 changed ⇒ 900

        var ren = Single(Run(), ChangeStatus.Renamed);
        Assert.Equal("b.txt", ren.RelativePath);
        Assert.Equal("a.txt", ren.RenamedFrom);
        Assert.Equal(900, ren.SimilarityMilli);
    }

    [Fact]
    public void EditedRename_BelowThreshold_StaysAddAndRemove()
    {
        WriteLeft("a.txt", TenLines);
        WriteRight("b.txt", "x\ny\nz\n"); // disjoint ⇒ sim 0, under the 500 default

        var r = Run();
        Assert.Equal(0, r.Count(ChangeStatus.Renamed));
        Assert.Equal("a.txt", Single(r, ChangeStatus.Removed).RelativePath);
        Assert.Equal("b.txt", Single(r, ChangeStatus.Added).RelativePath);
    }

    [Fact]
    public void BestMatch_WinsAmongCompetingAdds()
    {
        WriteLeft("a.txt", TenLines);
        WriteRight("exact.txt", TenLines);                                   // sim 1000
        WriteRight("half.txt", "l1\nl2\nl3\nl4\nl5\nA\nB\nC\nD\nE\n");       // 5/10 ⇒ 500

        var r = Run();
        var ren = Single(r, ChangeStatus.Renamed);
        Assert.Equal("exact.txt", ren.RelativePath);          // the removed file pairs with its best match
        Assert.Equal(1000, ren.SimilarityMilli);
        Assert.Equal("half.txt", Single(r, ChangeStatus.Added).RelativePath); // leftover add, not a rename
    }

    [Fact]
    public void Decoy_NearDuplicateAdd_WhoseSourceStillExists_IsNotARename()
    {
        // orig.txt is unchanged on both sides (⇒ Identical, not Removed), so the near-duplicate
        // orig_dup.txt add has no removed counterpart to pair with — precision oracle.
        WriteLeft("orig.txt", TenLines);
        WriteRight("orig.txt", TenLines);
        WriteRight("orig_dup.txt", TenLines); // byte-identical decoy ADD

        var r = Run();
        Assert.Equal(0, r.Count(ChangeStatus.Renamed));
        Assert.Equal(ChangeStatus.Identical, r.Changes.Single(c => c.RelativePath == "orig.txt").Status);
        Assert.Equal("orig_dup.txt", Single(r, ChangeStatus.Added).RelativePath);
    }

    [Fact]
    public void DetectRenamesOff_FallsBackToAddAndRemove()
    {
        WriteLeft("src/old.c", TenLines);
        WriteRight("src/new.c", TenLines);

        var r = Run(new CompareOptions { DetectRenames = false });
        Assert.Equal(0, r.Count(ChangeStatus.Renamed));
        Assert.Equal("src/old.c", Single(r, ChangeStatus.Removed).RelativePath);
        Assert.Equal("src/new.c", Single(r, ChangeStatus.Added).RelativePath);
    }

    [Fact]
    public void EditedRenames_PrefilteredAndParallel_AreExactlyTheBruteForceAnswer()
    {
        // Files of very different lengths sharing a small line pool (duplicates included): many pairs are ruled
        // out by line counts alone, many score near the threshold — the fast path must agree on every one.
        var rng = new Random(1234);
        string Make(int n) => string.Concat(Enumerable.Range(0, n).Select(_ => $"w{rng.Next(40)}\n"));
        var left = Enumerable.Range(0, 30).Select(i => (Path: $"old/f{i:D2}.c", Text: Make(rng.Next(5, 80)))).ToList();
        var right = Enumerable.Range(0, 30).Select(i => (Path: $"new/g{i:D2}.c", Text: Make(rng.Next(5, 80)))).ToList();
        foreach (var (p, t) in left) WriteLeft(p, t);
        foreach (var (p, t) in right) WriteRight(p, t);

        // The reference: every pair scored by the contract's Similarity, then the same greedy best-first assignment.
        var scored = (from l in left from r in right
                      let m = CodeDiffer.Core.Diff.Similarity.Milli(l.Text, r.Text)
                      where m >= 500
                      select (From: l.Path, To: r.Path, m))
            .OrderByDescending(x => x.m).ThenBy(x => x.From, StringComparer.Ordinal).ThenBy(x => x.To, StringComparer.Ordinal).ToList();
        var usedL = new HashSet<string>();
        var usedR = new HashSet<string>();
        var expected = new List<(string, string, int)>();
        foreach (var (from, to, m) in scored)
        {
            if (usedL.Contains(from) || usedR.Contains(to)) continue;
            usedL.Add(from);
            usedR.Add(to);
            expected.Add((from, to, m));
        }
        Assert.True(expected.Count >= 5, $"fixture too easy: {expected.Count} renames");

        var actual = Run().Changes.Where(c => c.Status == ChangeStatus.Renamed)
            .Select(c => (c.RenamedFrom!, c.RelativePath, c.SimilarityMilli!.Value)).ToList();
        Assert.Equal(expected.OrderBy(e => e.Item1, StringComparer.Ordinal), actual.OrderBy(e => e.Item1, StringComparer.Ordinal));
    }
}
