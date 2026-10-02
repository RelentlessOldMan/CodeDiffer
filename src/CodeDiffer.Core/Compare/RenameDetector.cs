using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Hashing;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Walk;

namespace CodeDiffer.Core.Compare;

/// <summary>A resolved rename plus the add/remove sets left over after pairing.</summary>
public sealed record RenameResult(
    IReadOnlyList<RenameOp> Renames,
    IReadOnlyList<FileEntry> UnmatchedRemoved,
    IReadOnlyList<FileEntry> UnmatchedAdded);

/// <summary>
/// Resolves renames out of a 2-way compare's left-only (removed) and right-only (added) sets, matching
/// CodeSpawner's delta contract. Two passes:
///
///   1. PURE renames — identical content (same SHA-256). Works for binary too and needs no line read;
///      similarityMilli = 1000. Grouped by SHA so a removed file pairs with an added file byte-for-byte.
///   2. EDITED renames — remaining text files (not binary, within the read cap) scored by
///      <see cref="Similarity"/>; greedy best-match assignment by descending similarity, each file used
///      once, only pairs at or above <see cref="CompareOptions.RenameSimilarityThresholdMilli"/> kept.
///
/// Decoys in the fixtures are near-duplicate ADDs whose source still exists on BOTH sides — those are
/// never in the removed set, so they can't be falsely paired. Among genuine remove/add candidates we
/// take the single best match per file, which is what keeps precision up when one removed file resembles
/// several adds. Giant/binary files only ever rename via the pure (SHA) pass.
/// </summary>
public sealed class RenameDetector
{
    private readonly CompareOptions _options;

    public RenameDetector(CompareOptions options) => _options = options;

    public RenameResult Detect(IReadOnlyList<FileEntry> removed, IReadOnlyList<FileEntry> added)
    {
        if (removed.Count == 0 || added.Count == 0)
            return new RenameResult([], removed, added);

        var renames = new List<RenameOp>();
        var removedLeft = new List<FileEntry>(removed);
        var addedLeft = new List<FileEntry>(added);

        PairPureRenames(removedLeft, addedLeft, renames);
        if (removedLeft.Count > 0 && addedLeft.Count > 0)
            PairEditedRenames(removedLeft, addedLeft, renames);

        return new RenameResult(renames, removedLeft, addedLeft);
    }

    /// <summary>Pass 1: pair byte-identical files (same SHA-256). Consumes matched entries from both lists.</summary>
    private static void PairPureRenames(List<FileEntry> removed, List<FileEntry> added, List<RenameOp> renames)
    {
        // Index adds by SHA; multiple adds can share a SHA, so keep a queue and pop as we pair.
        var addsBySha = new Dictionary<string, Queue<FileEntry>>(StringComparer.Ordinal);
        foreach (var a in added)
        {
            var sha = ContentHasher.HashFile(a.FullPath);
            if (!addsBySha.TryGetValue(sha, out var q)) addsBySha[sha] = q = new Queue<FileEntry>();
            q.Enqueue(a);
        }

        var pairedAdds = new HashSet<string>(StringComparer.Ordinal);
        var stillRemoved = new List<FileEntry>(removed.Count);
        foreach (var r in removed)
        {
            var sha = ContentHasher.HashFile(r.FullPath);
            if (addsBySha.TryGetValue(sha, out var q) && q.Count > 0)
            {
                var a = q.Dequeue();
                renames.Add(new RenameOp(r.RelativePath, a.RelativePath, 1000));
                pairedAdds.Add(a.RelativePath);
            }
            else
            {
                stillRemoved.Add(r);
            }
        }

        removed.Clear();
        removed.AddRange(stillRemoved);
        added.RemoveAll(a => pairedAdds.Contains(a.RelativePath));
    }

    /// <summary>Pass 2: score remaining text pairs and assign greedily by descending similarity.</summary>
    private void PairEditedRenames(List<FileEntry> removed, List<FileEntry> added, List<RenameOp> renames)
    {
        // Read + EOL-normalize + split each candidate once; skip binary / oversized (line metric needs text).
        var removedLines = ReadLines(removed);
        var addedLines = ReadLines(added);

        var scored = new List<(int milli, int ri, int ai)>();
        for (int ri = 0; ri < removed.Count; ri++)
        {
            if (removedLines[ri] is not { } rl) continue;
            for (int ai = 0; ai < added.Count; ai++)
            {
                if (addedLines[ai] is not { } al) continue;
                int milli = Similarity.MilliFromLines(rl, al);
                if (milli >= _options.RenameSimilarityThresholdMilli)
                    scored.Add((milli, ri, ai));
            }
        }

        // Best first; ties broken by path so assignment is deterministic across runs/platforms.
        scored.Sort((x, y) =>
        {
            int c = y.milli.CompareTo(x.milli);
            if (c != 0) return c;
            c = string.CompareOrdinal(removed[x.ri].RelativePath, removed[y.ri].RelativePath);
            if (c != 0) return c;
            return string.CompareOrdinal(added[x.ai].RelativePath, added[y.ai].RelativePath);
        });

        var usedR = new bool[removed.Count];
        var usedA = new bool[added.Count];
        var pairedR = new HashSet<string>(StringComparer.Ordinal);
        var pairedA = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (milli, ri, ai) in scored)
        {
            if (usedR[ri] || usedA[ai]) continue;
            usedR[ri] = usedA[ai] = true;
            renames.Add(new RenameOp(removed[ri].RelativePath, added[ai].RelativePath, milli));
            pairedR.Add(removed[ri].RelativePath);
            pairedA.Add(added[ai].RelativePath);
        }

        removed.RemoveAll(e => pairedR.Contains(e.RelativePath));
        added.RemoveAll(e => pairedA.Contains(e.RelativePath));
    }

    /// <summary>Line arrays for each entry, or null where the file is binary or past the read cap.</summary>
    private string[]?[] ReadLines(IReadOnlyList<FileEntry> entries)
    {
        var result = new string[]?[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Length > _options.MaxClassifyBytes) { result[i] = null; continue; }

            var bytes = File.ReadAllBytes(e.FullPath);
            int head = Math.Min(bytes.Length, TextInspector.HeadBytes);
            if (TextInspector.LooksBinary(bytes.AsSpan(0, head))) { result[i] = null; continue; }

            var normalized = TextInspector.NormalizeEol(TextInspector.Decode(bytes));
            result[i] = LineText.SplitLines(normalized);
        }
        return result;
    }
}
