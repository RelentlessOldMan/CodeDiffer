namespace CodeDiffer.Core.Giant;

/// <summary>
/// The 256-entry gear table for content-defined chunking (FastCDC-style). Each byte value maps to a
/// 64-bit constant; the rolling fingerprint is <c>fp = (fp &lt;&lt; 1) + Gear[b]</c>. The table is generated
/// deterministically from a fixed seed (SplitMix64) so chunk boundaries — and therefore block hashes and
/// the whole giant-file diff — are byte-identical on every machine and every run.
/// </summary>
internal static class GearHash
{
    public static readonly ulong[] Table = Build();

    private static ulong[] Build()
    {
        var table = new ulong[256];
        ulong state = 0x9E3779B97F4A7C15UL; // fixed seed — determinism is part of the contract
        for (int i = 0; i < 256; i++)
            table[i] = SplitMix64(ref state);
        return table;
    }

    private static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
