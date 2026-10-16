using System.Collections.Concurrent;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Walk;

namespace CodeDiffer.Core.Compare;

/// <summary>A resolved rename plus the add/remove sets left over after pairing. <paramref name="Unreadable"/>:
/// relative path → why, for files that could not be read (left out of the pairing). <paramref name="BytesRead"/>:
/// what the pairing read (ledger hits read nothing). <paramref name="Edited"/>: the destination paths of the renames the
/// similarity pass found (their bytes differ, whatever the similarity rounds to). <paramref name="EditedSkipped"/>: why the
/// similarity pass was not run (too many candidates), or null.</summary>
public sealed record RenameResult(
    IReadOnlyList<RenameOp> Renames,
    IReadOnlyList<FileEntry> UnmatchedRemoved,
    IReadOnlyList<FileEntry> UnmatchedAdded,
    IReadOnlyDictionary<string, string>? Unreadable = null,
    long BytesRead = 0,
    IReadOnlySet<string>? Edited = null,
    string? EditedSkipped = null);

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
///      skipped, each file's lines are counted once (not per pair), and the scoring runs in parallel. Bounded: past
///      <see cref="CompareOptions.MaxEditedRenamePairs"/> candidate pairs or <see cref="CompareOptions.MaxEditedRenameBytes"/>
///      of their text the pass is skipped, and said, rather than run for minutes or hold gigabytes.
///
/// Decoys in the fixtures are near-duplicate ADDs whose source still exists on BOTH sides — those are
/// never in the removed set, so they can't be falsely paired. Among genuine remove/add candidates we
/// take the single best match per file, which is what keeps precision up when one removed file resembles
/// several adds. Giant/binary files only ever rename via the pure (SHA) pass.
///
/// Every rename found is listed (the contract's metric), but one too weak or too ambiguous to act on is HELD
/// (<see cref="RenameOp.Held"/>): compare3 and apply take it as the remove and the add it may be, so the other side's
/// edit is a conflict, never written into an unrelated file. Held: a tie (another file as good a match); a file with
/// fewer than <see cref="MinSignificant"/> significant lines (<see cref="SignificantLines"/>) that changed its name; an
/// edited rename whose significant lines are less than <see cref="FollowMilli"/> alike, unless they are at least the
/// threshold alike and the name or directory is kept. Shared boilerplate (a licence header, includes, braces) is
/// what the significant lines leave out.
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
        string? skipped = null;

        PairPureRenames(removedLeft, addedLeft, leftCache, rightCache, renames, unreadable, ref bytesRead, ct);
        if (removedLeft.Count > 0 && addedLeft.Count > 0 && (skipped = OverLimit(removedLeft, addedLeft, unreadable)) is null)
            PairEditedRenames(removedLeft, addedLeft, renames, edited, unreadable, ref bytesRead, ct);

        return new RenameResult(renames, removedLeft, addedLeft,
            new Dictionary<string, string>(unreadable, StringComparer.Ordinal), bytesRead, edited, skipped);
    }

    /// <summary>Why the similarity pass would cost too much (by the candidates' count and size, before reading any), or null.</summary>
    private string? OverLimit(List<FileEntry> removed, List<FileEntry> added, ConcurrentDictionary<string, string> unreadable)
    {
        bool Candidate(FileEntry e) => e.Length > 0 && e.Length <= _options.MaxClassifyBytes && !unreadable.ContainsKey(e.RelativePath);
        long r = 0, a = 0, bytes = 0;
        foreach (var e in removed) if (Candidate(e)) { r++; bytes += e.Length; }
        foreach (var e in added) if (Candidate(e)) { a++; bytes += e.Length; }
        if (r * a > _options.MaxEditedRenamePairs)
            return $"edited renames not looked for: {r:N0} removed × {a:N0} added candidates is past the limit of " +
                   $"{_options.MaxEditedRenamePairs:N0} pairs — they are listed as added and removed (identical renames are still found)";
        if (bytes > _options.MaxEditedRenameBytes)
            return $"edited renames not looked for: the {r + a:N0} candidates hold {bytes / (1024 * 1024):N0} MB of text, past the " +
                   $"limit of {_options.MaxEditedRenameBytes / (1024 * 1024):N0} MB — they are listed as added and removed (identical renames are still found)";
        return null;
    }

    /// <summary>
    /// Pass 1: pair byte-identical files. Consumes matched entries from both lists. Identical bytes means identical
    /// size, so only files whose size occurs on both sides are hashed. Empty files never pair (git skips them too):
    /// every empty file is identical to every other, so a pairing would be a guess — and a 3-way merge would follow
    /// it, moving the other side's edit into an unrelated file. A file that is only a BOM (an editor's "empty" .cs)
    /// is empty text, and never pairs either. Among identical files the pairs are taken best first —
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

        // The identical files, grouped by content: removed and added files with one XxHash128.
        var groups = new Dictionary<string, (List<int> R, List<int> A)>(StringComparer.Ordinal);
        for (int ai = 0; ai < added.Count; ai++)
        {
            if (addedIds[ai] is not { } id) continue;
            if (!groups.TryGetValue(id.XxHash, out var g)) groups[id.XxHash] = g = ([], []);
            g.A.Add(ai);
        }
        for (int ri = 0; ri < removed.Count; ri++)
            if (removedIds[ri] is { } rid && !(removed[ri].Length <= 4 && BomOnly.Contains(rid.XxHash)) && groups.TryGetValue(rid.XxHash, out var g))
                g.R.Add(ri);

        var rName = removed.Select(e => Name(e.RelativePath)).ToArray();
        var rDir = removed.Select(e => Dir(e.RelativePath)).ToArray();
        var aName = added.Select(e => Name(e.RelativePath)).ToArray();
        var aDir = added.Select(e => Dir(e.RelativePath)).ToArray();
        var usedRemoved = new bool[removed.Count];
        var usedAdded = new bool[added.Count];
        var found = new List<(int R, RenameOp Op)>();
        void Pair(int ri, int ai, string? held = null)
        {
            usedRemoved[ri] = usedAdded[ai] = true;
            found.Add((ri, new RenameOp(removed[ri].RelativePath, added[ai].RelativePath, 1000,
                held ?? (rName[ri] == aName[ai] ? null : TrivialPure(added[ai])))));
        }

        foreach (var (rs, adds) in groups.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (rs.Count == 0) continue;
            rs.Sort((x, y) => string.CompareOrdinal(removed[x].RelativePath, removed[y].RelativePath));
            adds.Sort((x, y) => string.CompareOrdinal(added[x].RelativePath, added[y].RelativePath));

            // One content (no two SHA-256s that differ under the one XxHash128): every pair is identical. Best first, as
            // one sort of every pair would take them, but in O(k) — thousands of identical files moved make millions of
            // pairs: same name and directory, then same name, then same directory, then any; at each, the removed files
            // in path order take the first free added file in path order.
            if (rs.Select(r => removedIds[r]!.Value.Sha256).Concat(adds.Select(a => addedIds[a]!.Value.Sha256))
                  .Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Count() <= 1)
            {
                foreach (var key in new Func<string, string, string>[] { (n, d) => n + "/" + d, (n, _) => n, (_, d) => d, (_, _) => "" })
                {
                    var free = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
                    foreach (var a in adds)
                        if (!usedAdded[a])
                        {
                            var k = key(aName[a], aDir[a]);
                            if (!free.TryGetValue(k, out var q)) free[k] = q = new Queue<int>();
                            q.Enqueue(a);
                        }
                    // Two removed files or two added files at the same level is a tie: they pair as before, but every pair
                    // of that level is held — which is the rename can't be told (M3).
                    var groupA = free.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);
                    var rest = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                    foreach (var r in rs)
                        if (!usedRemoved[r])
                        {
                            var k = key(rName[r], rDir[r]);
                            if (!rest.TryGetValue(k, out var l)) rest[k] = l = [];
                            l.Add(r);
                        }
                    foreach (var r in rs)
                    {
                        var k = key(rName[r], rDir[r]);
                        if (usedRemoved[r] || !free.TryGetValue(k, out var q) || q.Count == 0) continue;
                        int a = q.Dequeue();
                        int otherA = groupA[k].FirstOrDefault(x => x != a, -1), otherR = rest[k].FirstOrDefault(x => x != r, -1);
                        string? tie = otherA >= 0 ? Tied(added[otherA].RelativePath)
                                    : otherR >= 0 ? Tied(removed[otherR].RelativePath) : null;
                        Pair(r, a, tie);
                    }
                }
                continue;
            }

            // A hash collision: compare every pair of the group, best first.
            var pairs = new List<(int Rank, int R, int A)>();
            foreach (var r in rs)
                foreach (var a in adds)
                    if (ContentId.Same(addedIds[a]!.Value, removedIds[r]!.Value) == true)
                        pairs.Add((Rank(rName[r], rDir[r], aName[a], aDir[a]), r, a));
            foreach (var (_, r, a) in pairs.OrderBy(p => p.Rank)) // stable: rs and adds are in path order
                if (!usedRemoved[r] && !usedAdded[a])
                    Pair(r, a);
        }
        // Listed in removed order, as before.
        renames.AddRange(found.OrderBy(f => f.R).Select(f => f.Op));

        var stillRemoved = removed.Where((_, i) => !usedRemoved[i]).ToList();
        var stillAdded = added.Where((_, i) => !usedAdded[i]).ToList();
        removed.Clear();
        removed.AddRange(stillRemoved);
        added.Clear();
        added.AddRange(stillAdded);
    }

    /// <summary>How likely a pair is the rename, other things equal: the same name and directory first, then the same
    /// name (moved), then the same directory (renamed in place), then neither.</summary>
    private static int Rank(string removedName, string removedDir, string addedName, string addedDir)
        => (addedName == removedName ? 0 : 2) + (addedDir == removedDir ? 0 : 1);

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static string Dir(string path) => path[..Math.Max(0, path.LastIndexOf('/'))];

    /// <summary>The XxHash128 of each byte-order mark alone: a file with that content is empty text.</summary>
    private static readonly HashSet<string> BomOnly = new(
        new byte[][] { [0xEF, 0xBB, 0xBF], [0xFF, 0xFE], [0xFE, 0xFF], [0xFF, 0xFE, 0, 0], [0, 0, 0xFE, 0xFF] }
            .Select(b => Convert.ToHexString(System.IO.Hashing.XxHash128.Hash(b))), StringComparer.OrdinalIgnoreCase);

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
        var (removedLines, removedSig) = ReadLines(removed, intern, unreadable, ref bytesRead, ct);
        var (addedLines, addedSig) = ReadLines(added, intern, unreadable, ref bytesRead, ct);
        int threshold = _options.RenameSimilarityThresholdMilli;

        var rName = removed.Select(e => Name(e.RelativePath)).ToArray();
        var rDir = removed.Select(e => Dir(e.RelativePath)).ToArray();
        var aName = added.Select(e => Name(e.RelativePath)).ToArray();
        var aDir = added.Select(e => Dir(e.RelativePath)).ToArray();

        // Best first; ties broken by name and directory (as identical files are paired), then by path so assignment is
        // deterministic across runs/platforms. By path alone, a/x.c and b/x.c renamed to a/y.c and b/y.c, equally
        // edited, could cross-pair (a/x.c → b/y.c), and compare3 follows renames.
        int Order(int ri, (int milli, int ai) x, (int milli, int ai) y)
        {
            int c = y.milli.CompareTo(x.milli);
            if (c != 0) return c;
            c = Rank(rName[ri], rDir[ri], aName[x.ai], aDir[x.ai]).CompareTo(Rank(rName[ri], rDir[ri], aName[y.ai], aDir[y.ai]));
            return c != 0 ? c : string.CompareOrdinal(added[x.ai].RelativePath, added[y.ai].RelativePath);
        }

        // Each removed file's best few candidates, not every pair over the threshold: thousands of near-alike files
        // (Unity .meta, generated stubs) made millions of pairs to hold and sort. A row whose candidates all go to
        // other files is scanned again (for twice as many), so the assignment is the same greedy one as over every pair.
        var usedA = new bool[added.Count];
        var rows = new List<(int milli, int ai)>?[removed.Count];
        var more = new bool[removed.Count];
        var keep = new int[removed.Count];
        List<(int milli, int ai)> Row(int ri, int k, out bool truncated)
        {
            var row = new List<(int milli, int ai)>();
            truncated = false;
            var rl = removedLines[ri]!;
            var order = Comparer<(int milli, int ai)>.Create((x, y) => Order(ri, x, y));
            for (int ai = 0; ai < added.Count; ai++)
            {
                if (usedA[ai] || addedLines[ai] is not { } al) continue;
                // common ≤ the smaller line count, so the counts alone can rule a pair out: no need to score it.
                int max = Math.Max(rl.Count, al.Count);
                if (max > 0 && Milli(Math.Min(rl.Count, al.Count), max) < threshold) continue;
                int milli = max == 0 ? 1000 : Milli(LineCounts.Common(rl, al, NeedCommon(max, threshold)), max);
                if (milli < threshold) continue;
                var cand = (milli, ai);
                if (row.Count == k && Order(ri, cand, row[^1]) >= 0) { truncated = true; continue; }
                int at = row.BinarySearch(cand, order);
                row.Insert(at < 0 ? ~at : at, cand);
                if (row.Count > k) { row.RemoveAt(k); truncated = true; }
            }
            return row;
        }
        Parallel.For(0, removed.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, ri =>
        {
            if (removedLines[ri] is null) return;
            keep[ri] = TopCandidates;
            rows[ri] = Row(ri, keep[ri], out more[ri]);
        });

        // The heads of the rows, best first: a head whose added file is taken moves on to the row's next candidate.
        var queue = new PriorityQueue<int, (int milli, int ri, int ai)>(Comparer<(int milli, int ri, int ai)>.Create((x, y) =>
        {
            int c = y.milli.CompareTo(x.milli);
            if (c != 0) return c;
            c = Rank(rName[x.ri], rDir[x.ri], aName[x.ai], aDir[x.ai]).CompareTo(Rank(rName[y.ri], rDir[y.ri], aName[y.ai], aDir[y.ai]));
            if (c != 0) return c;
            c = string.CompareOrdinal(removed[x.ri].RelativePath, removed[y.ri].RelativePath);
            return c != 0 ? c : string.CompareOrdinal(added[x.ai].RelativePath, added[y.ai].RelativePath);
        }));
        var head = new int[removed.Count];
        // Which removed files' queued heads are on each added file (a tie on the added side), and the files a tie was
        // seen with: the other side of a tie is held too when it pairs, whichever way the greedy took it.
        var headsOn = new Dictionary<int, List<int>>();
        var tiedR = new Dictionary<int, string>();
        var tiedA = new Dictionary<int, string>();
        void Enqueue(int ri, int milli, int ai)
        {
            queue.Enqueue(ri, (milli, ri, ai));
            if (!headsOn.TryGetValue(ai, out var l)) headsOn[ai] = l = [];
            l.Add(ri);
        }
        for (int ri = 0; ri < removed.Count; ri++)
            if (rows[ri] is { Count: > 0 } row) Enqueue(ri, row[0].milli, row[0].ai);

        var pairedR = new HashSet<string>(StringComparer.Ordinal);
        var pairedA = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var ri, out var p))
        {
            ct.ThrowIfCancellationRequested();
            headsOn[p.ai].Remove(ri);
            if (!usedA[p.ai])
            {
                // A tie (M3): another free added file this one is as alike to and as near by name and directory, or
                // another removed file whose best is this added file as well. Taken as before (best first, then by
                // path), but held — and so is the other file of the tie, when it pairs.
                int rank = Rank(rName[ri], rDir[ri], aName[p.ai], aDir[p.ai]);
                bool Same(int milli, int r, int a) => milli == p.milli && Rank(rName[r], rDir[r], aName[a], aDir[a]) == rank;
                string? tie = tiedR.GetValueOrDefault(ri) ?? tiedA.GetValueOrDefault(p.ai);
                var cur = rows[ri]!;
                int h2 = head[ri] + 1;
                for (; h2 < cur.Count && cur[h2].milli == p.milli && tie is null; h2++)
                    if (!usedA[cur[h2].ai] && Same(cur[h2].milli, ri, cur[h2].ai))
                    {
                        tie = Tied(added[cur[h2].ai].RelativePath);
                        tiedA[cur[h2].ai] = Tied(removed[ri].RelativePath);
                    }
                if (tie is null && h2 == cur.Count && more[ri])
                {
                    // The kept candidates ran out still tied: the next free one decides (the same as with every candidate kept).
                    usedA[p.ai] = true;
                    var next = Row(ri, 1, out _);
                    usedA[p.ai] = false;
                    if (next is [var n] && Same(n.milli, ri, n.ai))
                    {
                        tie = Tied(added[n.ai].RelativePath);
                        tiedA[n.ai] = Tied(removed[ri].RelativePath);
                    }
                }
                if (tie is null)
                    foreach (var r2 in headsOn[p.ai])
                        if (Same(rows[r2]![head[r2]].milli, r2, p.ai))
                        {
                            tie = Tied(removed[r2].RelativePath);
                            tiedR[r2] = Tied(added[p.ai].RelativePath);
                            break;
                        }
                usedA[p.ai] = true;
                renames.Add(new RenameOp(removed[ri].RelativePath, added[p.ai].RelativePath, p.milli,
                    tie ?? WeakEdited(removedSig[ri]!, addedSig[p.ai]!, rName[ri] == aName[p.ai], rDir[ri] == aDir[p.ai], threshold)));
                edited.Add(added[p.ai].RelativePath);
                pairedR.Add(removed[ri].RelativePath);
                pairedA.Add(added[p.ai].RelativePath);
                continue;
            }
            var row = rows[ri]!;
            int h = head[ri] + 1;
            while (h < row.Count && usedA[row[h].ai]) h++;
            if (h == row.Count)
            {
                if (!more[ri]) continue;
                keep[ri] *= 2;
                rows[ri] = row = Row(ri, keep[ri], out more[ri]);
                h = 0;
                if (row.Count == 0) continue;
            }
            head[ri] = h;
            Enqueue(ri, row[h].milli, row[h].ai);
        }

        removed.RemoveAll(e => pairedR.Contains(e.RelativePath));
        added.RemoveAll(e => pairedA.Contains(e.RelativePath));
    }

    /// <summary>Significant lines alike (per mille) at or past which an edited rename is followed whatever its name.</summary>
    internal const int FollowMilli = 800;

    /// <summary>Fewer significant lines than this (the smaller file's), and a file that changed its name is too small to tell.</summary>
    internal const int MinSignificant = 3;

    private const string NotFollowed = "not followed by compare3 or apply: ";

    private static string Tied(string other) => NotFollowed + $"tied with {other}, as good a match";

    /// <summary>Why an edited rename is held, or null (followed). Judged on the significant lines alone.</summary>
    private static string? WeakEdited(LineCounts from, LineCounts to, bool sameName, bool sameDir, int threshold)
    {
        int small = Math.Min(from.Count, to.Count), max = Math.Max(from.Count, to.Count);
        if (small < MinSignificant && !sameName)
            return NotFollowed + $"too small to tell ({small} significant line{(small == 1 ? "" : "s")})";
        int milli = max == 0 ? 1000 : Milli(LineCounts.Common(from, to), max);
        if (milli >= FollowMilli || (milli >= threshold && (sameName || sameDir))) return null;
        return NotFollowed + $"only {milli / 10.0:0.#}% alike past shared boilerplate (comments, braces), " +
               (milli >= threshold ? "with another name and directory" : $"under {threshold / 10.0:0.#}%");
    }

    /// <summary>Why a byte-identical rename to another name is held — a file too small to tell (a one-line header, a
    /// stub) — or null. Only a small file is read for it (identical bytes: the added side's).</summary>
    private static string? TrivialPure(FileEntry added)
    {
        if (added.Length > TrivialBytes) return null;
        try
        {
            var bytes = TextInspector.ReadAll(added.FullPath);
            int n = TextInspector.LooksBinary(bytes) ? (bytes.Length < 64 ? 0 : MinSignificant)
                  : SignificantLines(TextInspector.Decode(bytes)).Take(MinSignificant).Count();
            return n < MinSignificant ? NotFollowed + $"too small to tell ({n} significant line{(n == 1 ? "" : "s")})" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // as before: a read that fails here never changes the listing
        }
    }

    /// <summary>A byte-identical rename this size or smaller is read to see if it is trivial; past it, it isn't.</summary>
    private const int TrivialBytes = 4096;

    /// <summary>
    /// A text's significant lines, trimmed: past its leading comment block (blank lines, <c>//</c>, <c>/* … */</c>,
    /// <c>*</c>, <c>--</c>, <c>&lt;!--</c>, and <c>#</c> followed by a space, <c>!</c> or <c>#</c> — a licence header, a
    /// shebang — but not <c>#include</c>), and only lines with a letter or digit (not braces, blank lines). What a rename
    /// is followed by: a licence, includes and braces shared by two unrelated files are not.
    /// </summary>
    internal static IEnumerable<string> SignificantLines(string text)
    {
        bool lead = true, inBlock = false;
        foreach (var raw in LineText.NormalizedLines(text))
        {
            var t = raw.Trim();
            if (lead)
            {
                if (inBlock)
                {
                    if (t.Contains("*/", StringComparison.Ordinal)) inBlock = false;
                    continue;
                }
                if (t.Length == 0) continue;
                if (LeadingComment(t))
                {
                    if (t.StartsWith("/*", StringComparison.Ordinal) && !t.AsSpan(2).Contains("*/", StringComparison.Ordinal)) inBlock = true;
                    continue;
                }
                lead = false;
            }
            foreach (var ch in t)
                if (char.IsLetterOrDigit(ch)) { yield return t; break; }
        }
    }

    private static bool LeadingComment(string t)
        => t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal) || t[0] == '*'
           || t.StartsWith("--", StringComparison.Ordinal) || t.StartsWith("<!--", StringComparison.Ordinal)
           || (t[0] == '#' && (t.Length == 1 || t[1] is ' ' or '\t' or '!' or '#'));

    /// <summary>How many candidates of each removed file the edited-rename pass keeps at first (a test sets it to 1).</summary>
    internal int TopCandidates { get; init; } = 8;

    /// <summary>round(common / max * 1000) in exact integers — the same formula as <see cref="Similarity.MilliFromLines"/>.</summary>
    private static int Milli(int common, int max) => (int)(((long)common * 1000 + max / 2) / max);

    /// <summary>The fewest common lines that score at least the threshold against <paramref name="max"/> lines
    /// (<see cref="Milli"/> rises with the common count).</summary>
    private static int NeedCommon(int max, int threshold)
    {
        int lo = 0, hi = max;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Milli(mid, max) >= threshold) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>Each entry's line multiset, or null where the file is empty (or only a BOM), binary, past the read cap or unreadable.
    /// Read in parallel; lines are interned so a pair's common count is a merge of two sorted id arrays.</summary>
    private (LineCounts?[] All, LineCounts?[] Significant) ReadLines(IReadOnlyList<FileEntry> entries, LineIds intern,
        ConcurrentDictionary<string, string> unreadable, ref long bytesRead, CancellationToken ct)
    {
        var result = new LineCounts?[entries.Count];
        var significant = new LineCounts?[entries.Count];
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
            if (TextInspector.LooksBinary(bytes)) return;

            var text = TextInspector.Decode(bytes);
            bytes = null!;
            if (text.Length == 0) return; // only a BOM: empty text pairs with nothing, as an empty file
            result[i] = LineCounts.Of(LineText.NormalizedLines(text), intern);
            significant[i] = LineCounts.Of(SignificantLines(text), intern);
        });
        bytesRead += read;
        return (result, significant);
    }

    /// <summary>A file's lines as a multiset: distinct interned line ids (ascending) with how often each occurs.</summary>
    private sealed class LineCounts
    {
        private int[] _ids = [];
        private int[] _counts = [];
        public int Count { get; private init; }

        public static LineCounts Of(IEnumerable<string> lines, LineIds intern)
        {
            var counts = new Dictionary<int, int>();
            int count = 0;
            foreach (var line in lines)
            {
                int id = intern.Of(line);
                counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
                count++;
            }
            var ids = counts.Keys.ToArray();
            Array.Sort(ids);
            return new LineCounts { _ids = ids, _counts = ids.Select(id => counts[id]).ToArray(), Count = count };
        }

        /// <summary>Σ over shared lines of min(count in a, count in b) — the contract's multiset intersection. Once
        /// even the lines not yet merged could not lift it to <paramref name="need"/>, some count below that.</summary>
        public static int Common(LineCounts a, LineCounts b, int need = 0)
        {
            int i = 0, j = 0, common = 0, restA = a.Count, restB = b.Count;
            while (i < a._ids.Length && j < b._ids.Length)
            {
                if (common + Math.Min(restA, restB) < need) return common;
                int c = a._ids[i].CompareTo(b._ids[j]);
                if (c < 0) restA -= a._counts[i++];
                else if (c > 0) restB -= b._counts[j++];
                else
                {
                    common += Math.Min(a._counts[i], b._counts[j]);
                    restA -= a._counts[i++];
                    restB -= b._counts[j++];
                }
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
