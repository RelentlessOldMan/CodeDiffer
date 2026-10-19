using System.Buffers;
using System.IO.Hashing;
using System.Security.Cryptography;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Walk;
using Microsoft.Win32.SafeHandles;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// A file's content hashes in ledger form: XxHash128 (canonical bytes, 32 uppercase hex — CodeCompass's
/// form) and SHA-256 (64 uppercase hex). <see cref="Stable"/> is true only when the file's handle-level
/// size/mtime/ChangeTime/FileId were identical immediately before and after the read AND matched the walk's
/// listing row — i.e. the hashes provably describe the file the listing describes. Only stable hashes may be
/// cached. <see cref="HashedAtUtcTicks"/> is the local UTC clock just before the read began. Why a read isn't stable:
/// <see cref="Doubt"/>.
/// </summary>
public sealed record FileHashes(string XxHash128, string Sha256, bool Stable, long HashedAtUtcTicks, ReadDoubt Doubt = ReadDoubt.None)
{
    /// <summary>Stable, but the listing's FileId isn't the handle's (size, mtime and ChangeTime all agree): a server
    /// that numbers a file one way in its listing and another when it's opened. Cached without an id.</summary>
    public bool ListingIdDiffers { get; init; }
}

/// <summary>Why a read's hashes can't vouch for the file the listing describes (never cached), most telling first.</summary>
public enum ReadDoubt : byte
{
    None,
    /// <summary>The handle's size, times or id moved between before and after the read: a writer during it.</summary>
    Changed,
    /// <summary>Steady through the read, but not what the walk's directory entry says: a stale entry (a write
    /// through another hard link, a server's cached listing) or a touch since the walk.</summary>
    Listing,
    /// <summary>The server gives the handle no change time, so a same-size, same-mtime edit couldn't be told.</summary>
    NoChangeTime,
}

/// <summary>
/// Outcome of comparing one same-path, same-size file pair. Hashes are present only when the pair was read
/// to the end with hashing on.
/// </summary>
public readonly record struct PairResult(bool Equal, FileHashes? Left, FileHashes? Right)
{
    /// <summary>With <c>keepSmall</c>: both files' whole bytes, when they differ and each fit in one chunk (so the
    /// reason can be found without reading them again).</summary>
    public byte[]? LeftBytes { get; init; }
    public byte[]? RightBytes { get; init; }
}

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

    public static async Task<PairResult> CompareAsync(FileEntry left, FileEntry right, bool hashes, CancellationToken ct = default, Action<int, int>? onChunk = null,
        IoMeter? meter = null, bool keepSmall = false)
    {
        long t0 = IoMeter.Now();
        string pathL = left.FullPath, pathR = right.FullPath;
        using var lh = Open(pathL);
        using var rh = Open(pathR);
        long hashedAt = DateTime.UtcNow.Ticks;
        var beforeL = hashes ? FileStat.Of(lh, pathL) : default;
        var beforeR = hashes ? FileStat.Of(rh, pathR) : default;
        long lenL = RandomAccess.GetLength(lh), lenR = RandomAccess.GetLength(rh);
        meter?.Opened(t0);
        if (lenL != lenR && !hashes) return new PairResult(false, null, null);

        var hl = hashes ? new Hasher() : null;
        var hr = hashes ? (lenL == lenR ? hl!.Twin() : null) ?? new Hasher() : null;
        var bufL = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var bufR = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        byte[]? keptL = null, keptR = null;
        try
        {
            bool equal = lenL == lenR;
            long offL = 0, offR = 0;
            while (offL < lenL || offR < lenR)
            {
                // Each side's chunk read in full (a read may return less, over SMB too): chunks of two equal files line up.
                long tRead = IoMeter.Now();
                var tl = offL < lenL ? Fill(lh, bufL.AsMemory(0, (int)Math.Min(ChunkBytes, lenL - offL)), offL, ct) : ValueTask.FromResult(0);
                var tr = offR < lenR ? Fill(rh, bufR.AsMemory(0, (int)Math.Min(ChunkBytes, lenR - offR)), offR, ct) : ValueTask.FromResult(0);
                int nl = await tl.ConfigureAwait(false);
                int nr = await tr.ConfigureAwait(false);
                meter?.Read(tRead, nl + nr);
                if (nl == 0 && nr == 0) break; // file shrank under us — the after-stat marks it unstable
                onChunk?.Invoke(nl, nl + nr); // progress: one side's advance, bytes read
                long tWork = IoMeter.Now();

                if (equal && (nl != nr || !bufL.AsSpan(0, nl).SequenceEqual(bufR.AsSpan(0, nr))))
                {
                    equal = false;
                    hr?.Part();
                    if (keepSmall && offL == 0 && offR == 0 && nl == lenL && nr == lenR)
                        (keptL, keptR) = (bufL.AsSpan(0, nl).ToArray(), bufR.AsSpan(0, nr).ToArray());
                    if (!hashes) return new PairResult(false, null, null) { LeftBytes = keptL, RightBytes = keptR };
                }
                hl?.Append(bufL.AsSpan(0, nl));
                hr?.Append(bufR.AsSpan(0, nr));
                meter?.Worked(tWork);
                offL += nl;
                offR += nr;
            }
            if (!hashes) return new PairResult(equal, null, null);
            var hashesL = hl!.Finish(Doubt(beforeL, FileStat.Of(lh, pathL), left), hashedAt); // before its twin's: the twin takes its SHA-256
            return new PairResult(equal, hashesL, hr!.Finish(Doubt(beforeR, FileStat.Of(rh, pathR), right), hashedAt))
            { LeftBytes = keptL, RightBytes = keptR };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufL);
            ArrayPool<byte>.Shared.Return(bufR);
        }
    }

    /// <summary>Read until <paramref name="buf"/> is full or the file ends; the bytes read.</summary>
    private static async ValueTask<int> Fill(SafeFileHandle h, Memory<byte> buf, long off, CancellationToken ct)
    {
        int got = 0, n;
        while (got < buf.Length && (n = await RandomAccess.ReadAsync(h, buf[got..], off + got, ct).ConfigureAwait(false)) > 0) got += n;
        return got;
    }

    /// <summary>Whole-file XxHash128 + SHA-256 of one file (streamed, 1 MB chunks), stat-bracketed.</summary>
    public static async Task<FileHashes> HashAsync(FileEntry file, CancellationToken ct = default, Action<int, int>? onChunk = null,
        IoMeter? meter = null)
    {
        long t0 = IoMeter.Now();
        string path = file.FullPath;
        using var h = Open(path);
        long hashedAt = DateTime.UtcNow.Ticks;
        var before = FileStat.Of(h, path);
        meter?.Opened(t0);
        var hasher = new Hasher();
        var buf = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            long off = 0;
            int n;
            long tRead = IoMeter.Now();
            while ((n = await RandomAccess.ReadAsync(h, buf.AsMemory(0, ChunkBytes), off, ct).ConfigureAwait(false)) > 0)
            {
                meter?.Read(tRead, n);
                onChunk?.Invoke(n, n);
                long tWork = IoMeter.Now();
                hasher.Append(buf.AsSpan(0, n));
                meter?.Worked(tWork);
                off += n;
                tRead = IoMeter.Now();
            }
            return hasher.Finish(Doubt(before, FileStat.Of(h, path), file), hashedAt);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>Convenience for callers with only a path (tests, probes): stat-less entry ⇒ never Stable.</summary>
    public static Task<FileHashes> HashAsync(string path, CancellationToken ct = default)
        => HashAsync(new FileEntry("", path, -1, default), ct);

    /// <summary>
    /// <paramref name="file"/>'s hashes, read beside <paramref name="known"/>, a same-length file whose hashes the
    /// ledger already has. While their bytes match only XxHash128 runs (~3% of a SHA-256): every byte equal, and the
    /// known file's live XxHash128 still its recorded one, the known SHA-256 is this file's too, by byte compare, not
    /// by hash. At the first differing chunk this file gets its own SHA-256, from its start. For local trees: the
    /// known file is read again (a network share pays that at link speed, so it hashes alone there).
    /// </summary>
    public static async Task<FileHashes> HashBesideAsync(FileEntry known, ContentId knownId, FileEntry file, CancellationToken ct = default,
        Action<int, int>? onChunk = null, IoMeter? meter = null)
    {
        if (knownId.Sha256.Length == 0 || knownId.XxHash.Length == 0 || known.Length != file.Length)
            return await HashAsync(file, ct, onChunk, meter).ConfigureAwait(false);
        long t0 = IoMeter.Now();
        string path = file.FullPath;
        SafeFileHandle kh;
        try { kh = Open(known.FullPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await HashAsync(file, ct, onChunk, meter).ConfigureAwait(false);
        }
        using var _ = kh;
        using var h = Open(path);
        long hashedAt = DateTime.UtcNow.Ticks;
        var before = FileStat.Of(h, path);
        long len = RandomAccess.GetLength(h);
        meter?.Opened(t0);
        var xx = new XxHash128();
        var xxKnown = new XxHash128();
        IncrementalHash? sha = null; // null while every byte so far is the known file's
        var buf = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var bufK = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            long off = 0;
            while (off < len)
            {
                long tRead = IoMeter.Now();
                var want = (int)Math.Min(ChunkBytes, len - off);
                var tk = sha is null ? Fill(kh, bufK.AsMemory(0, want), off, ct) : ValueTask.FromResult(0);
                int n = await Fill(h, buf.AsMemory(0, want), off, ct).ConfigureAwait(false);
                int nk = await tk.ConfigureAwait(false);
                meter?.Read(tRead, n + nk);
                if (n == 0) break; // shrank under us — the after-stat marks it unstable
                onChunk?.Invoke(n, n + nk);
                long tWork = IoMeter.Now();
                if (sha is null && (nk != n || !buf.AsSpan(0, n).SequenceEqual(bufK.AsSpan(0, n))))
                {
                    // Differs here: this file's own SHA-256, over what was already read and on from here.
                    sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    for (long o = 0; o < off;)
                    {
                        int m = await Fill(h, bufK.AsMemory(0, (int)Math.Min(ChunkBytes, off - o)), o, ct).ConfigureAwait(false);
                        if (m == 0) break;
                        sha.AppendData(bufK.AsSpan(0, m));
                        o += m;
                    }
                }
                xx.Append(buf.AsSpan(0, n));
                if (sha is null) xxKnown.Append(bufK.AsSpan(0, n));
                else sha.AppendData(buf.AsSpan(0, n));
                meter?.Worked(tWork);
                off += n;
            }
            if (sha is null && !string.Equals(Convert.ToHexString(xxKnown.GetCurrentHash()), knownId.XxHash, StringComparison.OrdinalIgnoreCase))
                return await HashAsync(file, ct, onChunk, meter).ConfigureAwait(false); // the known file isn't what the ledger says now
            var digest = sha is null ? knownId.Sha256 : Convert.ToHexString(sha.GetHashAndReset());
            var (doubt, onlyId) = Doubt(before, FileStat.Of(h, path), file);
            return new FileHashes(Convert.ToHexString(xx.GetCurrentHash()), digest, doubt == ReadDoubt.None, hashedAt, doubt)
                { ListingIdDiffers = onlyId };
        }
        finally
        {
            sha?.Dispose();
            ArrayPool<byte>.Shared.Return(buf);
            ArrayPool<byte>.Shared.Return(bufK);
        }
    }

    private static (ReadDoubt, bool OnlyId) Doubt(FileStat before, FileStat after, FileEntry listing)
    {
        if (before != after) return (ReadDoubt.Changed, false);
        // Size, mtime and ChangeTime still have to agree: only the id is let go, and only for a read that held still.
        bool onlyId = after.OnlyIdDiffers(listing);
        if (!onlyId && !after.Matches(listing)) return (ReadDoubt.Listing, false);
        return (after.ChangeUtcTicks == 0 ? ReadDoubt.NoChangeTime : ReadDoubt.None, onlyId);
    }

    /// <summary>
    /// One file's XxHash128 + SHA-256. SHA-256 is nearly all of it (~250 MB/s a core without SHA instructions,
    /// XxHash128 ~8 GB/s), so the second file of a pair is a <see cref="Twin"/>: while its bytes are the first's, the
    /// first's SHA-256 stands for both, forked at the first chunk that differs (<see cref="Part"/>). An identical
    /// pair, the usual case, costs one SHA-256 pass, not two.
    /// </summary>
    private sealed class Hasher
    {
        private readonly XxHash128 _xx = new();
        private IncrementalHash? _sha; // null while a twin: the first file's stands for it
        private Hasher? _twin;
        private byte[]? _digest;

        public Hasher() => _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private Hasher(Hasher twin) => _twin = twin;

        /// <summary>The other file of a same-length pair, appended chunk for chunk with this one; null where the runtime
        /// can't fork a SHA-256 (IncrementalHash.Clone is .NET 9+: the exes run on 10, a net8 host hashes both).</summary>
        public Hasher? Twin() => s_clone is null ? null : new Hasher(this);

        /// <summary>A twin's next chunk differs from the first file's: its own SHA-256 from here, forked from theirs.</summary>
        public void Part()
        {
            if (_twin is null) return;
            _sha = s_clone!(_twin._sha!);
            _twin = null;
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            _xx.Append(data);
            _sha?.AppendData(data);
        }

        public FileHashes Finish((ReadDoubt Doubt, bool OnlyId) d, long hashedAt)
        {
            _digest = _sha?.GetHashAndReset() ?? _twin!._digest!;
            _sha?.Dispose();
            return new FileHashes(Convert.ToHexString(_xx.GetCurrentHash()), Convert.ToHexString(_digest), d.Doubt == ReadDoubt.None, hashedAt, d.Doubt)
                { ListingIdDiffers = d.OnlyId };
        }

        private static readonly Func<IncrementalHash, IncrementalHash>? s_clone =
            typeof(IncrementalHash).GetMethod("Clone", Type.EmptyTypes)?.CreateDelegate<Func<IncrementalHash, IncrementalHash>>();
    }

    private static SafeFileHandle Open(string path)
        => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
