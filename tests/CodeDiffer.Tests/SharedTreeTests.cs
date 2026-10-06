using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// compare3's second compare reuses the first one's base listing and base hashes (SharedTree): the verdicts
/// must be exactly those of an independent compare, only cheaper.
/// </summary>
public sealed class SharedTreeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-shared-" + Guid.NewGuid().ToString("N"));
    private readonly string _base, _v1, _v2;

    public SharedTreeTests()
    {
        _base = Path.Combine(_dir, "B");
        _v1 = Path.Combine(_dir, "V1");
        _v2 = Path.Combine(_dir, "V2");
        for (int i = 0; i < 30; i++)
            foreach (var d in new[] { "B", "V1", "V2" })
                Put($"{d}/f{i:D2}.c", $"file {i}\nbody\n");
        Put("V1/f01.c", "file 1\nBODY\n");   // same size, edited on v1
        Put("V2/f02.c", "file 2\nBODY\n");   // same size, edited on v2
        Put("V2/f03.c", "file 3\nlonger body\n");
        Put("V2/extra.c", "new\n");
        File.Delete(Path.Combine(_v2, "f04.c"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Put(string rel, string text)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(text));
    }

    private static string Verdicts(CompareReport r) =>
        string.Join("\n", r.Changes.Select(c => $"{c.Status} {c.Reason} {c.RelativePath} {c.RenamedFrom}"));

    [Theory]
    [InlineData(CacheMode.On)]
    [InlineData(CacheMode.Rehash)]
    public void SecondCompare_ReusesTheBase_AndMatchesAnIndependentCompare(CacheMode mode)
    {
        var opt = new CompareOptions { Cache = mode };
        var shared = new SharedTree();
        var r1 = new DirectoryComparer(opt).Compare(_base, _v1, sharedLeft: shared);
        var r2 = new DirectoryComparer(opt).Compare(_base, _v2, sharedLeft: shared);
        var alone = new DirectoryComparer(new CompareOptions { Cache = CacheMode.Off }).Compare(_base, _v2);

        Assert.Equal(0, r1.ReusedLeftFiles);
        // Every base file both compares pair same-size was proven in the first: f00..f29 minus f03 (size
        // changed on v2) and f04 (gone on v2).
        Assert.Equal(28, r2.ReusedLeftFiles);
        Assert.Equal(Verdicts(alone), Verdicts(r2));
        Assert.Contains(r2.Changes, c => c.RelativePath == "f02.c" && c.Status == ChangeStatus.Modified);
    }

    [Fact]
    public void NoCache_SharesTheListingOnly()
    {
        var opt = new CompareOptions { Cache = CacheMode.Off };
        var shared = new SharedTree();
        new DirectoryComparer(opt).Compare(_base, _v1, sharedLeft: shared);
        var r2 = new DirectoryComparer(opt).Compare(_base, _v2, sharedLeft: shared);
        Assert.Equal(0, r2.ReusedLeftFiles); // --no-cache byte-compares every pair, so no ids to pass on
        Assert.Equal(Verdicts(new DirectoryComparer(opt).Compare(_base, _v2)), Verdicts(r2));
    }

    [Fact]
    public void ADifferentLeftRoot_StartsOver()
    {
        var opt = new CompareOptions { Cache = CacheMode.Rehash };
        var shared = new SharedTree();
        new DirectoryComparer(opt).Compare(_base, _v1, sharedLeft: shared);
        var r = new DirectoryComparer(opt).Compare(_v1, _v2, sharedLeft: shared);
        Assert.Equal(0, r.ReusedLeftFiles);
        Assert.Equal(Verdicts(new DirectoryComparer(new CompareOptions { Cache = CacheMode.Off }).Compare(_v1, _v2)), Verdicts(r));
    }
}
