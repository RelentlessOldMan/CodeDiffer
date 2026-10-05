using System.Buffers;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// Outcome of comparing one same-path, same-size file pair. Hashes are XxHash128 of the WHOLE file in the
/// ledger's form (canonical bytes, 32 uppercase hex — exactly CodeCompass's), present only when the pair
/// was read to the end with hashing on.
/// </summary>
public readonly record struct PairResult(bool Equal, string? LeftHash, string? RightHash);

/// <summary>
/// Decides whether two same-size files are byte-identical by reading BOTH at once, chunk by chunk
/// (overlapped async reads, so the two SMB round-trips overlap instead of stacking), and comparing the
/// bytes directly — no hash is needed to prove equality.
///
/// With <c>hashes: false</c> the read stops at the first differing chunk, so a modified file costs only
/// up to its first change. With <c>hashes: true</c> both files are read to the end and their whole-file
/// XxHash128 returned (to fill the hash ledger), even when they differ.
/// </summary>
public static class PairComparer
{
    public const int ChunkBytes = 1 << 20;

    public static async Task<PairResult> CompareAsync(string left, string right, bool hashes, CancellationToken ct = default)
    {
        using var lh = Open(left);
        using var rh = Open(right);
        long lenL = RandomAccess.GetLength(lh), lenR = RandomAccess.GetLength(rh);
        if (lenL != lenR && !hashes) return new PairResult(false, null, null);

        var hl = hashes ? new XxHash128() : null;
        var hr = hashes ? new XxHash128() : null;
        var bufL = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var bufR = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            bool equal = lenL == lenR;
            long offL = 0, offR = 0;
            while (offL < lenL || offR < lenR)
            {
                var tl = offL < lenL ? RandomAccess.ReadAsync(lh, bufL.AsMemory(0, ChunkBytes), offL, ct) : ValueTask.FromResult(0);
                var tr = offR < lenR ? RandomAccess.ReadAsync(rh, bufR.AsMemory(0, ChunkBytes), offR, ct) : ValueTask.FromResult(0);
                int nl = await tl.ConfigureAwait(false);
                int nr = await tr.ConfigureAwait(false);
                if (nl == 0 && nr == 0) break; // file shrank under us — whatever was compared stands

                if (equal && (nl != nr || !bufL.AsSpan(0, nl).SequenceEqual(bufR.AsSpan(0, nr))))
                {
                    equal = false;
                    if (!hashes) return new PairResult(false, null, null);
                }
                hl?.Append(bufL.AsSpan(0, nl));
                hr?.Append(bufR.AsSpan(0, nr));
                offL += nl;
                offR += nr;
            }
            return new PairResult(equal, Hex(hl), Hex(hr));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufL);
            ArrayPool<byte>.Shared.Return(bufR);
        }
    }

    /// <summary>Whole-file XxHash128 of one file (streamed, 1 MB chunks).</summary>
    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        using var h = Open(path);
        var hasher = new XxHash128();
        var buf = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            long off = 0;
            int n;
            while ((n = await RandomAccess.ReadAsync(h, buf.AsMemory(0, ChunkBytes), off, ct).ConfigureAwait(false)) > 0)
            {
                hasher.Append(buf.AsSpan(0, n));
                off += n;
            }
            return Hex(hasher)!;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static string? Hex(XxHash128? h) => h is null ? null : Convert.ToHexString(h.GetCurrentHash());

    private static SafeFileHandle Open(string path)
        => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
