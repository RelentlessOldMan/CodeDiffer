using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Hashing;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Walk;

namespace CodeDiffer.Core.Compare;

/// <summary>A resolved rename plus the add/remove sets left over after pairing. <paramref name="Unreadable"/>:
/// relative path → why, for files that could not be read (left out of the pairing).</summary>
public sealed record RenameResult(
    IReadOnlyList<RenameOp> Renames,
    IReadOnlyList<FileEntry> UnmatchedRemoved,
    IReadOnlyList<FileEntry> UnmatchedAdded,
    IReadOnlyDictionary<string, string>? Unreadable = null);

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

    /// <param name="ct">Checked per file and per 1 MB chunk: this pass reads every added and removed file.</param>
    public RenameResult Detect(IReadOnlyList<FileEntry> removed, IReadOnlyList<FileEntry> added, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (removed.Count == 0 || added.Count == 0)
            return new RenameResult([], removed, added);

        var renames = new List<RenameOp>();
        var removedLeft = new List<FileEntry>(removed);
        var addedLeft = new List<FileEntry>(added);
        var unreadable = new Dictionary<string, string>(StringComparer.Ordinal);

        PairPureRenames(removedLeft, addedLeft, renames, unreadable, ct);
        if (removedLeft.Count > 0 && addedLeft.Count > 0)
            PairEditedRenames(removedLeft, addedLeft, renames, unreadable, ct);

        return new RenameResult(renames, removedLeft, addedLeft, unreadable);
    }

    /// <summary>
    /// Pass 1: pair byte-identical files (same SHA-256). Consumes matched entries from both lists. Identical bytes
    /// means identical size, so only files whose size occurs on both sides are read. Empty files never pair (git
    /// skips them too): every empty file is identical to every other, so a pairing would be a guess — and a 3-way
    /// merge would follow it, moving the other side's edit into an unrelated file. Among identical candidates the
    /// one with the same file name, then the same directory, wins.
    /// </summary>
    private static void PairPureRenames(List<FileEntry> removed, List<FileEntry> added, List<RenameOp> renames,
        Dictionary<string, string> unreadable, CancellationToken ct)
    {
        var removedSizes = removed.Where(r => r.Length > 0).Select(r => r.Length).ToHashSet();
        var addedSizes = added.Where(a => a.Length > 0).Select(a => a.Length).ToHashSet();

        var addsBySha = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);
        foreach (var a in added)
        {
            if (!removedSizes.Contains(a.Length) || Hash(a, unreadable, ct) is not { } sha) continue;
            if (!addsBySha.TryGetValue(sha, out var list)) addsBySha[sha] = list = [];
            list.Add(a);
        }

        var pairedAdds = new HashSet<string>(StringComparer.Ordinal);
        var stillRemoved = new List<FileEntry>(removed.Count);
        foreach (var r in removed)
        {
            if (addedSizes.Contains(r.Length) && Hash(r, unreadable, ct) is { } sha
                && addsBySha.TryGetValue(sha, out var list) && list.Count > 0)
            {
                var a = list.OrderBy(x => Name(x) == Name(r) ? 0 : 1).ThenBy(x => Dir(x) == Dir(r) ? 0 : 1)
                    .ThenBy(x => x.RelativePath, StringComparer.Ordinal).First();
                list.Remove(a);
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

        static string Name(FileEntry e) => e.RelativePath[(e.RelativePath.LastIndexOf('/') + 1)..];
        static string Dir(FileEntry e) => e.RelativePath[..Math.Max(0, e.RelativePath.LastIndexOf('/'))];
    }

    /// <summary>A file's SHA-256, or null (and the reason recorded) when it can't be read.</summary>
    private static string? Hash(FileEntry e, Dictionary<string, string> unreadable, CancellationToken ct)
    {
        try { return ContentHasher.HashFile(e.FullPath, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable[e.RelativePath] = ex.Message;
            return null;
        }
    }

    /// <summary>Pass 2: score remaining text pairs and assign greedily by descending similarity.</summary>
    private void PairEditedRenames(List<FileEntry> removed, List<FileEntry> added, List<RenameOp> renames,
        Dictionary<string, string> unreadable, CancellationToken ct)
    {
        // Read + EOL-normalize + split each candidate once; skip binary / oversized (line metric needs text).
        var removedLines = ReadLines(removed, unreadable, ct);
        var addedLines = ReadLines(added, unreadable, ct);

        var scored = new List<(int milli, int ri, int ai)>();
        for (int ri = 0; ri < removed.Count; ri++)
        {
            ct.ThrowIfCancellationRequested();
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

    /// <summary>Line arrays for each entry, or null where the file is empty, binary, past the read cap or unreadable.</summary>
    private string[]?[] ReadLines(IReadOnlyList<FileEntry> entries, Dictionary<string, string> unreadable, CancellationToken ct)
    {
        var result = new string[]?[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var e = entries[i];
            if (e.Length == 0 || e.Length > _options.MaxClassifyBytes || unreadable.ContainsKey(e.RelativePath)) { result[i] = null; continue; }

            byte[] bytes;
            try { bytes = TextInspector.ReadAll(e.FullPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable[e.RelativePath] = ex.Message;
                continue;
            }
            if (bytes.Length == 0) continue;
            int head = Math.Min(bytes.Length, TextInspector.HeadBytes);
            if (TextInspector.LooksBinary(bytes.AsSpan(0, head))) { result[i] = null; continue; }

            var normalized = TextInspector.NormalizeEol(TextInspector.Decode(bytes));
            result[i] = LineText.SplitLines(normalized);
        }
        return result;
    }
}
