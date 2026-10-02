using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.DiffTruth;

/// <summary>
/// Computes <c>_meta.diffTruthSha</c> from a delta manifest via the shared <see cref="CanonicalDigest"/>
/// form (LOCKED with CodeSpawner — a reason-flip, sha-flip, hunk shift, or rename change trips it).
///
/// Four sections, THIS fixed order:
///   1. modified-files : path · reason · oldSha · newSha · oldSize · newSize
///   2. hunks-explicit : path · op · oldStart · oldLines · newStart · newLines
///   3. hunks-run      : path · op · stride · rangeStart · rangeEnd · perHunk
///   4. renames        : from · to · similarityMilli
///
/// Gate: reproduce CodeSpawner's frozen golden vector 66c7e625 (see <see cref="DiffTruthDigestTests"/>).
/// </summary>
public static class DiffTruthDigest
{
    public static string Compute(DeltaManifest manifest)
    {
        var modifiedFiles = new List<byte[]>();
        var hunksExplicit = new List<byte[]>();
        var hunksRun = new List<byte[]>();
        var renames = new List<byte[]>();

        foreach (var f in manifest.Modified)
        {
            modifiedFiles.Add(CanonicalDigest.Record(
                f.Path, CanonicalTokens.Token(f.Reason), f.OldSha, f.NewSha,
                CanonicalDigest.Dec(f.OldSize), CanonicalDigest.Dec(f.NewSize)));

            foreach (var h in f.Hunks)
                hunksExplicit.Add(CanonicalDigest.Record(
                    f.Path, CanonicalTokens.Token(h.Op),
                    CanonicalDigest.Dec(h.OldStart), CanonicalDigest.Dec(h.OldLines),
                    CanonicalDigest.Dec(h.NewStart), CanonicalDigest.Dec(h.NewLines)));

            foreach (var r in f.RunHunks)
                hunksRun.Add(CanonicalDigest.Record(
                    f.Path, CanonicalTokens.Token(r.Op),
                    CanonicalDigest.Dec(r.Stride), CanonicalDigest.Dec(r.RangeStart),
                    CanonicalDigest.Dec(r.RangeEnd), CanonicalDigest.Dec(r.PerHunk)));
        }

        foreach (var r in manifest.Renamed)
            renames.Add(CanonicalDigest.Record(r.From, r.To, CanonicalDigest.Dec(r.SimilarityMilli)));

        return CanonicalDigest.Hash([modifiedFiles, hunksExplicit, hunksRun, renames]);
    }
}
