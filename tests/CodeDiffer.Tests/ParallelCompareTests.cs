using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The parallel compare path: dual-read pair comparison (early exit / whole-file hashing), the
/// concurrent walker (same result at any parallelism), and the comparer end to end at high parallelism.
/// </summary>
public class ParallelCompareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-par-" + Guid.NewGuid().ToString("N"));

    public ParallelCompareTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Put(string rel, byte[] data)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, data);
        return p;
    }

    private static byte[] Payload(int n, byte seed)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 31 + seed);
        return b;
    }

    /// <summary>A walk-style entry for a file, from its live stat (so hashes come back Stable).</summary>
    private static FileEntry E(string path)
    {
        var st = FileStat.OfPath(path);
        return new FileEntry(Path.GetFileName(path), path, st.Length, new DateTime(st.LastWriteUtcTicks, DateTimeKind.Utc), st.ChangeUtcTicks, st.FileId);
    }

    [Fact]
    public async Task IdenticalMultiChunkFiles_AreEqual_AndHashesMatch()
    {
        var data = Payload(PairComparer.ChunkBytes * 2 + 123, 1); // spans 3 chunks
        var a = E(Put("a.bin", data));
        var b = E(Put("b.bin", data));
        var r = await PairComparer.CompareAsync(a, b, hashes: true);
        Assert.True(r.Equal);
        Assert.NotNull(r.Left);
        Assert.Equal(r.Left!.XxHash128, r.Right!.XxHash128);
        Assert.Equal(r.Left.Sha256, r.Right.Sha256);
        Assert.Equal(64, r.Left.Sha256.Length);
        Assert.True(r.Left.Stable && r.Right.Stable);
        var single = await PairComparer.HashAsync(a);
        Assert.Equal((r.Left.XxHash128, r.Left.Sha256), (single.XxHash128, single.Sha256));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)), single.Sha256);
    }

    [Fact]
    public async Task DifferenceInLastChunk_IsDetected()
    {
        var data = Payload(PairComparer.ChunkBytes * 2 + 10, 2);
        var edited = (byte[])data.Clone();
        edited[^1] ^= 0xFF;
        var r = await PairComparer.CompareAsync(E(Put("a", data)), E(Put("b", edited)), hashes: false);
        Assert.False(r.Equal);
        Assert.Null(r.Left); // no hashing requested
    }

    [Fact]
    public async Task EarlyDifference_WithHashes_StillReadsToEnd_AndHashesWholeFiles()
    {
        var data = Payload(PairComparer.ChunkBytes + 500, 3);
        var edited = (byte[])data.Clone();
        edited[0] ^= 0xFF;
        var a = E(Put("a", data));
        var b = E(Put("b", edited));
        var r = await PairComparer.CompareAsync(a, b, hashes: true);
        Assert.False(r.Equal);
        Assert.Equal((await PairComparer.HashAsync(a)).Sha256, r.Left!.Sha256);
        Assert.Equal((await PairComparer.HashAsync(b)).Sha256, r.Right!.Sha256);
        Assert.NotEqual(r.Left.Sha256, r.Right.Sha256);
    }

    [Fact]
    public async Task EmptyFiles_AreEqual()
    {
        var r = await PairComparer.CompareAsync(E(Put("a", [])), E(Put("b", [])), hashes: true);
        Assert.True(r.Equal);
        Assert.Equal(r.Left!.Sha256, r.Right!.Sha256);
    }

    [Fact]
    public async Task StaleListing_MakesHashesUnstable_SoTheyAreNeverCached()
    {
        // The listing row says one mtime, the live handle says another — exactly what a directory entry
        // looks like while a writer holds the file open. The hash describes the LIVE bytes, so it must not be
        // filed under the stale identity.
        var p = Put("a", Payload(1000, 4));
        var stale = E(p) with { LastWriteTimeUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        Assert.False((await PairComparer.HashAsync(stale)).Stable);
        Assert.True((await PairComparer.HashAsync(E(p))).Stable);
    }

    [Fact]
    public void ParallelWalk_FindsSameFilesAsSerial_InSortedOrder()
    {
        for (int d = 0; d < 6; d++)
            for (int f = 0; f < 5; f++)
                Put($"tree/d{d}/s{f % 2}/f{f}.txt", Payload(10 + f, (byte)d));
        Put("tree/.git/ignored.txt", [1]);
        var root = Path.Combine(_dir, "tree");

        var serial = new TreeWalker(parallelism: 1).WalkAll(root);
        var parallel = new TreeWalker(parallelism: 8).WalkAll(root);

        Assert.Equal(30, serial.Files.Count);
        Assert.Equal(serial.Files.Select(f => f.RelativePath), parallel.Files.Select(f => f.RelativePath));
        Assert.Equal(0, parallel.DroppedDirectories);
        Assert.DoesNotContain(parallel.Files, f => f.RelativePath.StartsWith(".git/"));
    }

    [Fact]
    public void Compare_AtHighParallelism_ClassifiesEveryPairCorrectly()
    {
        for (int i = 0; i < 40; i++)
        {
            var data = Payload(1000 + i, (byte)i);
            Put($"L/f{i:D2}.txt", data);
            var right = (byte[])data.Clone();
            if (i % 5 == 0) right[i] ^= 0x01;    // same size, different byte ⇒ Modified
            Put($"R/f{i:D2}.txt", right);
        }
        var report = new DirectoryComparer(new CompareOptions { Parallelism = 8 })
            .Compare(Path.Combine(_dir, "L"), Path.Combine(_dir, "R"));

        Assert.Equal(40, report.Total);
        Assert.Equal(8, report.Count(ChangeStatus.Modified));
        Assert.Equal(32, report.Count(ChangeStatus.Identical));
        Assert.All(report.Changes.Where(c => c.Status == ChangeStatus.Modified),
            c => Assert.Equal(0, int.Parse(c.RelativePath[1..3]) % 5));
        Assert.Equal(report.Changes.Select(c => c.RelativePath).OrderBy(p => p, StringComparer.Ordinal),
            report.Changes.Select(c => c.RelativePath)); // deterministic ordinal order survives parallelism
    }
}

