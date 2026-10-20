using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// --rehash-side: one tree re-read and its cache refreshed while the other trees' cache is used, so one side can be
/// checked against its bytes, or timed cold, without reading the rest again.
/// </summary>
public sealed class RehashSideTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-rehash-side-" + Guid.NewGuid().ToString("N"));
    private const int Files = 20;

    public RehashSideTests()
    {
        for (int i = 0; i < Files; i++)
            foreach (var tree in new[] { "B", "V1", "V2" })
                Put($"{tree}/f{i:D3}.c", $"file {i:D3}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel);

    private void Put(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), new UTF8Encoding(false).GetBytes(text));
    }

    private CompareOptions Opts(params string[] rehash) => new()
    {
        Parallelism = 1,
        CacheBaseDir = P("cache"),
        CodeCompassBaseDir = P("no-compass"),
        Timing = new TrustTiming(0, 0),
        RehashRoots = rehash.Select(P).ToList(),
        RehashSides = string.Join("+", rehash),
    };

    private long Bytes(string tree) => Directory.EnumerateFiles(P(tree)).Sum(f => new FileInfo(f).Length);

    [Fact]
    public void OneSideRehashed_IsReadAgain_AndTheOtherComesFromTheCache()
    {
        var cold = new DirectoryComparer(Opts()).Compare(P("B"), P("V1"));
        Assert.Equal(0, cold.CacheHits);

        var right = new DirectoryComparer(Opts("V1")).Compare(P("B"), P("V1"));
        Assert.Equal(Files, right.CacheHits);           // the left side, from the cache
        Assert.Equal(Bytes("V1"), right.BytesRead);     // the right side, read again
        Assert.All(right.Changes, c => Assert.Equal(ChangeStatus.Identical, c.Status));

        var left = new DirectoryComparer(Opts("B")).Compare(P("B"), P("V1"));
        Assert.Equal(Files, left.CacheHits);
        Assert.Equal(Bytes("B"), left.BytesRead);

        var warm = new DirectoryComparer(Opts()).Compare(P("B"), P("V1")); // and the rehashed ledgers still serve
        Assert.Equal(2 * Files, warm.CacheHits);
        Assert.Equal(0, warm.BytesRead);
    }

    [Fact]
    public void BesideItsCachedTwin_BothFilesAreCounted()
    {
        new DirectoryComparer(Opts()).Compare(P("B"), P("V1")); // B cached
        var r = new DirectoryComparer(Opts()).Compare(P("B"), P("V2"));
        Assert.Equal(Files, r.CacheHits);
        Assert.Equal(2 * Bytes("V2"), r.BytesRead); // on local trees V2 is read beside B, and B's bytes count too
        Assert.All(r.Changes, c => Assert.Equal(ChangeStatus.Identical, c.Status));
    }

    [Fact]
    public void Compare3_RehashesOnlyTheTreeNamed()
    {
        TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts());
        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts("V2"));
        Assert.Equal(0, r.V1Report.BytesRead);           // base and v1 from the cache
        Assert.Equal(2 * Files, r.V1Report.CacheHits);
        Assert.Equal(Bytes("V2"), r.V2Report.BytesRead); // v2 read again; the base still not
    }

    [Fact]
    public void ATreeIsMatchedByItsFullPath_AndOnlyWithTheCacheOn()
    {
        var o = Opts("V1");
        Assert.Equal(CacheMode.Rehash, o.CacheFor(P("V1") + Path.DirectorySeparatorChar));
        Assert.Equal(CacheMode.On, o.CacheFor(P("B")));
        Assert.Equal(CacheMode.Off, new CompareOptions { Cache = CacheMode.Off, RehashRoots = o.RehashRoots }.CacheFor(P("V1")));
        Assert.Equal("on, rehash V1", o.CacheLabel);
    }
}
