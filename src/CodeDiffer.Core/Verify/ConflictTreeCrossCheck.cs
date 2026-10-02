using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Verify;

/// <summary>Per-file 3-way reconstruction outcome.</summary>
public sealed record FileMergeCheck(string Path, bool V1Reconstructs, bool V2Reconstructs);

public sealed record ConflictCrossCheckResult(IReadOnlyList<FileMergeCheck> Files)
{
    public int FilesChecked => Files.Count;
    public int V1Reconstructed => Files.Count(f => f.V1Reconstructs);
    public int V2Reconstructed => Files.Count(f => f.V2Reconstructs);

    /// <summary>Pass iff every file's v1 AND v2 sides rebuild from the manifest's own coordinates.</summary>
    public bool Ok => Files.All(f => f.V1Reconstructs && f.V2Reconstructs);
}

/// <summary>
/// The 3-way correctness gate, and — as in the 2-way path — it is <b>reconstruction</b>, NOT digest/coord
/// equality against an independent diff. A diff over a run of byte-identical lines is not unique: the
/// fixture appends a marker to one line in a repeated block, so a minimal differ (CodeDiffer's Myers) may
/// place the edit at an equally-valid but different coordinate than CodeSpawner's edit-POSITIONAL truth.
/// Re-deriving the decomposition and comparing digests therefore fails for a correct engine.
///
/// So we verify what actually must hold: the manifest's OWN per-side operations, applied to the base,
/// rebuild each variant tree byte-for-byte. Collect every v1-side op (each conflict's v1 hunk + every
/// mergedClean with side "v1") and reconstruct V1 from B; collect every v2-side op and reconstruct V2.
/// If both equal the on-disk trees, CodeSpawner's conflict/clean coordinates are byte-consistent with the
/// trees — the claim a consumer needs. (The conflictTruthSha is reproduced separately, straight from the
/// parsed manifest; that check lives in <see cref="DeltaVerifier.VerifyConflictDigest"/>.)
///
/// Caveat: an identical-edit-both-sides region is recorded once with canonical side "v1", so it is absent
/// from the v2 op set; such a region's v2 reconstruction is validated by the trees agreeing there
/// (B_v1 ≡ B_v2), handled when the edge fixture that exercises it lands. The overlap-0.5 fixture has none
/// (per-side markers differ, so an overlap is always a conflict).
/// </summary>
public static class ConflictTreeCrossCheck
{
    public static ConflictCrossCheckResult Run(
        string baseDir, string v1Dir, string v2Dir, ConflictManifest manifest)
    {
        // Gather each side's hunks per path from the manifest's own records.
        var v1ByPath = new Dictionary<string, List<Hunk>>(StringComparer.Ordinal);
        var v2ByPath = new Dictionary<string, List<Hunk>>(StringComparer.Ordinal);

        void Add(Dictionary<string, List<Hunk>> map, string path, Hunk h)
        {
            if (!map.TryGetValue(path, out var list)) map[path] = list = [];
            list.Add(h);
        }

        foreach (var c in manifest.Conflicts)
        {
            Add(v1ByPath, c.Path, new Hunk(c.V1Op, c.BaseStart, c.BaseLines, c.V1NewStart, c.V1NewLines));
            Add(v2ByPath, c.Path, new Hunk(c.V2Op, c.BaseStart, c.BaseLines, c.V2NewStart, c.V2NewLines));
        }
        foreach (var m in manifest.CleanMerges)
        {
            var map = m.Side == "v2" ? v2ByPath : v1ByPath; // canonical side "v1" also covers agreed edits
            Add(map, m.Path, new Hunk(m.Op, m.OldStart, m.OldLines, m.NewStart, m.NewLines));
        }

        var paths = new SortedSet<string>(StringComparer.Ordinal);
        paths.UnionWith(v1ByPath.Keys);
        paths.UnionWith(v2ByPath.Keys);

        var files = new List<FileMergeCheck>(paths.Count);
        foreach (var path in paths)
        {
            var rel = path.Replace('/', Path.DirectorySeparatorChar);
            var bPath = Path.Combine(baseDir, rel);
            var v1Path = Path.Combine(v1Dir, rel);
            var v2Path = Path.Combine(v2Dir, rel);
            if (!File.Exists(bPath) || !File.Exists(v1Path) || !File.Exists(v2Path))
            {
                files.Add(new FileMergeCheck(path, false, false));
                continue;
            }

            var baseLines = LineText.SplitLines(File.ReadAllText(bPath));
            var v1Lines = LineText.SplitLines(File.ReadAllText(v1Path));
            var v2Lines = LineText.SplitLines(File.ReadAllText(v2Path));

            bool v1Ok = ReconstructsSide(baseLines, v1Lines, v1ByPath.GetValueOrDefault(path));
            bool v2Ok = ReconstructsSide(baseLines, v2Lines, v2ByPath.GetValueOrDefault(path));
            files.Add(new FileMergeCheck(path, v1Ok, v2Ok));
        }

        return new ConflictCrossCheckResult(files);
    }

    private static bool ReconstructsSide(
        IReadOnlyList<string> baseLines, IReadOnlyList<string> variantLines, List<Hunk>? hunks)
    {
        if (hunks is null) return true; // no recorded change on this side ⇒ variant equals base there
        var sorted = hunks.OrderBy(h => h.OldStart).ThenBy(h => h.NewStart).ToList();
        return HunkApplier.Reconstruct(baseLines, variantLines, sorted).SequenceEqual(variantLines);
    }
}
