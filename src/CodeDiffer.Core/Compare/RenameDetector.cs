using System.Collections.Concurrent;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Walk;

namespace CodeDiffer.Core.Compare;

/// <summary>A resolved rename plus the add/remove sets left over after pairing. <paramref name="Unreadable"/>:
/// relative path → why, for files that could not be read (left out of the pairing). <paramref name="BytesRead"/>:
/// what the pairing read (ledger hits read nothing). <paramref name="Edited"/>: the destination paths of the renames the
/// similarity pass found (their bytes differ, whatever the similarity rounds to).</summary>
public sealed record RenameResult(
    IReadOnlyList<RenameOp> Renames,
    IReadOnlyList<FileEntry> UnmatchedRemoved,
    IReadOnlyList<FileEntry> UnmatchedAdded,
    IReadOnlyDictionary<string, string>? Unreadable = null,
    long BytesRead = 0,
    IReadOnlySet<string>? Edited = null);

/// <summary>
/// Resolves renames out of a 2-way compare's left-only (removed) and right-only (added) sets, matching
/// CodeSpawner's delta contract. Two passes:
///
///   1. PURE renames — identical content. Works for binary too and needs no line read; similarityMilli = 1000.
///      Only files whose size occurs on both sides are hashed, in parallel, and a trusted ledger entry is used
///      instead of a read (the hashes read are recorded for next time).
///   2. EDITED renames — remaining text files (not binary, within the read cap) scored by
///      <see cref="Similarity"/>; greedy best-match assignment by descending similarity, each file used
///      once, only pairs at or above <see cref="CompareOptions.RenameSimilarityThresholdMilli"/> kept.
///      Every pair is still scored exactly, but a pair whose line counts alone rule out the threshold is
///      skipped, each file's lines are counted once (not per pair), and the scoring runs in parallel.
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

    /// <param name="ct">Checked per file and per 1 MB chunk: this pass reads added and removed files.</param>
    public RenameResult Detect(IReadOnlyList<FileEntry> removed, IReadOnlyList<FileEntry> added, CancellationToken ct = default)
        => Detect(removed, added, null, null, ct);

    /// <param name="leftCache">The left tree's ledger (removed files), or null: trusted ids are used instead of a read.</param>
    /// <param name="rightCache">The right tree's ledger (added files), or null.</param>
    public RenameResult Detect(IReadOnlyList<FileEntry> removed, IReadOnlyList<FileEntry> added,
        HashCache? leftCache, HashCache? rightCache, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (removed.Count == 0 || added.Count == 0)
            return new RenameResult([], removed, added);

        var renames = new List<RenameOp>();
        var edited = new HashSet<string>(StringComparer.Ordinal);
        var removedLeft = new List<FileEntry>(removed);
        var addedLeft = new List<FileEntry>(added);
        var unreadable = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        long bytesRead = 0;

        PairPureRenames(removedLeft, addedLeft, leftCache, rightCache, renames, unreadable, ref bytesRead, ct);
        if (removedLeft.Count > 0 && addedLeft.Count > 0)
            PairEditedRenames(removedLeft, addedLeft, renames, edited, unreadable, ref bytesRead, ct);

        return new RenameResult(renames, removedLeft, addedLeft,
            new Dictionary<string, string>(unreadable, StringComparer.Ordinal), bytesRead, edited);
    }

    /// <summary>
    /// Pass 1: pair byte-identical files. Consumes matched entries from both lists. Identical bytes means identical
    /// size, so only files whose size occurs on both sides are hashed. Empty files never pair (git skips them too):
    /// every empty file is identical to every other, so a pairing would be a guess — and a 3-way merge would follow
    /// it, moving the other side's edit into an unrelated file. Among identical files the pairs are taken best first —
    /// same file name, then same directory, then by path — over every pair at once, so the order of the removed list
    /// never decides (a/LICENSE and b/LICENSE identical, b/LICENSE renamed to b/COPYING: b/LICENSE is the rename).
    /// </summary>
    private void PairPureRenames(List<FileEntry> removed, List<FileEntry> added, HashCache? leftCache, HashCache? rightCache,
        List<RenameOp> renames, ConcurrentDictionary<string, string> unreadable, ref long bytesRead, CancellationToken ct)
    {
        var removedSizes = removed.Where(r => r.Length > 0).Select(r => r.Length).ToHashSet();
        var addedSizes = added.Where(a => a.Length > 0).Select(a => a.Length).ToHashSet();
        var removedIds = Ids(removed, addedSizes, leftCache, unreadable, ref bytesRead, ct);
        var addedIds = Ids(added, removedSizes, rightCache, unreadable, ref bytesRead, ct);

        var addsByXx = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int ai = 0; ai < added.Count; ai++)
        {
            if (addedIds[ai] is not { } id) continue;
            if (!addsByXx.TryGetValue(id.XxHash, out var list)) addsByXx[id.XxHash] = list = [];
            list.Add(ai);
        }

        // Every identical (removed, added) pair, best first; each file is used once.
        var pairs = new List<(int R, int A)>();
        for (int ri = 0; ri < removed.Count; ri++)
            if (removedIds[ri] is { } rid && addsByXx.TryGetValue(rid.XxHash, out var list))
                foreach (var ai in list)
                    if (ContentId.Same(addedIds[ai]!.Value, rid) == true)
                        pairs.Add((ri, ai));
        pairs.Sort((x, y) =>
        {
            int c = Rank(x).CompareTo(Rank(y));
            if (c == 0) c = string.CompareOrdinal(removed[x.R].RelativePath, removed[y.R].RelativePath);
            if (c == 0) c = string.CompareOrdinal(added[x.A].RelativePath, added[y.A].RelativePath);
            return c;
        });

        var usedRemoved = new HashSet<int>();
        var usedAdded = new HashSet<int>();
        var found = new List<(int R, RenameOp Op)>();
        foreach (var (ri, ai) in pairs)
        {
            if (usedRemoved.Contains(ri) || usedAdded.Contains(ai)) continue;
            usedRemoved.Add(ri);
            usedAdded.Add(ai);
            found.Add((ri, new RenameOp(removed[ri].RelativePath, added[ai].RelativePath, 1000)));
        }
        // Listed in removed order, as before.
        renames.AddRange(found.OrderBy(f => f.R).Select(f => f.Op));

        var stillRemoved = removed.Where((_, i) => !usedRemoved.Contains(i)).ToList();
        var stillAdded = added.Where((_, i) => !usedAdded.Contains(i)).ToList();
        removed.Clear();
        removed.AddRange(stillRemoved);
        added.Clear();
        added.AddRange(stillAdded);

        int Rank((int R, int A) p) => (Name(added[p.A]) == Name(removed[p.R]) ? 0 : 2) + (Dir(added[p.A]) == Dir(removed[p.R]) ? 0 : 1);

        static string Name(FileEntry e) => e.RelativePath[(e.RelativePath.LastIndexOf('/') + 1)..];
        static string Dir(FileEntry e) => e.RelativePath[..Math.Max(0, e.RelativePath.LastIndexOf('/'))];
    }

    /// <summary>
    /// The content id of each entry whose (non-zero) size is in <paramref name="sizes"/>, else null. A trusted ledger
    /// entry is used as is; otherwise the file is hashed (in parallel, as the content phase does over SMB) and the
    /// hashes recorded. A file that can't be read gets null and its reason in <paramref name="unreadable"/>.
    /// </summary>
    private ContentId?[] Ids(List<FileEntry> entries, HashSet<long> sizes, HashCache? cache,
        ConcurrentDictionary<string, string> unreadable, ref long bytesRead, CancellationToken ct)
    {
        var ids = new ContentId?[entries.Count];
        long read = 0;
        Parallel.ForEachAsync(Enumerable.Range(0, entries.Count),
            new ParallelOptions { MaxDegreeOfParallelism = _options.Parallelism, CancellationToken = ct },
            async (i, ct) =>
            {
                var e = entries[i];
                if (e.Length == 0 || !sizes.Contains(e.Length)) return;
                if (cache is not null && cache.TryGet(e, out var cached) && cached.XxHash.Length > 0)
                {
                    ids[i] = cached;
                    return;
                }
                try
                {
                    var h = await PairComparer.HashAsync(e, ct).ConfigureAwait(false);
                    cache?.Record(e, h);
                    ids[i] = new ContentId(h.XxHash128, h.Sha256);
                    Interlocked.Add(ref read, e.Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadable[e.RelativePath] = ex.Message;
                }
            }).GetAwaiter().GetResult();
        bytesRead += read;
        return ids;
    }

    /// <summary>Pass 2: score remaining text pairs and assign greedily by descending similarity.</summary>
    private void PairEditedRenames(List<FileEntry> removed, List<FileEntry> added, List<RenameOp> renames, HashSet<string> edited,
        ConcurrentDictionary<string, string> unreadable, ref long bytesRead, CancellationToken ct)
    {
        // Read + EOL-normalize + count each candidate's lines once; skip binary / oversized (line metric needs text).
        var intern = new LineIds();
        var removedLines = ReadLines(removed, intern, unreadable, ref bytesRead, ct);
        var addedLines = ReadLines(added, intern, unreadable, ref bytesRead, ct);
        int threshold = _options.RenameSimilarityThresholdMilli;

        var perRemoved = new List<(int milli, int ri, int ai)>?[removed.Count];
        Parallel.For(0, removed.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, ri =>
        {
            if (removedLines[ri] is not { } rl) return;
            List<(int, int, int)>? found = null;
            for (int ai = 0; ai < added.Count; ai++)
            {
                if (addedLines[ai] is not { } al) continue;
                // common ≤ the smaller line count, so the counts alone can rule a pair out: no need to score it.
                int max = Math.Max(rl.Count, al.Count);
                if (max > 0 && Milli(Math.Min(rl.Count, al.Count), max) < threshold) continue;
                int milli = max == 0 ? 1000 : Milli(LineCounts.Common(rl, al), max);
                if (milli >= threshold)
                    (found ??= []).Add((milli, ri, ai));
            }
            perRemoved[ri] = found;
        });
        var scored = perRemoved.Where(l => l is not null).SelectMany(l => l!).ToList();

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
            edited.Add(added[ai].RelativePath);
            pairedR.Add(removed[ri].RelativePath);
            pairedA.Add(added[ai].RelativePath);
        }

        removed.RemoveAll(e => pairedR.Contains(e.RelativePath));
        added.RemoveAll(e => pairedA.Contains(e.RelativePath));
    }

    /// <summary>round(common / max * 1000) in exact integers — the same formula as <see cref="Similarity.MilliFromLines"/>.</summary>
    private static int Milli(int common, int max) => (int)(((long)common * 1000 + max / 2) / max);

    /// <summary>Each entry's line multiset, or null where the file is empty, binary, past the read cap or unreadable.
    /// Read in parallel; lines are interned so a pair's common count is a merge of two sorted id arrays.</summary>
    private LineCounts?[] ReadLines(IReadOnlyList<FileEntry> entries, LineIds intern,
        ConcurrentDictionary<string, string> unreadable, ref long bytesRead, CancellationToken ct)
    {
        var result = new LineCounts?[entries.Count];
        long read = 0;
        Parallel.For(0, entries.Count, new ParallelOptions { MaxDegreeOfParallelism = _options.Parallelism, CancellationToken = ct }, i =>
        {
            var e = entries[i];
            if (e.Length == 0 || e.Length > _options.MaxClassifyBytes || unreadable.ContainsKey(e.RelativePath)) return;

            byte[] bytes;
            try { bytes = TextInspector.ReadAll(e.FullPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable[e.RelativePath] = ex.Message;
                return;
            }
            Interlocked.Add(ref read, bytes.Length);
            if (bytes.Length == 0) return;
            int head = Math.Min(bytes.Length, TextInspector.HeadBytes);
            if (TextInspector.LooksBinary(bytes.AsSpan(0, head))) return;

            var normalized = TextInspector.NormalizeEol(TextInspector.Decode(bytes));
            result[i] = LineCounts.Of(LineText.SplitLines(normalized), intern);
        });
        bytesRead += read;
        return result;
    }

    /// <summary>A file's lines as a multiset: distinct interned line ids (ascending) with how often each occurs.</summary>
    private sealed class LineCounts
    {
        private int[] _ids = [];
        private int[] _counts = [];
        public int Count { get; private init; }

        public static LineCounts Of(string[] lines, LineIds intern)
        {
            var counts = new Dictionary<int, int>();
            foreach (var line in lines)
            {
                int id = intern.Of(line);
                counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
            }
            var ids = counts.Keys.ToArray();
            Array.Sort(ids);
            return new LineCounts { _ids = ids, _counts = ids.Select(id => counts[id]).ToArray(), Count = lines.Length };
        }

        /// <summary>Σ over shared lines of min(count in a, count in b) — the contract's multiset intersection.</summary>
        public static int Common(LineCounts a, LineCounts b)
        {
            int i = 0, j = 0, common = 0;
            while (i < a._ids.Length && j < b._ids.Length)
            {
                int c = a._ids[i].CompareTo(b._ids[j]);
                if (c < 0) i++;
                else if (c > 0) j++;
                else common += Math.Min(a._counts[i++], b._counts[j++]);
            }
            return common;
        }
    }

    /// <summary>One id per distinct line, for one detection (ids are arbitrary; only equality matters).</summary>
    private sealed class LineIds
    {
        private readonly ConcurrentDictionary<string, int> _ids = new(StringComparer.Ordinal);
        private int _next;

        public int Of(string line) => _ids.TryGetValue(line, out var id) ? id : _ids.GetOrAdd(line, _ => Interlocked.Increment(ref _next));
    }
}
