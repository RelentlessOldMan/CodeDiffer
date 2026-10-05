using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CodeDiffer.Core.Ledger;

/// <summary>One file's cached identity: size + mtime (UTC ticks) + XxHash128 (32 uppercase hex chars).</summary>
public readonly record struct LedgerEntry(long Size, long MTimeTicks, string Hash);

/// <summary>A ledger read off disk: entries by repo-relative path, plus when it was last written (UTC ticks).</summary>
public sealed record LedgerSnapshot(IReadOnlyDictionary<string, LedgerEntry> Entries, long WrittenUtcTicks);

/// <summary>
/// The shared hash-ledger on-disk format, byte-compatible with CodeCompass's change ledger (agreed with
/// CodeCompass 2026-10-05; CodeCompass owns the spec). A ledger directory holds:
/// <list type="bullet">
/// <item><c>snapshot.manifest</c> — text: line 1 = current base file name, line 2 = next base number.</item>
/// <item><c>snapshot-NNNNNNNN.base</c> — "CCSN" v1 columnar base, little-endian: magic u32, version i32,
///   count i32, then 5 i64 section offsets (path offsets[count+1] i64, UTF-8 path blob ordinal-sorted,
///   sizes i64, mtimes i64 UTC ticks, raw 16-byte XxHash128 in canonical byte order).</item>
/// <item><c>snapshot.journal</c> (optional) — "CSNJ": BinaryWriter (path, size, mtime, hexHash)* then removed paths*.</item>
/// </list>
/// Version 1 IMPLIES XxHash128; any algorithm change bumps the version, and a reader that doesn't know
/// the magic/version ignores the ledger (re-hashes) — it never guesses. Paths are repo-relative, '/'
/// separators, original case, ordinal sort, UTF-8 exactly as walked (no normalization).
///
/// Reads open with FileShare.ReadWrite|Delete, read, and close — no long-lived mmap — so a CodeCompass
/// compaction/GC never trips over a CodeDiffer reader.
/// </summary>
public static class LedgerFormat
{
    internal const uint BaseMagic = 0x4E535343;    // "CCSN"
    internal const uint JournalMagic = 0x4A4E5343; // "CSNJ"
    internal const int Version = 1;
    internal const int HeaderSize = 12 + 8 * 5;
    internal const int HashBytes = 16;
    internal const string ManifestName = "snapshot.manifest";
    internal const string JournalName = "snapshot.journal";

    /// <summary>CodeCompass's "binary file, not hashed" sentinel — a reader treats it as NO hash.</summary>
    public const string BinarySentinel = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF";

    /// <summary>Per-root directory key: first 16 hex (uppercase) of SHA-256(lowercased normalized full path).
    /// NOTE: a UNC path and a mapped drive letter for the same folder give DIFFERENT keys.</summary>
    public static string RootKey(string root)
    {
        var norm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm)))[..16];
    }

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

                var entries = ReadBase(basePath);
                if (entries is null) return null;
                ApplyJournal(Path.Combine(dir, JournalName), entries);
                return new LedgerSnapshot(entries, LatestWriteTicks(dir));
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            return null; // unreadable ledger ⇒ no cache, never a wrong answer
        }
    }

    /// <summary>
    /// Write a complete ledger: a fresh base (next number), then atomically swap the manifest to name it,
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

        long offsOff = HeaderSize;
        long blobOff = offsOff + (long)(count + 1) * 8;
        long sizesOff = blobOff + blobLen;
        long mtimesOff = sizesOff + (long)count * 8;
        long hashesOff = mtimesOff + (long)count * 8;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true); // BinaryWriter is little-endian
        w.Write(BaseMagic);
        w.Write(Version);
        w.Write(count);
        w.Write(offsOff);
        w.Write(blobOff);
        w.Write(sizesOff);
        w.Write(mtimesOff);
        w.Write(hashesOff);

        long acc = 0;
        foreach (var b in pathBytes) { w.Write(acc); acc += b.Length; }
        w.Write(acc);
        foreach (var b in pathBytes) w.Write(b);
        foreach (var e in sorted) w.Write(e.Value.Size);
        foreach (var e in sorted) w.Write(e.Value.MTimeTicks);
        foreach (var e in sorted) w.Write(HashToBytes(e.Value.Hash));
        w.Flush();
        fs.Flush(flushToDisk: true); // durable before the manifest names it
    }

    private static Dictionary<string, LedgerEntry>? ReadBase(string path)
    {
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }
        var s = data.AsSpan();
        if (s.Length < HeaderSize) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(s) != BaseMagic) return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(s[4..]) != Version) return null; // unknown version ⇒ ignore
        int count = BinaryPrimitives.ReadInt32LittleEndian(s[8..]);
        long offsOff = BinaryPrimitives.ReadInt64LittleEndian(s[12..]);
        long blobOff = BinaryPrimitives.ReadInt64LittleEndian(s[20..]);
        long sizesOff = BinaryPrimitives.ReadInt64LittleEndian(s[28..]);
        long mtimesOff = BinaryPrimitives.ReadInt64LittleEndian(s[36..]);
        long hashesOff = BinaryPrimitives.ReadInt64LittleEndian(s[44..]);
        if (count < 0 || blobOff - offsOff < ((long)count + 1) * 8 || mtimesOff - sizesOff < (long)count * 8 ||
            hashesOff - mtimesOff < (long)count * 8 || sizesOff < blobOff || hashesOff + (long)count * HashBytes > s.Length)
            return null; // structurally corrupt ⇒ ignore

        var map = new Dictionary<string, LedgerEntry>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            long o0 = BinaryPrimitives.ReadInt64LittleEndian(s[(int)(offsOff + (long)i * 8)..]);
            long o1 = BinaryPrimitives.ReadInt64LittleEndian(s[(int)(offsOff + (long)(i + 1) * 8)..]);
            if (o0 < 0 || o1 < o0 || blobOff + o1 > sizesOff) return null;
            var p = Encoding.UTF8.GetString(s.Slice((int)(blobOff + o0), (int)(o1 - o0)));
            long size = BinaryPrimitives.ReadInt64LittleEndian(s[(int)(sizesOff + (long)i * 8)..]);
            long mtime = BinaryPrimitives.ReadInt64LittleEndian(s[(int)(mtimesOff + (long)i * 8)..]);
            var hash = Convert.ToHexString(s.Slice((int)(hashesOff + (long)i * HashBytes), HashBytes));
            map[p] = new LedgerEntry(size, mtime, hash);
        }
        return map;
    }

    /// <summary>Fold a CodeCompass journal (recent upserts + removals) over the base. A torn journal is
    /// dropped whole — those files just get re-hashed, which is always safe.</summary>
    private static void ApplyJournal(string journal, Dictionary<string, LedgerEntry> entries)
    {
        if (!File.Exists(journal)) return;
        var upserts = new List<(string, LedgerEntry)>();
        var removes = new List<string>();
        try
        {
            using var fs = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new BinaryReader(fs, Encoding.UTF8);
            if (r.ReadUInt32() != JournalMagic) return;
            int sets = r.ReadInt32();
            for (int i = 0; i < sets; i++)
                upserts.Add((r.ReadString(), new LedgerEntry(r.ReadInt64(), r.ReadInt64(), r.ReadString())));
            int rems = r.ReadInt32();
            for (int i = 0; i < rems; i++) removes.Add(r.ReadString());
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or FormatException)
        {
            return;
        }
        foreach (var (p, e) in upserts) entries[p] = e;
        foreach (var p in removes) entries.Remove(p);
    }

    /// <summary>The ledger's write time = newest last-write (UTC) of its snapshot* files (journal appends
    /// count). 0 = unknown ⇒ nothing in it is trusted.</summary>
    private static long LatestWriteTicks(string dir)
    {
        long max = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "snapshot*"))
            max = Math.Max(max, File.GetLastWriteTimeUtc(f).Ticks);
        return max;
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

    private static byte[] HashToBytes(string hex)
    {
        try { return hex.Length == HashBytes * 2 ? Convert.FromHexString(hex) : new byte[HashBytes]; }
        catch (FormatException) { return new byte[HashBytes]; }
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
