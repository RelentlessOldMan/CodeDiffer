using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeDiffer.Core.DiffTruth;

/// <summary>
/// The shared canonical byte-form for the component digests (diffTruthSha, conflictTruthSha), LOCKED
/// with CodeSpawner/CodeCompass/CodeCarver: US(0x1F) between fields, RS(0x1E) after each record,
/// GS(0x1D) between sections; dedup-then-ordinal-sort each section; ALL sections always emitted
/// (empty = zero records, the GS boundaries remain); sha256 over the UTF-8 bytes, lowercase hex.
/// Both digests build their sections as records and hash them through here, so the discipline is
/// defined once and cannot diverge between the two.
/// </summary>
internal static class CanonicalDigest
{
    internal const byte US = 0x1F; // unit — between fields
    internal const byte RS = 0x1E; // record — after each record
    internal const byte GS = 0x1D; // group — between sections

    public static string Dec(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static byte[] Record(params string[] fields)
    {
        int size = Math.Max(0, fields.Length - 1);
        foreach (var f in fields) size += Encoding.UTF8.GetByteCount(f);
        var rec = new byte[size];
        int at = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) rec[at++] = US;
            at += Encoding.UTF8.GetBytes(fields[i], rec.AsSpan(at));
        }
        return rec;
    }

    /// <summary>Hash the sections in order: each dedup-then-ordinal-sorted, records RS-terminated, GS between sections.</summary>
    /// <remarks>Hashed as it goes: the canonical form was built whole in a MemoryStream and copied once more to hash it
    /// (2.2M hunk records: 635 MB peak, now 276 MB; over 2 GB it could not be built at all).</remarks>
    public static string Hash(IReadOnlyList<List<byte[]>> sections)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int s = 0; s < sections.Count; s++)
        {
            if (s > 0) sha.AppendData([GS]);
            WriteSection(sha, sections[s]);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteSection(IncrementalHash dest, List<byte[]> records)
    {
        records.Sort(CompareBytesOrdinal);
        byte[]? prev = null;
        foreach (var rec in records)
        {
            if (prev is not null && CompareBytesOrdinal(prev, rec) == 0) continue; // dedup identical records
            dest.AppendData(rec);
            dest.AppendData([RS]);
            prev = rec;
        }
    }

    /// <summary>Byte-wise (unsigned) lexicographic order; shorter-is-less on a shared prefix.</summary>
    internal static int CompareBytesOrdinal(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = a[i].CompareTo(b[i]);
            if (d != 0) return d;
        }
        return a.Length.CompareTo(b.Length);
    }
}
