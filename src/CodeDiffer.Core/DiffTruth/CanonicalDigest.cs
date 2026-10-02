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
        using var ms = new MemoryStream();
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) ms.WriteByte(US);
            var bytes = Encoding.UTF8.GetBytes(fields[i]);
            ms.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    /// <summary>Hash the sections in order: each dedup-then-ordinal-sorted, records RS-terminated, GS between sections.</summary>
    public static string Hash(IReadOnlyList<List<byte[]>> sections)
    {
        using var input = new MemoryStream();
        for (int s = 0; s < sections.Count; s++)
        {
            if (s > 0) input.WriteByte(GS);
            WriteSection(input, sections[s]);
        }
        return Convert.ToHexString(SHA256.HashData(input.ToArray())).ToLowerInvariant();
    }

    private static void WriteSection(Stream dest, List<byte[]> records)
    {
        records.Sort(CompareBytesOrdinal);
        byte[]? prev = null;
        foreach (var rec in records)
        {
            if (prev is not null && CompareBytesOrdinal(prev, rec) == 0) continue; // dedup identical records
            dest.Write(rec, 0, rec.Length);
            dest.WriteByte(RS);
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
