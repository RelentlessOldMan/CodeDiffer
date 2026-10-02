using System.Text;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Verify;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The 3-way reconstruction gate on real temp-dir trees: a manifest's per-side coordinates must rebuild
/// both variant trees from the base. A faithful manifest reconstructs; a manifest with a corrupted
/// coordinate must be caught.
/// </summary>
public sealed class ConflictTreeCrossCheckTests : IDisposable
{
    private readonly string _base = NewTempDir();
    private readonly string _v1 = NewTempDir();
    private readonly string _v2 = NewTempDir();

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"codediffer-3way-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void Write(string root, string rel, string text)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encoding.UTF8.GetBytes(text));
    }

    public void Dispose()
    {
        foreach (var d in new[] { _base, _v1, _v2 })
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void FaithfulManifest_ReconstructsBothSides()
    {
        // One conflict file + one clean-each-way file.
        Write(_base, "x.c", "a\nb\nc\n"); Write(_v1, "x.c", "a\nB\nc\n"); Write(_v2, "x.c", "a\nC\nc\n");
        Write(_base, "y.c", "p\nq\nr\ns\n"); Write(_v1, "y.c", "P\nq\nr\ns\n"); Write(_v2, "y.c", "p\nq\nr\nS\n");

        var manifest = BuildManifestFromMerger("x.c", "y.c");
        var cc = ConflictTreeCrossCheck.Run(_base, _v1, _v2, manifest);

        Assert.True(cc.Ok);
        Assert.Equal(2, cc.FilesChecked);
        Assert.Equal(2, cc.V1Reconstructed);
        Assert.Equal(2, cc.V2Reconstructed);
    }

    [Fact]
    public void CorruptedCoordinate_IsCaught()
    {
        Write(_base, "x.c", "a\nb\nc\n"); Write(_v1, "x.c", "a\nB\nc\n"); Write(_v2, "x.c", "a\nC\nc\n");

        var good = BuildManifestFromMerger("x.c");
        // Corrupt the v1 side of the single conflict: point it at the wrong base line.
        var c = good.Conflicts[0];
        var bad = good with { Conflicts = [c with { BaseStart = 1 }] };

        var cc = ConflictTreeCrossCheck.Run(_base, _v1, _v2, bad);
        Assert.False(cc.Ok);
        Assert.False(cc.Files.Single().V1Reconstructs);
    }

    /// <summary>Compose a ConflictManifest by running CodeDiffer's own merger over the temp trees.</summary>
    private ConflictManifest BuildManifestFromMerger(params string[] paths)
    {
        var conflicts = new List<Conflict>();
        var clean = new List<CleanMerge>();
        foreach (var p in paths)
        {
            var rel = p.Replace('/', Path.DirectorySeparatorChar);
            var r = ThreeWayMerger.Merge(
                p,
                File.ReadAllText(Path.Combine(_base, rel)),
                File.ReadAllText(Path.Combine(_v1, rel)),
                File.ReadAllText(Path.Combine(_v2, rel)));
            conflicts.AddRange(r.Conflicts);
            clean.AddRange(r.CleanMerges);
        }
        return new ConflictManifest(1, "base_v1", "base_v2", conflicts, clean, ConflictTruthSha: null);
    }
}
