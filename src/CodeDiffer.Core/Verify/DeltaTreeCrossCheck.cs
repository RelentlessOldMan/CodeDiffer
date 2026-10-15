using CodeDiffer.Core.Compare;
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
/// The manifest's hunks must also be the canonical ones (<see cref="HunkApplier.Problem"/>: coalesced, each where the
/// walk is, no replace wider than its change), since a hunk takes its text from the variant and a loose one would still
/// rebuild. Content and whitespace files are checked (a whitespace file's hunks must change only whitespace), run-rule
/// hunks expanded and the files streamed; binary/eol/encoding/metadata carry no textual hunks and are skipped (one that carries any fails).
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
            bool comparable = f.Reason is ChangeReason.Content or ChangeReason.Whitespace;
            if (!comparable)
            {
                // The contract gives these reasons no textual hunks: one that carries any is wrong, whatever the trees hold.
                if (f.Hunks.Count + f.RunHunks.Count > 0)
                    results.Add(new FileHunkCheck(f.Path, reason, Checked: true, Reconstructs: false, ExactMatch: false, f.Hunks.Count + f.RunHunks.Count, 0,
                        $"a {reason} record carries {f.Hunks.Count + f.RunHunks.Count} hunk(s); the contract gives it none"));
                else
                    results.Add(new FileHunkCheck(f.Path, reason, Checked: false, Reconstructs: false, ExactMatch: false, 0, 0));
                continue;
            }

            var relative = f.Path.Replace('/', Path.DirectorySeparatorChar);
            var baseRelative = (renamedFrom.TryGetValue(f.Path, out var from) ? from : f.Path).Replace('/', Path.DirectorySeparatorChar);
            bool ws = f.Reason == ChangeReason.Whitespace;
            var rules = TextInspector.RulesFor(baseRelative, relative); // strings read as the engine reads them
            if (f.RunHunks.Count > 0)
            {
                // A giant file: its run rules expanded as they are checked (never held: a rule can claim 2³¹ lines), both
                // files streamed; CodeDiffer's own line diff isn't run on it.
                string? why;
                try
                {
                    why = HunkApplier.ProblemInOrder(TreeLines.Stream(Path.Combine(baseDir, baseRelative)), TreeLines.Stream(Path.Combine(variantDir, relative)),
                        InOrder(f), ws, rules);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OverflowException)
                {
                    why = ex.Message;
                }
                results.Add(new FileHunkCheck(f.Path, reason, Checked: true, Reconstructs: why is null, ExactMatch: false, f.Hunks.Count + f.RunHunks.Count, 0, why));
                continue;
            }
            string[] baseLines, variantLines;
            try
            {
                baseLines = TreeLines.Read(Path.Combine(baseDir, baseRelative));
                variantLines = TreeLines.Read(Path.Combine(variantDir, relative));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new FileHunkCheck(f.Path, reason, Checked: true, Reconstructs: false, ExactMatch: false, f.Hunks.Count, 0, ex.Message));
                continue;
            }

            var mine = (ws ? LineDiffer.WhitespaceAligned(baseLines, variantLines, rules) : null) ?? LineDiffer.Diff(baseLines, variantLines);

            var problem = HunkApplier.Problem(baseLines, variantLines, f.Hunks, ws, rules);
            bool mineRebuilds = Reconstructs(baseLines, variantLines, mine);
            bool exact = HunksEqual(mine, f.Hunks);

            results.Add(new FileHunkCheck(
                f.Path, reason, Checked: true,
                Reconstructs: problem is null && mineRebuilds,
                ExactMatch: exact,
                f.Hunks.Count, mine.Count,
                problem ?? (mineRebuilds ? null : "CodeDiffer's own hunks do not rebuild the variant")));
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

    /// <summary>A record's explicit hunks and its run rules' expansions, merged in order (<see cref="HunkApplier.OrderKey"/>,
    /// then new start) one at a time.</summary>
    private static IEnumerable<Hunk> InOrder(FileDelta f)
    {
        var sources = new List<IEnumerator<Hunk>> { f.Hunks.OrderBy(HunkApplier.OrderKey).ThenBy(h => h.NewStart).GetEnumerator() };
        sources.AddRange(f.RunHunks.Select(r => r.Expand().GetEnumerator()));
        var queue = new PriorityQueue<IEnumerator<Hunk>, (int, int)>();
        try
        {
            foreach (var s in sources)
                if (s.MoveNext()) queue.Enqueue(s, (HunkApplier.OrderKey(s.Current), s.Current.NewStart));
            while (queue.TryDequeue(out var s, out _))
            {
                yield return s.Current;
                if (s.MoveNext()) queue.Enqueue(s, (HunkApplier.OrderKey(s.Current), s.Current.NewStart));
            }
        }
        finally
        {
            foreach (var s in sources) s.Dispose();
        }
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
