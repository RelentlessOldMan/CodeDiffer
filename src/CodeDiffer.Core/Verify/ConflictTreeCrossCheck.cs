using CodeDiffer.Core.Diff;
using CodeDiffer.Core.DiffTruth;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.ThreeWay;

namespace CodeDiffer.Core.Verify;

/// <summary>
/// Gate 3: what <c>compare3</c> itself (<see cref="TreeMerger"/>, over the whole trees, lines with their endings) says
/// against the manifest. <see cref="ConflictOnlyInCompare3"/> / <see cref="ConflictOnlyInManifest"/>: files compare3
/// conflicts in at the same path in all three trees that the manifest doesn't, and the reverse. <see cref="Unchanged"/>:
/// manifest files compare3 found no change in.
/// </summary>
public sealed record Compare3CrossCheck(
    int Entries,
    int Conflicts,
    IReadOnlyList<string> ConflictOnlyInCompare3,
    IReadOnlyList<string> ConflictOnlyInManifest,
    IReadOnlyList<string> Unchanged,
    int Unread,
    IReadOnlyList<string> MergedNotInManifest)
{
    public bool Ok => ConflictOnlyInCompare3.Count == 0 && ConflictOnlyInManifest.Count == 0 && Unchanged.Count == 0 && Unread == 0
                      && MergedNotInManifest.Count == 0;
}

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
    IReadOnlyList<CleanMerge> CleanOnlyInManifest,
    IReadOnlyList<string> ConflictPathsOnlyInMine,
    IReadOnlyList<string> ConflictPathsOnlyInManifest,
    IReadOnlyList<string> UnsoundRegions)
{
    public int FilesChecked => Files.Count;
    public int V1Reconstructed => Files.Count(f => f.V1Reconstructs);
    public int V2Reconstructed => Files.Count(f => f.V2Reconstructs);
    public bool Reconstructs => Files.Count > 0 && Files.All(f => f.V1Reconstructs && f.V2Reconstructs);

    /// <summary>My merge conflicts in exactly the files the manifest says conflict (wherever in them).</summary>
    public bool ConflictPathsMatch => ConflictPathsOnlyInMine.Count == 0 && ConflictPathsOnlyInManifest.Count == 0;

    /// <summary>
    /// Pass if my own diff3 reproduces the exact decomposition (digest equality — achievable when the diff is
    /// unambiguous), OR — when a run of identical lines makes the minimal diff non-unique, so regions may
    /// legitimately sit elsewhere — the manifest's per-side coords rebuild both trees AND my merge conflicts in
    /// exactly the same files, AND every region is what it says against the trees (<see cref="UnsoundRegions"/>: a
    /// conflict both sides changed, differently; a clean merge its side changed, clear of the other side's changes).
    /// Reconstruction alone tests only the manifest; the file agreement keeps CodeDiffer's own merge in the gate, and
    /// the region check keeps a clean edit from being passed off as a conflict (or the reverse).
    /// </summary>
    public bool Ok => DecompositionMatches || (Reconstructs && ConflictPathsMatch && UnsoundRegions.Count == 0);
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
    /// <summary>
    /// Gate 3: the files compare3 conflicts in must be exactly the manifest's. Gates 1 and 2 check the manifest's files
    /// with the contract's line metric (no line endings); this one checks the verdicts a user gets, everywhere: a
    /// difference only in a final newline, or a conflict in a file the manifest never mentions, fails here.
    /// </summary>
    public static Compare3CrossCheck Compare3(ThreeWayReport report, ConflictManifest manifest)
    {
        // A line merge happens where both sides modified the file in place; renames, adds and deletes are file
        // operations the conflict manifest doesn't describe.
        static bool InPlace(Merge3Entry e) => e.V1 is { Status: ChangeStatus.Modified } && e.V2 is { Status: ChangeStatus.Modified };
        var mine = report.Entries.Where(e => e.Outcome == Merge3Outcome.Conflict && InPlace(e)).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var merged = report.Entries.Where(e => e.Outcome == Merge3Outcome.Merged && InPlace(e)).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var theirs = manifest.Conflicts.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        var touched = report.Entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var named = manifest.Conflicts.Select(c => c.Path).Concat(manifest.CleanMerges.Select(m => m.Path)).ToHashSet(StringComparer.Ordinal);
        return new Compare3CrossCheck(
            report.Entries.Count, report.Count(Merge3Outcome.Conflict),
            mine.Except(theirs).Order(StringComparer.Ordinal).ToList(),
            theirs.Except(mine).Order(StringComparer.Ordinal).ToList(),
            named.Except(touched).Order(StringComparer.Ordinal).ToList(),
            report.UnreadableFiles + report.DroppedDirectories,
            // A file both sides edited in place and compare3 merged cleanly is a merge the manifest must describe: one
            // left out entirely would otherwise pass every gate (gates 1 and 2 only see the files the manifest names).
            merged.Except(named).Order(StringComparer.Ordinal).ToList());
    }

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

            string[] b, v1, v2;
            try
            {
                b = LineText.SplitLines(File.ReadAllText(bPath));
                v1 = LineText.SplitLines(File.ReadAllText(v1Path));
                v2 = LineText.SplitLines(File.ReadAllText(v2Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; } // fails reconstruction below
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

        var unsound = new List<string>();
        foreach (var path in paths)
            if (trees.TryGetValue(path, out var t)) unsound.AddRange(Unsound(path, t.b, t.v1, t.v2, manifest));

        var mineC = myConflicts.ToHashSet();
        var manC = manifest.Conflicts.ToHashSet();
        var mineM = myClean.ToHashSet();
        var manM = manifest.CleanMerges.ToHashSet();

        return new ConflictCrossCheckResult(
            decompMatches, myDigest, manifest.ConflictTruthSha,
            myConflicts.Count, myClean.Count, manifest.Conflicts.Count, manifest.CleanMerges.Count,
            files,
            mineC.Except(manC).Take(20).ToList(), manC.Except(mineC).Take(20).ToList(),
            mineM.Except(manM).Take(20).ToList(), manM.Except(mineM).Take(20).ToList(),
            myConflicts.Select(c => c.Path).Except(manifest.Conflicts.Select(c => c.Path), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            manifest.Conflicts.Select(c => c.Path).Except(myConflicts.Select(c => c.Path), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            unsound);
    }

    /// <summary>
    /// The manifest's regions in one file that are not what they say, read against the trees: a conflict where a side
    /// left the base as it was, or both sides made the same change; a clean merge whose side changed nothing there, or
    /// that overlaps a change of the other side (a conflict, or the other side's own clean merge).
    /// </summary>
    private static IEnumerable<string> Unsound(string path, string[] b, string[] v1, string[] v2, ConflictManifest manifest)
    {
        var conflicts = manifest.Conflicts.Where(c => c.Path == path).ToList();
        var clean = manifest.CleanMerges.Where(m => m.Path == path).ToList();
        foreach (var c in conflicts)
        {
            bool v1Same = SeqEqual(v1, c.V1NewStart - 1, c.V1NewLines, b, c.BaseStart - 1, c.BaseLines);
            bool v2Same = SeqEqual(v2, c.V2NewStart - 1, c.V2NewLines, b, c.BaseStart - 1, c.BaseLines);
            bool agreed = SeqEqual(v1, c.V1NewStart - 1, c.V1NewLines, v2, c.V2NewStart - 1, c.V2NewLines);
            if (v1Same || v2Same || agreed)
                yield return $"conflict {path}:{c.BaseStart},{c.BaseLines}: " +
                             (v1Same ? "v1 left the base as it was there" : v2Same ? "v2 left the base as it was there" : "both sides made the same change");
        }
        foreach (var m in clean)
        {
            var side = m.Side == "v2" ? v2 : v1;
            if (SeqEqual(side, m.NewStart - 1, m.NewLines, b, m.OldStart - 1, m.OldLines))
            {
                yield return $"clean {m.Side} {path}:{m.OldStart},{m.OldLines}: {m.Side} changed nothing there";
                continue;
            }
            bool clash = conflicts.Any(c => Overlap(m.OldStart, m.OldLines, c.BaseStart, c.BaseLines))
                         || clean.Any(o => o.Side != m.Side && Overlap(m.OldStart, m.OldLines, o.OldStart, o.OldLines));
            if (clash) yield return $"clean {m.Side} {path}:{m.OldStart},{m.OldLines}: it overlaps a change of the other side";
        }
    }

    /// <summary>Two base regions that share a line, or two inserts at the same place (lenient about where an insert
    /// next to a region sits, which conventions differ on).</summary>
    private static bool Overlap(int s1, int n1, int s2, int n2)
        => n1 > 0 && n2 > 0 ? s1 < s2 + n2 && s2 < s1 + n1 : n1 == 0 && n2 == 0 && s1 == s2;

    private static bool ReconstructsSide(
        IReadOnlyList<string> baseLines, IReadOnlyList<string> variantLines, List<Hunk>? hunks)
    {
        // No recorded change on this side: the variant must BE the base (checked, not assumed — a manifest that left
        // out all of one side's changes in a file would otherwise pass).
        var sorted = (hunks ?? []).OrderBy(h => h.OldStart).ThenBy(h => h.NewStart).ToList();
        return HunkApplier.Rebuilds(baseLines, variantLines, sorted);
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
        return HunkApplier.Rebuilds(baseLines, v2Lines, sorted);
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
