using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.DiffTruth;

/// <summary>
/// Computes <c>_meta.conflictTruthSha</c> for the 3-way conflict artifact via the shared
/// <see cref="CanonicalDigest"/> form — kept separate from <c>diffTruthSha</c> exactly as CodeSpawner
/// keeps them apart. Two sections, THIS fixed order:
///   1. conflicts-3way : path · baseStart · baseLines · v1Op · v1NewStart · v1NewLines · v2Op · v2NewStart · v2NewLines
///   2. merged-clean   : path · side · op · oldStart · oldLines · newStart · newLines   (side ∈ v1|v2)
///
/// Gate: reproduce CodeSpawner's frozen golden vector 68cd14ac (see <see cref="ConflictDigestTests"/>).
/// </summary>
public static class ConflictDigest
{
    public static string Compute(IReadOnlyList<Conflict> conflicts, IReadOnlyList<CleanMerge> clean)
    {
        var conflictRecords = new List<byte[]>(conflicts.Count);
        foreach (var x in conflicts)
            conflictRecords.Add(CanonicalDigest.Record(
                x.Path, CanonicalDigest.Dec(x.BaseStart), CanonicalDigest.Dec(x.BaseLines),
                CanonicalTokens.Token(x.V1Op), CanonicalDigest.Dec(x.V1NewStart), CanonicalDigest.Dec(x.V1NewLines),
                CanonicalTokens.Token(x.V2Op), CanonicalDigest.Dec(x.V2NewStart), CanonicalDigest.Dec(x.V2NewLines)));

        var cleanRecords = new List<byte[]>(clean.Count);
        foreach (var x in clean)
            cleanRecords.Add(CanonicalDigest.Record(
                x.Path, x.Side, CanonicalTokens.Token(x.Op),
                CanonicalDigest.Dec(x.OldStart), CanonicalDigest.Dec(x.OldLines),
                CanonicalDigest.Dec(x.NewStart), CanonicalDigest.Dec(x.NewLines)));

        return CanonicalDigest.Hash([conflictRecords, cleanRecords]);
    }

    public static string Compute(ConflictManifest manifest) => Compute(manifest.Conflicts, manifest.CleanMerges);
}
