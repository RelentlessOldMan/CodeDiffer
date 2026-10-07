using System.Security.Cryptography;

namespace CodeDiffer.Core.Hashing;

/// <summary>
/// The one hash discipline for the whole tool: <b>SHA-256 of raw file bytes, lowercase hex</b>.
/// This is (a) the hash the CodeSpawner delta contract uses, so the verify-adapter asserts directly;
/// and (b) the loose-coupling point with a CodeCompass index (a shared fact, not a shared binary).
/// Files are hashed by streaming (bounded memory) so a multi-GB header hashes on a 16 GB box.
/// </summary>
public static class ContentHasher
{
    /// <summary>Read buffer for streamed hashing (~1 MB) — bounded regardless of file size.</summary>
    public const int StreamBufferBytes = 1 << 20;

    /// <summary>Hash a file's raw bytes. Streams in ~1 MB chunks; never loads the whole file. A cancel is seen
    /// between chunks.</summary>
    public static string HashFile(string path, CancellationToken ct = default)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: StreamBufferBytes, FileOptions.SequentialScan);
        return HashStream(stream, ct);
    }

    /// <summary>Hash an arbitrary stream's bytes (streamed).</summary>
    public static string HashStream(Stream stream, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[StreamBufferBytes];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>Hash a byte span (small inputs, tests, head reads).</summary>
    public static string HashBytes(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
