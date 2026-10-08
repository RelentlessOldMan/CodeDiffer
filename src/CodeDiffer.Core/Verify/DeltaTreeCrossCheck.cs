using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Verify;

/// <summary>Per-file cross-check outcome.</summary>
/// <param name="Reconstructs">The manifest's hunks (and CodeDiffer's own) rebuild the variant from the base — the correctness gate.</param>
/// <param name="ExactMatch">CodeDiffer's hunks are coordinate-identical to the manifest's (a bonus; only guaranteed when the diff is unambiguous).</param>
/// <param name="Problem">Why the file could not be checked (missing, unreadable), or null.</param>
public sealed record FileHunkCheck(
    string Path, string Reason, bool Checked, bool Reconstructs, bool ExactMatch, int ExpectedHunks, int ActualHunks, string? Problem = null);

/// <param name="ContentMismatches">Modified records whose oldSha/oldSize or newSha/newSize (any reason) are not the
/// trees' files: the digest proves only that the manifest agrees with itself.</param>
public sealed record TreeCrossCheckResult(IReadOnlyList<FileHunkCheck> Files, IReadOnlyList<string> ContentMismatches)
{
    public int Checked => Files.Count(f => f.Checked);
    public int Reconstructed => Files.Count(f => f.Checked && f.Reconstructs);
    public int ExactMatches => Files.Count(f => f.Checked && f.ExactMatch);
    public int Skipped => Files.Count(f => !f.Checked);

    /// <summary>Pass iff every checked file's hunks reconstruct the variant and every modified record's shas and sizes
    /// are the trees' files. Exact-match is NOT required.</summary>
    public bool Ok => Files.All(f => !f.Checked || f.Reconstructs) && ContentMismatches.Count == 0;
}

/// <summary>
/// The second verify tier: given the base/variant trees a delta describes, confirm CodeDiffer's view of
/// each modified file agrees with the manifest. The honest gate is <b>reconstruction</b>, not coordinate
/// equality: a diff between two files is not unique (when lines repeat, several minimal diffs exist), and
/// CodeSpawner records the edit it MADE while CodeDiffer computes a MINIMAL Myers diff — both correct, and
/// they may legitimately place hunks differently. So we assert that the manifest's hunks (and CodeDiffer's)
/// each rebuild the variant from the base; coordinate-identical hunks are reported as a bonus signal.
///
/// Only reason=content files with explicit hunks are line-comparable; giant run-rule files and
/// binary/eol/encoding/metadata carry no comparable textual hunks and are reported as skipped (with reason).
/// A rename with edits has its hunks under the new path (the contract keys them by <c>to</c>): its old side is the
/// base's <c>from</c>.
/// </summary>
public static class DeltaTreeCrossCheck
{
    public static TreeCrossCheckResult Run(string baseDir, string variantDir, DeltaManifest manifest)
    {
        var results = new List<FileHunkCheck>(manifest.Modified.Count);
        var renamedFrom = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in manifest.Renamed) renamedFrom[r.To] = r.From;
        var content = new List<string>();

        foreach (var f in manifest.Modified)
        {
            var reason = CanonicalTokens.Token(f.Reason);
            var oldPath = renamedFrom.TryGetValue(f.Path, out var was) ? was : f.Path;
            if (Mismatch(baseDir, oldPath, f.OldSha, f.OldSize) is { } bm) content.Add($"{f.Path}: base {bm}");
            if (Mismatch(variantDir, f.Path, f.NewSha, f.NewSize) is { } vm) content.Add($"{f.Path}: variant {vm}");
            bool comparable = f.Reason == ChangeReason.Content && f.RunHunks.Count == 0;
            if (!comparable)
            {
                results.Add(new FileHunkCheck(f.Path, reason, Checked: false, Reconstructs: false, ExactMatch: false, f.Hunks.Count, 0));
                continue;
            }

            var relative = f.Path.Replace('/', Path.DirectorySeparatorChar);
            var baseRelative = (renamedFrom.TryGetValue(f.Path, out var from) ? from : f.Path).Replace('/', Path.DirectorySeparatorChar);
            string[] baseLines, variantLines;
            try
            {
                baseLines = LineText.SplitLines(File.ReadAllText(Path.Combine(baseDir, baseRelative)));
                variantLines = LineText.SplitLines(File.ReadAllText(Path.Combine(variantDir, relative)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new FileHunkCheck(f.Path, reason, Checked: true, Reconstructs: false, ExactMatch: false, f.Hunks.Count, 0, ex.Message));
                continue;
            }

            var mine = LineDiffer.Diff(baseLines, variantLines);

            bool manifestRebuilds = Reconstructs(baseLines, variantLines, f.Hunks);
            bool mineRebuilds = Reconstructs(baseLines, variantLines, mine);
            bool exact = HunksEqual(mine, f.Hunks);

            results.Add(new FileHunkCheck(
                f.Path, reason, Checked: true,
                Reconstructs: manifestRebuilds && mineRebuilds,
                ExactMatch: exact,
                f.Hunks.Count, mine.Count));
        }

        return new TreeCrossCheckResult(results, content);
    }

    /// <summary>Why the file is not the one the manifest names (size, then SHA-256 of its bytes), or null when it is.</summary>
    private static string? Mismatch(string root, string rel, string sha, long size)
    {
        var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return $"{rel} is missing";
            if (info.Length != size) return $"{rel} is {info.Length:N0} bytes, the manifest says {size:N0}";
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
            return string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase) ? null : $"{rel} has SHA-256 {actual}, the manifest says {sha}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"{rel} can't be read ({ex.Message})"; }
    }

    private static bool Reconstructs(IReadOnlyList<string> baseLines, IReadOnlyList<string> variantLines, IReadOnlyList<Hunk> hunks)
        => HunkApplier.Rebuilds(baseLines, variantLines, hunks);

    private static bool HunksEqual(IReadOnlyList<Hunk> mine, IReadOnlyList<Hunk> expected)
    {
        if (mine.Count != expected.Count) return false;
        var a = mine.OrderBy(h => h.OldStart).ThenBy(h => h.NewStart).ToList();
        var b = expected.OrderBy(h => h.OldStart).ThenBy(h => h.NewStart).ToList();
        return a.SequenceEqual(b);
    }
}
