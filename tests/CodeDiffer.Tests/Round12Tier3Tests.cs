using System.Text;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.ThreeWay;
using CodeDiffer.Core.Verify;
using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Code review pass 12, tier 3: a conflict manifest with a side other than v1/v2 or a section missing is refused; an
/// overlay directory someone else took is told apart from other refusals (write_merge's default moves on); a directory
/// that lists exactly what one above it lists is a loop.
/// </summary>
public sealed class Round12Tier3Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r12c-" + Guid.NewGuid().ToString("N"));

    public Round12Tier3Tests() => Directory.CreateDirectory(_dir);

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

    // ---- the conflict manifest's side and sections ----

    private static string Manifest(string side, bool withConflicts = true, bool withClean = true)
        => "{ \"_meta\": { \"manifestVersion\": 1, \"deltaKind\": \"conflict-3way\" }" +
           (withConflicts ? ", \"conflicts\": []" : "") +
           (withClean ? $", \"mergedClean\": [ {{ \"path\": \"f.c\", \"side\": \"{side}\", \"op\": \"replace\", \"oldStart\": 3, \"oldLines\": 1, \"newStart\": 3, \"newLines\": 1 }} ]" : "") +
           " }";

    [Theory]
    [InlineData("v3")]
    [InlineData("V2")]
    [InlineData("")]
    public void ACleanMergeOfNeitherSide_IsRefused(string side)
        => Assert.Contains("'side' must be v1 or v2", Assert.Throws<FormatException>(() => ConflictManifestParser.Parse(Manifest(side))).Message);

    [Fact]
    public void AConflictManifestMissingASection_IsRefused()
    {
        Assert.Equal("v2", ConflictManifestParser.Parse(Manifest("v2")).CleanMerges.Single().Side);
        Assert.Contains("'conflicts' must be an array", Assert.Throws<FormatException>(() => ConflictManifestParser.Parse(Manifest("v1", withConflicts: false))).Message);
        Assert.Contains("'mergedClean' must be an array", Assert.Throws<FormatException>(() => ConflictManifestParser.Parse(Manifest("v1", withClean: false))).Message);
    }

    // ---- an overlay directory already taken ----

    [Fact]
    public void AnOverlayDirectoryAlreadyTaken_IsItsOwnRefusal()
    {
        foreach (var t in new[] { "B", "V1", "V2" }) Put($"{t}/a.txt", "a\n");
        Put("V2/a.txt", "b\n");
        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), new CompareOptions { Cache = CacheMode.Off });
        Put("merge/INCOMPLETE.txt", "another writer\n");
        Assert.Throws<OverlayDirTakenException>(() => MergeOverlay.Write(r, P("B"), P("V1"), P("V2"), P("merge")));
        var other = Assert.ThrowsAny<ArgumentException>(() => MergeOverlay.Write(r, P("B"), P("V1"), P("V2"), P("V1/inside")));
        Assert.IsNotType<OverlayDirTakenException>(other);
        Assert.Equal(1, MergeOverlay.Write(r, P("B"), P("V1"), P("V2"), P("merge-2")).FromV2);
    }

    // ---- a loop a server followed ----

    private static DirRow Row(string name, long id, bool dir = false) => new(name, dir, false, 1, 0, 0, id);

    [Fact]
    public void ADirectoryListingWhatOneAboveItLists_HasItsSignature()
    {
        // Samba following x -> .. : x lists its parent's entries, x itself among them.
        List<DirRow> parent = [Row("a.c", 11), Row("x", 12, dir: true), Row("sub", 13, dir: true)];
        List<DirRow> x = [Row("sub", 13, dir: true), Row("a.c", 11), Row("x", 12, dir: true)];
        Assert.NotNull(TreeWalker.Signature(parent));
        Assert.Equal(TreeWalker.Signature(parent), TreeWalker.Signature(x));
        // A real a/a with the same names: other file ids.
        Assert.NotEqual(TreeWalker.Signature(parent), TreeWalker.Signature([Row("a.c", 21), Row("x", 22, dir: true), Row("sub", 23, dir: true)]));
        // No file ids from the server: names alone never call it a loop.
        Assert.Null(TreeWalker.Signature([Row("a.c", 0), Row("x", 0, dir: true)]));
    }

    [Fact]
    public void AnOrdinaryTree_LosesNothingToTheLoopCheck()
    {
        for (int i = 0; i < 4; i++) Put($"t/d{i}/d/d/f.txt", "same\n"); // alike subtrees, alike names
        Put("t/d0/d/f.txt", "same\n");
        var w = new TreeWalker().WalkAll(P("t"));
        Assert.Equal(5, w.Files.Count);
        Assert.Empty(w.SkippedPaths);
    }
}
