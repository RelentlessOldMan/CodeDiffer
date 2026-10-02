using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Giant;

/// <summary>A changed byte range between two files, at content-defined block granularity.</summary>
public readonly record struct ChangedBlock(HunkOp Op, long OldOffset, long OldLength, long NewOffset, long NewLength);

/// <summary>
/// A giant-file diff: the changed byte ranges plus a bounded summary. Reports WHAT changed by reference
/// (offsets + lengths), never the bytes — the output-model promise for files too big to show.
/// </summary>
public sealed record BlockDiffResult(
    IReadOnlyList<ChangedBlock> Changes,
    int OldBlocks, int NewBlocks, long OldSize, long NewSize,
    long ChangedOldBytes, long ChangedNewBytes)
{
    public bool Identical => Changes.Count == 0;
    /// <summary>Bytes that did NOT change (shared, content-defined), the honest "most of a 1 GB file is unchanged" figure.</summary>
    public long UnchangedBytes => OldSize - ChangedOldBytes;
}

/// <summary>
/// Diffs two files at content-defined-block granularity: run the Myers differ over the two block-SHA
/// sequences (each hash plays the role of a "line"), then map the resulting chunk-index hunks back to
/// absolute byte ranges. The block hashes come from <see cref="BlockIndex"/>, so each file is read once,
/// streamed, bounded memory — the whole point for multi-GB files. Because boundaries are content-defined,
/// an insert/delete resynchronizes at the next boundary and only the local blocks show as changed.
/// </summary>
public static class GiantFileDiffer
{
    public static BlockDiffResult Diff(IReadOnlyList<Chunk> oldChunks, IReadOnlyList<Chunk> newChunks)
    {
        long oldSize = TotalSize(oldChunks), newSize = TotalSize(newChunks);

        var oldShas = oldChunks.Select(c => c.Sha).ToArray();
        var newShas = newChunks.Select(c => c.Sha).ToArray();
        var hunks = LineDiffer.Diff(oldShas, newShas); // block-index coordinates, 1-based

        var changes = new List<ChangedBlock>(hunks.Count);
        long changedOld = 0, changedNew = 0;
        foreach (var h in hunks)
        {
            // Block span in each sequence: an insert/delete anchors on the "after-which" index.
            int oldStart = h.OldLines == 0 ? h.OldStart : h.OldStart - 1;
            int newStart = h.NewLines == 0 ? h.NewStart : h.NewStart - 1;

            long oldOff = ByteOffsetAt(oldChunks, oldStart, oldSize);
            long oldLen = ByteOffsetAt(oldChunks, oldStart + h.OldLines, oldSize) - oldOff;
            long newOff = ByteOffsetAt(newChunks, newStart, newSize);
            long newLen = ByteOffsetAt(newChunks, newStart + h.NewLines, newSize) - newOff;

            changes.Add(new ChangedBlock(h.Op, oldOff, oldLen, newOff, newLen));
            changedOld += oldLen;
            changedNew += newLen;
        }

        return new BlockDiffResult(changes, oldChunks.Count, newChunks.Count, oldSize, newSize, changedOld, changedNew);
    }

    public static BlockDiffResult Diff(string oldPath, string newPath, ChunkerOptions? options = null)
        => Diff(BlockIndex.Build(oldPath, options), BlockIndex.Build(newPath, options));

    private static long TotalSize(IReadOnlyList<Chunk> chunks)
        => chunks.Count == 0 ? 0 : chunks[^1].Offset + chunks[^1].Length;

    /// <summary>Absolute byte offset at a block boundary index (== file size at the end boundary).</summary>
    private static long ByteOffsetAt(IReadOnlyList<Chunk> chunks, int index, long totalSize)
        => index < chunks.Count ? chunks[index].Offset : totalSize;
}
