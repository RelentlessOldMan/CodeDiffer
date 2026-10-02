using CodeDiffer.Core.Diff;
using CodeDiffer.Core.DiffTruth;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Verify;

/// <summary>Per-file reconstruction outcome (manifest's own per-side coords rebuild each variant).</summary>
public sealed record FileMergeCheck(string Path, bool V1Reconstructs, bool V2Reconstructs);

public sealed record ConflictCrossCheckResult(
    bool DecompositionMatches,
    string MyDigest,
    string? Stated,
    int MyConflicts,
    int MyClean,
    int ManifestConflicts,
    int ManifestClean,
    IReadOnlyList<FileMergeCheck> Files,
    IReadOnlyList<Conflict> ConflictsOnlyInMine,
    IReadOnlyList<Conflict> ConflictsOnlyInManifest,
    IReadOnlyList<CleanMerge> CleanOnlyInMine,
    IReadOnlyList<CleanMerge> CleanOnlyInManifest)
{
    public int FilesChecked => Files.Count;
    public int V1Reconstructed => Files.Count(f => f.V1Reconstructs);
    public int V2Reconstructed => Files.Count(f => f.V2Reconstructs);
    public bool Reconstructs => Files.Count > 0 && Files.All(f => f.V1Reconstructs && f.V2Reconstructs);

    /// <summary>
    /// Pass if EITHER gate confirms: my own diff3 reproduces the exact decomposition (digest equality —
    /// achievable when the diff is unambiguous), OR the manifest's per-side coords rebuild both trees
    /// (robust when a run of identical lines makes the diff non-unique). The two gates cover each other's
    /// blind spots: an identical-line corpus defeats digest equality; an agreed-edit region defeats v2
    /// reconstruction. A faithful manifest clears at least one.
    /// </summary>
    public bool Ok => DecompositionMatches || Reconstructs;
}

/// <summary>
/// The 3-way correctness gate. It combines two independent checks because neither alone covers every
/// fixture:
///
///  1. DECOMPOSITION match — run CodeDiffer's own <see cref="ThreeWayMerger"/> over the trees and compare
///     its conflictTruthSha to the manifest's. Because the digest dedups-then-sorts, a match is set
///     equality: CodeDiffer independently produced the identical 3-way merge. This is exact but only
///     achievable when the diff is unique (the edge fixture is laid out for this).
///
///  2. RECONSTRUCTION — the manifest's OWN per-side ops rebuild V1 and V2 from the base. Robust when a
///     run of byte-identical lines makes the minimal diff non-unique (so (1) legitimately differs), which
///     is the overlap-0.5 main fixture. Caveat: an identical-edit-both-sides region is recorded once with
///     side "v1", so it is absent from the v2 op-set and v2 reconstruction misses it — such fixtures pass
///     via gate (1) instead.
/// </summary>
public static class ConflictTreeCrossCheck
{
    public static ConflictCrossCheckResult Run(
        string baseDir, string v1Dir, string v2Dir, ConflictManifest manifest)
    {
        // Gate 1: my own diff3 over the touched files.
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in manifest.Conflicts) paths.Add(c.Path);
        foreach (var m in manifest.CleanMerges) paths.Add(m.Path);

        var myConflicts = new List<Conflict>();
        var myClean = new List<CleanMerge>();
        var trees = new Dictionary<string, (string[] b, string[] v1, string[] v2)>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var rel = path.Replace('/', Path.DirectorySeparatorChar);
            var bPath = Path.Combine(baseDir, rel);
            var v1Path = Path.Combine(v1Dir, rel);
            var v2Path = Path.Combine(v2Dir, rel);
            if (!File.Exists(bPath) || !File.Exists(v1Path) || !File.Exists(v2Path)) continue;

            var b = LineText.SplitLines(File.ReadAllText(bPath));
            var v1 = LineText.SplitLines(File.ReadAllText(v1Path));
            var v2 = LineText.SplitLines(File.ReadAllText(v2Path));
            trees[path] = (b, v1, v2);

            var r = ThreeWayMerger.Merge(path, b, v1, v2);
            myConflicts.AddRange(r.Conflicts);
            myClean.AddRange(r.CleanMerges);
        }

        var myDigest = ConflictDigest.Compute(myConflicts, myClean);
        bool decompMatches = manifest.ConflictTruthSha is not null
                             && string.Equals(myDigest, manifest.ConflictTruthSha, StringComparison.Ordinal);

        // Gate 2: manifest's per-side coords reconstruct the trees. v1 is complete (conflict.v1 +
        // clean[v1]); v2 is conflict.v2 + clean[v2], plus identical-overlap regions (recorded once as
        // clean side "v1") folded back in — see ReconstructsV2.
        var v1ByPath = new Dictionary<string, List<Hunk>>(StringComparer.Ordinal);
        var v2DefByPath = new Dictionary<string, List<Hunk>>(StringComparer.Ordinal);
        var cleanV1ByPath = new Dictionary<string, List<Hunk>>(StringComparer.Ordinal);
        void Add(Dictionary<string, List<Hunk>> map, string p, Hunk h)
        {
            if (!map.TryGetValue(p, out var list)) map[p] = list = [];
            list.Add(h);
        }
        foreach (var c in manifest.Conflicts)
        {
            Add(v1ByPath, c.Path, new Hunk(c.V1Op, c.BaseStart, c.BaseLines, c.V1NewStart, c.V1NewLines));
            Add(v2DefByPath, c.Path, new Hunk(c.V2Op, c.BaseStart, c.BaseLines, c.V2NewStart, c.V2NewLines));
        }
        foreach (var m in manifest.CleanMerges)
        {
            var h = new Hunk(m.Op, m.OldStart, m.OldLines, m.NewStart, m.NewLines);
            if (m.Side == "v2") Add(v2DefByPath, m.Path, h);
            else { Add(v1ByPath, m.Path, h); Add(cleanV1ByPath, m.Path, h); }
        }

        var files = new List<FileMergeCheck>(paths.Count);
        foreach (var path in paths)
        {
            if (!trees.TryGetValue(path, out var t)) { files.Add(new FileMergeCheck(path, false, false)); continue; }
            bool v1Ok = ReconstructsSide(t.b, t.v1, v1ByPath.GetValueOrDefault(path));
            bool v2Ok = ReconstructsV2(t.b, t.v2, v2DefByPath.GetValueOrDefault(path), cleanV1ByPath.GetValueOrDefault(path));
            files.Add(new FileMergeCheck(path, v1Ok, v2Ok));
        }

        var mineC = myConflicts.ToHashSet();
        var manC = manifest.Conflicts.ToHashSet();
        var mineM = myClean.ToHashSet();
        var manM = manifest.CleanMerges.ToHashSet();

        return new ConflictCrossCheckResult(
            decompMatches, myDigest, manifest.ConflictTruthSha,
            myConflicts.Count, myClean.Count, manifest.Conflicts.Count, manifest.CleanMerges.Count,
            files,
            mineC.Except(manC).Take(20).ToList(), manC.Except(mineC).Take(20).ToList(),
            mineM.Except(manM).Take(20).ToList(), manM.Except(mineM).Take(20).ToList());
    }

    private static bool ReconstructsSide(
        IReadOnlyList<string> baseLines, IReadOnlyList<string> variantLines, List<Hunk>? hunks)
    {
        if (hunks is null) return true; // no recorded change on this side ⇒ variant equals base there
        var sorted = hunks.OrderBy(h => h.OldStart).ThenBy(h => h.NewStart).ToList();
        return HunkApplier.Reconstruct(baseLines, variantLines, sorted).SequenceEqual(variantLines);
    }

    /// <summary>
    /// Reconstruct V2, folding in identical-overlap regions that the manifest records only once (clean
    /// side "v1"). Walking the v2 changes in base order keeps a running V2 line-offset; at each clean-v1
    /// region we ask the trees whether V2 is unchanged there (one-sided v1 ⇒ skip) or changed to the same
    /// content (agreed ⇒ apply at the offset position, reading V2's own bytes). Agreed INSERTS (base-lines
    /// 0) are not folded — the contract's identical-overlap case is a modify; such a region would show as
    /// a V2 shortfall and is out of scope for this gate.
    /// </summary>
    private static bool ReconstructsV2(
        IReadOnlyList<string> baseLines, IReadOnlyList<string> v2Lines, List<Hunk>? v2Def, List<Hunk>? cleanV1)
    {
        var events = new List<(Hunk h, bool definite)>();
        if (v2Def is not null) foreach (var h in v2Def) events.Add((h, true));
        if (cleanV1 is not null) foreach (var h in cleanV1) events.Add((h, false));
        if (events.Count == 0) return true;
        events.Sort((x, y) => x.h.OldStart != y.h.OldStart ? x.h.OldStart.CompareTo(y.h.OldStart) : x.h.OldLines.CompareTo(y.h.OldLines));

        var v2Hunks = new List<Hunk>();
        int offset = 0; // running (V2 - base) line delta from v2 changes seen so far
        foreach (var (h, definite) in events)
        {
            if (definite)
            {
                v2Hunks.Add(h);
                offset += h.NewLines - h.OldLines;
                continue;
            }

            // clean side "v1" region: is V2 unchanged (one-sided v1) or an agreed identical edit?
            int pos = h.OldStart + offset; // 1-based V2 line where this base region begins
            bool v2Unchanged = SeqEqual(v2Lines, pos - 1, h.OldLines, baseLines, h.OldStart - 1, h.OldLines);
            if (v2Unchanged) continue; // v2 did not touch it — nothing to apply

            // agreed: V2 holds the same edit (h.NewLines lines) at pos; read it from V2's own tree.
            v2Hunks.Add(new Hunk(h.Op, h.OldStart, h.OldLines, pos, h.NewLines));
            offset += h.NewLines - h.OldLines;
        }

        var sorted = v2Hunks.OrderBy(h => h.OldStart).ThenBy(h => h.NewStart).ToList();
        return HunkApplier.Reconstruct(baseLines, v2Lines, sorted).SequenceEqual(v2Lines);
    }

    private static bool SeqEqual(
        IReadOnlyList<string> a, int aStart, int len, IReadOnlyList<string> b, int bStart, int lenB)
    {
        if (len != lenB) return false;
        if (aStart < 0 || bStart < 0 || aStart + len > a.Count || bStart + len > b.Count) return false;
        for (int k = 0; k < len; k++)
            if (!string.Equals(a[aStart + k], b[bStart + k], StringComparison.Ordinal)) return false;
        return true;
    }
}
