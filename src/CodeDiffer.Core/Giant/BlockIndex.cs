using System.Security.Cryptography;

namespace CodeDiffer.Core.Giant;

/// <summary>One content-defined chunk: its byte offset in the file, length, and SHA-256 (lowercase hex).</summary>
public readonly record struct Chunk(long Offset, int Length, string Sha);

/// <summary>Tuning for the content-defined chunker. Defaults target ~64 KB average chunks.</summary>
public sealed class ChunkerOptions
{
    /// <summary>Boundary mask bits: a cut is allowed where the low <c>MaskBits</c> fingerprint bits are 0, so the average chunk is ~2^MaskBits bytes.</summary>
    public int MaskBits { get; init; } = 16;        // ~64 KB average
    public int MinSize { get; init; } = 16 * 1024;  // never cut shorter than this (avoids tiny chunks)
    public int MaxSize { get; init; } = 256 * 1024; // always cut by here (bounds worst-case memory/skew)

    /// <summary>Streaming read buffer. Bounds working memory regardless of file size.</summary>
    public int ReadBufferSize { get; init; } = 1 << 20; // 1 MB

    public static readonly ChunkerOptions Default = new();
}

/// <summary>
/// Builds a file's block index by streaming it once through a FastCDC-style content-defined chunker and
/// SHA-256-ing each chunk. Memory is bounded by <see cref="ChunkerOptions.ReadBufferSize"/> plus the chunk
/// list (metadata only — never the file body), so a multi-GB header over SMB is indexed without loading it.
///
/// Content-defined boundaries (cut where the rolling gear fingerprint has MaskBits low zero bits) are the
/// whole point: an edit near the front of a 1 GB file only re-chunks its locality; every chunk after the
/// next boundary keeps its hash, so the diff stays O(changed bytes), not O(file). Fixed-size blocks can't
/// do this — one inserted byte would shift and dirty every block.
/// </summary>
public static class BlockIndex
{
    public static IReadOnlyList<Chunk> Build(string path, ChunkerOptions? options = null)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, FileOptions.SequentialScan);
        return Build(fs, options);
    }

    public static IReadOnlyList<Chunk> Build(Stream stream, ChunkerOptions? options = null)
    {
        var o = options ?? ChunkerOptions.Default;
        ulong mask = o.MaskBits >= 64 ? ulong.MaxValue : (1UL << o.MaskBits) - 1;

        var chunks = new List<Chunk>();
        var buffer = new byte[o.ReadBufferSize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        long chunkStart = 0;   // absolute offset of the current chunk
        int chunkLen = 0;      // bytes accumulated into the current chunk
        ulong fp = 0;          // rolling gear fingerprint, reset at each chunk

        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            int segStart = 0; // start of the not-yet-hashed run within this buffer
            for (int i = 0; i < read; i++)
            {
                fp = (fp << 1) + GearHash.Table[buffer[i]];
                chunkLen++;

                bool cut = (chunkLen >= o.MinSize && (fp & mask) == 0) || chunkLen >= o.MaxSize;
                if (!cut) continue;

                hash.AppendData(buffer, segStart, i - segStart + 1); // feed this chunk's tail
                chunks.Add(new Chunk(chunkStart, chunkLen, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));

                chunkStart += chunkLen;
                chunkLen = 0;
                fp = 0;
                segStart = i + 1;
            }

            if (segStart < read) // carry the unterminated chunk's bytes into the hash before next read
                hash.AppendData(buffer, segStart, read - segStart);
        }

        if (chunkLen > 0) // final partial chunk at EOF
            chunks.Add(new Chunk(chunkStart, chunkLen, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));

        return chunks;
    }
}
