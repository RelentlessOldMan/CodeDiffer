using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CodeDiffer.Core.Ledger;

/// <summary>
/// One file's cached identity + hashes. Hashes are uppercase hex; "" = absent. ChangeTicks/FileId/HashedAt
/// 0 = unknown. A v1 (CodeCompass) entry carries only Size/MTime/XxHash — and is never trusted (see
/// <see cref="HashCache"/>): without ChangeTime it can't rule out a "restore the mtime" edit.
/// </summary>
public readonly record struct LedgerEntry(
    long Size,
    long MTimeTicks,
    string XxHash,
    long ChangeTicks = 0,
    long FileId = 0,
    long HashedAtTicks = 0,
    string Sha256 = "");

/// <summary>A ledger read off disk: entries by repo-relative path, and the base format version (1 or 2).</summary>
public sealed record LedgerSnapshot(IReadOnlyDictionary<string, LedgerEntry> Entries, int Version);

/// <summary>
/// The shared hash-ledger on-disk format (agreed with CodeCompass 2026-10-05; CodeCompass owns the spec,
/// docs/hash-ledger-format.md in its repo). A ledger directory holds:
/// <list type="bullet">
/// <item><c>snapshot.manifest</c> — text: line 1 = current base file name, line 2 = next base number.</item>
/// <item><c>snapshot-NNNNNNNN.base</c> — magic 0x4E535343, little-endian. v1: version 1, count, 5 i64 section
///   offsets (path offsets[count+1] i64, UTF-8 path blob ordinal-sorted, sizes i64, mtimes i64 UTC ticks,
///   XxHash128 16 B canonical bytes). v2: version 2, count, 9 offsets — v1's five in the same order, then
///   changeTimes i64, fileIds i64, hashedAt i64 (local UTC ticks just before the read), SHA-256 32 B.</item>
/// <item><c>snapshot.journal</c> (optional) — v1 magic "CSNJ" (path, size, mtime, xxhHex)*; v2 magic "CSN2"
///   (… + changeTime, fileId, hashedAt, shaHex); then removed paths.</item>
/// </list>
/// A hash of all 0x00 (unknown) or all 0xFF (CodeCompass's binary sentinel) is "absent". A reader that doesn't
/// know the magic/version ignores the ledger — it never guesses. Paths are repo-relative, '/' separators,
/// original case, ordinal sort, UTF-8 exactly as walked.
///
/// Reads open with FileShare.ReadWrite|Delete, read, and close — no long-lived mmap — so a CodeCompass
/// compaction/GC never trips over a CodeDiffer reader.
/// </summary>
public static class LedgerFormat
{
    internal const uint BaseMagic = 0x4E535343;      // bytes "CSSN"
    internal const uint JournalMagicV1 = 0x4A4E5343; // "CSNJ"
    internal const uint JournalMagicV2 = 0x324E5343; // "CSN2"
    internal const int WriteVersion = 2;
    internal const int XxBytes = 16, ShaBytes = 32;
    internal const string ManifestName = "snapshot.manifest";
    internal const string JournalName = "snapshot.journal";

    /// <summary>Per-root directory key: first 16 hex (uppercase) of SHA-256(lowercased normalized full path).
    /// NOTE: a UNC path and a mapped drive letter for the same folder give DIFFERENT keys.</summary>
    public static string RootKey(string root)
    {
        var norm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm)))[..16];
    }

    /// <summary>"" for an absent hash (all-zero = unknown, all-FF = CodeCompass binary sentinel).</summary>
    public static string HashOrAbsent(string hex)
        => hex.Length == 0 || hex.All(c => c == '0') || hex.All(c => c is 'F' or 'f') ? "" : hex.ToUpperInvariant();

    /// <summary>Read a ledger directory; null if absent, unrecognized (magic/version), or corrupt.</summary>
    public static LedgerSnapshot? TryRead(string dir)
    {
        try
        {
            var manifest = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifest)) return null;
            // The manifest may be swapped by a compaction between our reads: if the base it names has
            // vanished, re-read the manifest once.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var lines = ReadAllTextShared(manifest).Split('\n', StringSplitOptions.TrimEntries);
                if (lines.Length < 1 || !IsBareFileName(lines[0])) return null;
                var basePath = Path.Combine(dir, lines[0]);
                if (!File.Exists(basePath)) continue;

                var (entries, version) = ReadBase(basePath);
                if (entries is null) return null;
                if (!ApplyJournal(Path.Combine(dir, JournalName), entries)) return null;
                return new LedgerSnapshot(entries, version);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            return null; // unreadable ledger ⇒ no cache, never a wrong answer
        }
    }

    /// <summary>Base format version a ledger directory holds (reads only the manifest + 8 header bytes), 0 if none.</summary>
    public static int PeekVersion(string dir)
    {
        try
        {
            var manifest = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifest)) return 0;
            var first = ReadAllTextShared(manifest).Split('\n', StringSplitOptions.TrimEntries)[0];
            if (!IsBareFileName(first)) return 0;
            using var fs = new FileStream(Path.Combine(dir, first), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> h = stackalloc byte[8];
            fs.ReadExactly(h);
            return BinaryPrimitives.ReadUInt32LittleEndian(h) == BaseMagic ? BinaryPrimitives.ReadInt32LittleEndian(h[4..]) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Write a complete v2 ledger: a fresh base (next number), then atomically swap the manifest to name it,
    /// drop any journal, and best-effort delete older bases (a locked one is retried next write).
    /// </summary>
    public static void Write(string dir, IEnumerable<KeyValuePair<string, LedgerEntry>> entries)
    {
        Directory.CreateDirectory(dir);
        var sorted = entries.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
        int next = NextBaseNumber(dir);
        var baseName = $"snapshot-{next:D8}.base";
        WriteBase(Path.Combine(dir, baseName), sorted);

        var manifest = Path.Combine(dir, ManifestName);
        var tmp = manifest + ".tmp";
        File.WriteAllText(tmp, $"{baseName}\n{next + 1}\n");
        File.Move(tmp, manifest, overwrite: true);
        TryDelete(Path.Combine(dir, JournalName));

        foreach (var old in Directory.EnumerateFiles(dir, "snapshot-*.base"))
            if (!string.Equals(Path.GetFileName(old), baseName, StringComparison.OrdinalIgnoreCase))
                TryDelete(old);
    }

    private static void WriteBase(string path, List<KeyValuePair<string, LedgerEntry>> sorted)
    {
        int count = sorted.Count;
        var pathBytes = sorted.Select(e => Encoding.UTF8.GetBytes(e.Key)).ToArray();
        long blobLen = pathBytes.Sum(b => (long)b.Length);

        const int headerSize = 12 + 8 * 9;
        long offsOff = headerSize;
        long blobOff = offsOff + (long)(count + 1) * 8;
        long sizesOff = blobOff + blobLen;
        long mtimesOff = sizesOff + (long)count * 8;
        long xxOff = mtimesOff + (long)count * 8;
        long ctimesOff = xxOff + (long)count * XxBytes;
        long idsOff = ctimesOff + (long)count * 8;
        long hashedAtOff = idsOff + (long)count * 8;
        long shaOff = hashedAtOff + (long)count * 8;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true); // BinaryWriter is little-endian
        w.Write(BaseMagic);
        w.Write(WriteVersion);
        w.Write(count);
        foreach (var off in new[] { offsOff, blobOff, sizesOff, mtimesOff, xxOff, ctimesOff, idsOff, hashedAtOff, shaOff })
            w.Write(off);

        long acc = 0;
        foreach (var b in pathBytes) { w.Write(acc); acc += b.Length; }
        w.Write(acc);
        foreach (var b in pathBytes) w.Write(b);
        foreach (var e in sorted) w.Write(e.Value.Size);
        foreach (var e in sorted) w.Write(e.Value.MTimeTicks);
        foreach (var e in sorted) w.Write(HexToBytes(e.Value.XxHash, XxBytes));
        foreach (var e in sorted) w.Write(e.Value.ChangeTicks);
        foreach (var e in sorted) w.Write(e.Value.FileId);
        foreach (var e in sorted) w.Write(e.Value.HashedAtTicks);
        foreach (var e in sorted) w.Write(HexToBytes(e.Value.Sha256, ShaBytes));
        w.Flush();
        fs.Flush(flushToDisk: true); // durable before the manifest names it
    }

    private static (Dictionary<string, LedgerEntry>?, int) ReadBase(string path)
    {
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }
        var s = data.AsSpan();
        if (s.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(s) != BaseMagic) return (null, 0);
        int version = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
        int sections = version switch { 1 => 5, 2 => 9, _ => 0 };
        if (sections == 0) return (null, 0); // unknown version ⇒ ignore, never guess
        int count = BinaryPrimitives.ReadInt32LittleEndian(s[8..]);
        if (s.Length < 12 + 8 * sections || count < 0) return (null, 0);

        var off = new long[sections];
        for (int i = 0; i < sections; i++) off[i] = BinaryPrimitives.ReadInt64LittleEndian(s[(12 + 8 * i)..]);
        // Section widths in order; the path blob (index 1) is variable and only bounded by its neighbours.
        long[] widths = version == 1 ? [8, 0, 8, 8, XxBytes] : [8, 0, 8, 8, XxBytes, 8, 8, 8, ShaBytes];
        for (int i = 0; i < sections; i++)
        {
            long need = i == 0 ? ((long)count + 1) * 8 : (long)count * widths[i];
            long end = i + 1 < sections ? off[i + 1] : s.Length;
            if (off[i] < 12 + 8 * sections || off[i] > end || end - off[i] < need || end > s.Length) return (null, 0);
        }

        long blobOff = off[1], blobEnd = off[2];
        var map = new Dictionary<string, LedgerEntry>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            long o0 = I64(s, off[0] + (long)i * 8), o1 = I64(s, off[0] + (long)(i + 1) * 8);
            if (o0 < 0 || o1 < o0 || blobOff + o1 > blobEnd) return (null, 0);
            var p = Encoding.UTF8.GetString(s.Slice((int)(blobOff + o0), (int)(o1 - o0)));
            var xx = HashOrAbsent(Convert.ToHexString(s.Slice((int)(off[4] + (long)i * XxBytes), XxBytes)));
            map[p] = version == 1
                ? new LedgerEntry(I64(s, off[2] + (long)i * 8), I64(s, off[3] + (long)i * 8), xx)
                : new LedgerEntry(I64(s, off[2] + (long)i * 8), I64(s, off[3] + (long)i * 8), xx,
                    I64(s, off[5] + (long)i * 8), I64(s, off[6] + (long)i * 8), I64(s, off[7] + (long)i * 8),
                    HashOrAbsent(Convert.ToHexString(s.Slice((int)(off[8] + (long)i * ShaBytes), ShaBytes))));
        }
        return (map, version);
    }

    private static long I64(ReadOnlySpan<byte> s, long at) => BinaryPrimitives.ReadInt64LittleEndian(s[(int)at..]);

    /// <summary>Fold a journal (v1 or v2) over the base. A torn journal is dropped whole — those files are just
    /// re-hashed, which is always safe. An UNKNOWN journal magic fails the whole ledger (never guess).</summary>
    private static bool ApplyJournal(string journal, Dictionary<string, LedgerEntry> entries)
    {
        if (!File.Exists(journal)) return true;
        var upserts = new List<(string, LedgerEntry)>();
        var removes = new List<string>();
        try
        {
            using var fs = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new BinaryReader(fs, Encoding.UTF8);
            uint magic = r.ReadUInt32();
            if (magic is not (JournalMagicV1 or JournalMagicV2)) return false;
            int sets = r.ReadInt32();
            for (int i = 0; i < sets; i++)
            {
                var p = r.ReadString();
                long size = r.ReadInt64(), mtime = r.ReadInt64();
                var xx = HashOrAbsent(r.ReadString());
                upserts.Add((p, magic == JournalMagicV1
                    ? new LedgerEntry(size, mtime, xx)
                    : new LedgerEntry(size, mtime, xx, r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), HashOrAbsent(r.ReadString()))));
            }
            int rems = r.ReadInt32();
            for (int i = 0; i < rems; i++) removes.Add(r.ReadString());
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or FormatException)
        {
            return true; // torn journal: keep the base, lose only recent entries
        }
        foreach (var (p, e) in upserts) entries[p] = e;
        foreach (var p in removes) entries.Remove(p);
        return true;
    }

    private static int NextBaseNumber(string dir)
    {
        int next = 0;
        if (!Directory.Exists(dir)) return 0;
        foreach (var f in Directory.EnumerateFiles(dir, "snapshot-*.base"))
        {
            var stem = Path.GetFileNameWithoutExtension(f)["snapshot-".Length..];
            if (int.TryParse(stem, out var n)) next = Math.Max(next, n + 1);
        }
        return next;
    }

    private static byte[] HexToBytes(string hex, int len)
    {
        try { return hex.Length == len * 2 ? Convert.FromHexString(hex) : new byte[len]; }
        catch (FormatException) { return new byte[len]; }
    }

    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Replace("\r", "");
    }

    private static bool IsBareFileName(string name)
        => name.Length > 0 && name.IndexOfAny(['/', '\\', ':']) < 0 && name != "." && name != "..";

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
