using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.DiffTruth;

/// <summary>
/// Computes <c>_meta.diffTruthSha</c> from a delta manifest, using the canonical byte-form LOCKED
/// across CodeSpawner / CodeCompass / CodeCarver (identical to <c>indirectTruthSha</c>): ordinal
/// (byte-wise UTF-8) sort; US=0x1F between fields, RS=0x1E between records (trailing RS after each),
/// GS=0x1D between sections; dedup-then-sort each section; ALL sections always emitted (empty = zero
/// records, the GS boundaries remain); sha256 lowercase hex over the UTF-8 bytes.
///
/// Four sections, THIS fixed order:
///   1. modified-files : path · reason · oldSha · newSha · oldSize · newSize
///   2. hunks-explicit : path · op · oldStart · oldLines · newStart · newLines
///   3. hunks-run      : path · op · stride · rangeStart · rangeEnd · perHunk
///   4. renames        : from · to · similarityMilli
///
/// The acceptance gate is reproducing CodeSpawner's shipped digest-selftest golden vector; until that
/// lands, <see cref="DiffTruthDigestTests"/> guards the canonicalization mechanics (determinism,
/// order-independence, the all-empty vector).
/// </summary>
public static class DiffTruthDigest
{
    internal const byte US = 0x1F; // unit separator — between fields of one record
    internal const byte RS = 0x1E; // record separator — after each record
    internal const byte GS = 0x1D; // group separator — between sections

    public static string Compute(DeltaManifest manifest)
    {
        var modifiedFiles = new List<byte[]>();
        var hunksExplicit = new List<byte[]>();
        var hunksRun = new List<byte[]>();
        var renames = new List<byte[]>();

        foreach (var f in manifest.Modified)
        {
            modifiedFiles.Add(Record(f.Path, CanonicalTokens.Token(f.Reason), f.OldSha, f.NewSha, Dec(f.OldSize), Dec(f.NewSize)));
            foreach (var h in f.Hunks)
                hunksExplicit.Add(Record(f.Path, CanonicalTokens.Token(h.Op), Dec(h.OldStart), Dec(h.OldLines), Dec(h.NewStart), Dec(h.NewLines)));
            foreach (var r in f.RunHunks)
                hunksRun.Add(Record(f.Path, CanonicalTokens.Token(r.Op), Dec(r.Stride), Dec(r.RangeStart), Dec(r.RangeEnd), Dec(r.PerHunk)));
        }

        foreach (var r in manifest.Renamed)
            renames.Add(Record(r.From, r.To, Dec(r.SimilarityMilli)));

        using var input = new MemoryStream();
        WriteSection(input, modifiedFiles);
        input.WriteByte(GS);
        WriteSection(input, hunksExplicit);
        input.WriteByte(GS);
        WriteSection(input, hunksRun);
        input.WriteByte(GS);
        WriteSection(input, renames);

        return Convert.ToHexString(SHA256.HashData(input.ToArray())).ToLowerInvariant();
    }

    private static string Dec(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static byte[] Record(params string[] fields)
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
