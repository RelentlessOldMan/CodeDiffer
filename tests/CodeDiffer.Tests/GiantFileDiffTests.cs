using System.Text;
using CodeDiffer.Core.Giant;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Content-defined chunking + giant-file block diff. The properties that matter for multi-GB files:
/// determinism (same bytes ⇒ same chunks, any machine), boundary RESYNC (an insert near the front dirties
/// only its locality, not the whole tail — what fixed blocks can't do), and byte-accurate change ranges.
/// Small tuning is used so a few KB of test data still produces many chunks.
/// </summary>
public class GiantFileDiffTests
{
    // Tiny chunks so kilobyte inputs exercise real multi-chunk behavior.
    private static readonly ChunkerOptions Opts = new() { MaskBits = 6, MinSize = 16, MaxSize = 512, ReadBufferSize = 256 };

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static IReadOnlyList<Chunk> Index(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return BlockIndex.Build(ms, Opts);
    }

    /// <summary>Pseudo-random but DETERMINISTIC payload (no Random — must be reproducible).</summary>
    private static byte[] Payload(int n, ulong seed)
    {
        var b = new byte[n];
        ulong s = seed;
        for (int i = 0; i < n; i++)
        {
            s = s * 6364136223846793005UL + 1442695040888963407UL; // LCG
            b[i] = (byte)(s >> 33);
        }
        return b;
    }

    [Fact]
    public void Chunking_IsDeterministic()
    {
        var data = Payload(20_000, 1);
        Assert.Equal(Index(data).Select(c => c.Sha), Index(data).Select(c => c.Sha));
    }

    [Fact]
    public void ChunksTileTheFile_OffsetsAndLengthsContiguous()
    {
        var data = Payload(20_000, 2);
        var chunks = Index(data);
        long expected = 0;
        foreach (var c in chunks)
        {
            Assert.Equal(expected, c.Offset);
            expected += c.Length;
        }
        Assert.Equal(data.Length, expected); // chunks exactly cover the file
        Assert.True(chunks.Count > 1);
    }

    [Fact]
    public void IdenticalFiles_NoChange()
    {
        var data = Payload(20_000, 3);
        var r = GiantFileDiffer.Diff(Index(data), Index(data));
        Assert.True(r.Identical);
        Assert.Equal(0, r.ChangedOldBytes);
        Assert.Equal(r.OldSize, r.UnchangedBytes);
    }

    [Fact]
    public void MidFileInsert_Resyncs_TailChunksUnchanged()
    {
        var data = Payload(40_000, 4);
        var edited = data.Take(20_000).Concat(Bytes("<<INSERTED PAYLOAD>>")).Concat(data.Skip(20_000)).ToArray();

        var oldIx = Index(data);
        var newIx = Index(edited);

        // The KEY property: the vast majority of blocks resynchronize and keep their SHA, so the diff is
        // local to the insertion — nowhere near "every block after the edit changed".
        var r = GiantFileDiffer.Diff(oldIx, newIx);
        Assert.False(r.Identical);
        Assert.True(r.ChangedOldBytes < data.Length / 4,
            $"expected a local change, but {r.ChangedOldBytes} of {data.Length} bytes diffed");

        // Concretely: most old block SHAs survive into the new index.
        var newShas = newIx.Select(c => c.Sha).ToHashSet();
        int survivors = oldIx.Count(c => newShas.Contains(c.Sha));
        Assert.True(survivors >= oldIx.Count - 3, $"only {survivors}/{oldIx.Count} blocks resynced");
    }

    [Fact]
    public void LocalizedOverwrite_ChangeIsBounded_AndRangesLandNearTheEdit()
    {
        var data = Payload(40_000, 5);
        var edited = (byte[])data.Clone();
        for (int i = 20_000; i < 20_050; i++) edited[i] ^= 0xFF; // flip 50 bytes mid-file

        var r = GiantFileDiffer.Diff(Index(data), Index(edited));
        Assert.False(r.Identical);
        Assert.True(r.ChangedOldBytes < data.Length / 4);
        // every reported change overlaps the edited window's neighbourhood, not the whole file
        Assert.All(r.Changes, c => Assert.True(c.OldOffset < 25_000 && c.OldOffset + c.OldLength > 15_000));
    }

    [Fact]
    public void Append_ChangeIsAtTail_AndReflectsGrowth()
    {
        var data = Payload(20_000, 6);
        var edited = data.Concat(Payload(5_000, 99)).ToArray();
        var r = GiantFileDiffer.Diff(Index(data), Index(edited));
        Assert.False(r.Identical);
        Assert.Equal(data.Length, r.OldSize);
        Assert.Equal(edited.Length, r.NewSize);
        // The net new-minus-old changed bytes equals the appended size — and the change sits at the tail
        // (the original's EOF-partial final block isn't a content boundary, so it merges with the append).
        Assert.Equal(5_000, r.ChangedNewBytes - r.ChangedOldBytes);
        Assert.All(r.Changes, c => Assert.True(c.OldOffset > 15_000));
    }
}
