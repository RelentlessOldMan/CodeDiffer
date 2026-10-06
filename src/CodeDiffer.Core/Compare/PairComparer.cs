using System.Buffers;
using System.IO.Hashing;
using System.Security.Cryptography;
using CodeDiffer.Core.Walk;
using Microsoft.Win32.SafeHandles;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// A file's content hashes in ledger form: XxHash128 (canonical bytes, 32 uppercase hex — CodeCompass's
/// form) and SHA-256 (64 uppercase hex). <see cref="Stable"/> is true only when the file's handle-level
/// size/mtime/ChangeTime/FileId were identical immediately before and after the read AND matched the walk's
/// listing row — i.e. the hashes provably describe the file the listing describes. Only stable hashes may be
/// cached. <see cref="HashedAtUtcTicks"/> is the local UTC clock just before the read began.
/// </summary>
public sealed record FileHashes(string XxHash128, string Sha256, bool Stable, long HashedAtUtcTicks);

/// <summary>
/// Outcome of comparing one same-path, same-size file pair. Hashes are present only when the pair was read
/// to the end with hashing on.
/// </summary>
public readonly record struct PairResult(bool Equal, FileHashes? Left, FileHashes? Right);

/// <summary>
/// Decides whether two same-size files are byte-identical by reading BOTH at once, chunk by chunk
/// (overlapped async reads, so the two SMB round-trips overlap instead of stacking), and comparing the
/// bytes directly — no hash is needed to prove equality.
///
/// With <c>hashes: false</c> the read stops at the first differing chunk, so a modified file costs only
/// up to its first change. With <c>hashes: true</c> both files are read to the end and their whole-file
/// XxHash128 + SHA-256 returned (to fill the hash ledger), even when they differ, each bracketed by a
/// before/after handle stat so a file changed mid-read is never cached.
/// </summary>
public static class PairComparer
{
    public const int ChunkBytes = 1 << 20;

    public static async Task<PairResult> CompareAsync(FileEntry left, FileEntry right, bool hashes, CancellationToken ct = default, Action<int, int>? onChunk = null)
    {
        using var lh = Open(left.FullPath);
        using var rh = Open(right.FullPath);
        long hashedAt = DateTime.UtcNow.Ticks;
        var beforeL = hashes ? FileStat.Of(lh, left.FullPath) : default;
        var beforeR = hashes ? FileStat.Of(rh, right.FullPath) : default;
        long lenL = RandomAccess.GetLength(lh), lenR = RandomAccess.GetLength(rh);
        if (lenL != lenR && !hashes) return new PairResult(false, null, null);

        var hl = hashes ? new Hasher() : null;
        var hr = hashes ? new Hasher() : null;
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
                if (nl == 0 && nr == 0) break; // file shrank under us — the after-stat marks it unstable
                onChunk?.Invoke(nl, nl + nr); // progress: one side's advance, bytes read

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
            if (!hashes) return new PairResult(equal, null, null);
            return new PairResult(equal,
                hl!.Finish(Stable(beforeL, FileStat.Of(lh, left.FullPath), left), hashedAt),
                hr!.Finish(Stable(beforeR, FileStat.Of(rh, right.FullPath), right), hashedAt));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufL);
            ArrayPool<byte>.Shared.Return(bufR);
        }
    }

    /// <summary>Whole-file XxHash128 + SHA-256 of one file (streamed, 1 MB chunks), stat-bracketed.</summary>
    public static async Task<FileHashes> HashAsync(FileEntry file, CancellationToken ct = default, Action<int, int>? onChunk = null)
    {
        using var h = Open(file.FullPath);
        long hashedAt = DateTime.UtcNow.Ticks;
        var before = FileStat.Of(h, file.FullPath);
        var hasher = new Hasher();
        var buf = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            long off = 0;
            int n;
            while ((n = await RandomAccess.ReadAsync(h, buf.AsMemory(0, ChunkBytes), off, ct).ConfigureAwait(false)) > 0)
            {
                onChunk?.Invoke(n, n);
                hasher.Append(buf.AsSpan(0, n));
                off += n;
            }
            return hasher.Finish(Stable(before, FileStat.Of(h, file.FullPath), file), hashedAt);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>Convenience for callers with only a path (tests, probes): stat-less entry ⇒ never Stable.</summary>
    public static Task<FileHashes> HashAsync(string path, CancellationToken ct = default)
        => HashAsync(new FileEntry("", path, -1, default), ct);

    private static bool Stable(FileStat before, FileStat after, FileEntry listing)
        => before == after && after.Matches(listing) && after.ChangeUtcTicks != 0;

    private sealed class Hasher
    {
        private readonly XxHash128 _xx = new();
        private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Append(ReadOnlySpan<byte> data)
        {
            _xx.Append(data);
            _sha.AppendData(data);
        }

        public FileHashes Finish(bool stable, long hashedAt)
        {
            var result = new FileHashes(Convert.ToHexString(_xx.GetCurrentHash()), Convert.ToHexString(_sha.GetHashAndReset()), stable, hashedAt);
            _sha.Dispose();
            return result;
        }
    }

    private static SafeFileHandle Open(string path)
        => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
