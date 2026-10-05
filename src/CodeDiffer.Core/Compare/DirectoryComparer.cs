using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Walk;

namespace CodeDiffer.Core.Compare;

public sealed class CompareOptions
{
    /// <summary>Directory names skipped whole during the walk. Default ignores VCS metadata only.</summary>
    public IReadOnlyCollection<string> IgnoredDirectoryNames { get; init; } = [".git"];

    /// <summary>Above this per-file size, reason classification stays coarse (content), not decoded.</summary>
    public long MaxClassifyBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>Resolve added/removed pairs into renames (pure + edited). On by default.</summary>
    public bool DetectRenames { get; init; } = true;

    /// <summary>Minimum similarityMilli for an EDITED rename to be kept. 500 = git's -M50% default.</summary>
    public int RenameSimilarityThresholdMilli { get; init; } = 500;

    /// <summary>
    /// Concurrent directory listings / file-pair reads per side. Over SMB, overlapping round-trips is what
    /// fills the link; locally it washes. Default min(cores, 8) — CodeCompass's measured SMB sweet spot.
    /// </summary>
    public int Parallelism { get; init; } = Math.Min(Environment.ProcessorCount, 8);

    /// <summary>Hash-ledger use: On (default; trust path+size+mtime), Off (--no-cache), Rehash (--rehash).</summary>
    public CacheMode Cache { get; init; } = CacheMode.On;

    /// <summary>Override for the ledger base dir (tests); default %LOCALAPPDATA%\CodeDiffer or CODEDIFFER_CACHE_DIR.</summary>
    public string? CacheBaseDir { get; init; }

    /// <summary>Override for where CodeCompass ledgers are looked up (tests); default its own base dir.</summary>
    public string? CodeCompassBaseDir { get; init; }
}

/// <summary>
/// The 2-way directory compare (Tiers 0–2 of the cost funnel): walk both trees, pair by relative
/// path, and classify each path as identical / added / removed / modified — confirming content by
/// SHA-256 (size is the first-pass filter) and labeling every modified file with its reason.
///
/// v1 is serial and hashes same-size pairs fully (correct, re-run cost amortized by a future hash
/// cache). Rename detection, bounded-parallel SMB reads, the hash cache, and lazy per-file hunks are
/// subsequent increments — see DESIGN.txt.
/// </summary>
public sealed class DirectoryComparer
{
    private readonly CompareOptions _options;

    public DirectoryComparer(CompareOptions? options = null) => _options = options ?? new CompareOptions();

    public CompareReport Compare(string left, string right)
    {
        InputValidation.ValidateTrees(left, right);

        // Walk both sides at once — two independent trees (often two shares) shouldn't queue behind each other.
        var walker = new TreeWalker(_options.IgnoredDirectoryNames, _options.Parallelism);
        var leftWalk = Task.Run(() => walker.WalkAll(left));
        var rightWalk = Task.Run(() => walker.WalkAll(right));
        var (lw, rw) = (leftWalk.GetAwaiter().GetResult(), rightWalk.GetAwaiter().GetResult());
        var leftMap = lw.Files.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var rightMap = rw.Files.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var classifier = new ReasonClassifier(_options.MaxClassifyBytes);
        var leftCache = HashCache.Open(left, _options.Cache, _options.CacheBaseDir, _options.CodeCompassBaseDir);
        var rightCache = HashCache.Open(right, _options.Cache, _options.CacheBaseDir, _options.CodeCompassBaseDir);
        long bytesRead = 0;

        var paths = new SortedSet<string>(StringComparer.Ordinal);
        paths.UnionWith(leftMap.Keys);
        paths.UnionWith(rightMap.Keys);

        var changes = new List<FileChange>(paths.Count);
        var removed = new List<FileEntry>();
        var added = new List<FileEntry>();
        var sameSize = new List<(FileEntry L, FileEntry R)>();
        foreach (var path in paths)
        {
            bool inLeft = leftMap.TryGetValue(path, out var le);
            bool inRight = rightMap.TryGetValue(path, out var re);

            if (inLeft && !inRight) { removed.Add(le); continue; } // held back — may resolve into a rename
            if (!inLeft && inRight) { added.Add(re); continue; }

            if (le.Length == re.Length)
                sameSize.Add((le, re)); // size is the cheap filter; same size still needs the bytes
            else
                changes.Add(Modified(classifier, le, re));
        }

        // Same-size pairs: the only place bytes cross the wire. Biggest first so a 1.5 GB header starts
        // early instead of becoming the lone straggler at the end (top 2% of files ≈ 80% of the bytes).
        sameSize.Sort((a, b) => b.L.Length.CompareTo(a.L.Length));
        var verdicts = new FileChange[sameSize.Count];
        Parallel.ForEachAsync(
            Enumerable.Range(0, sameSize.Count),
            new ParallelOptions { MaxDegreeOfParallelism = _options.Parallelism },
            async (i, ct) =>
            {
                var (le, re) = sameSize[i];
                bool equal;
                bool hasL = leftCache.TryGet(le, out var hl);
                bool hasR = rightCache.TryGet(re, out var hr);
                if (hasL && hasR)
                {
                    equal = hl == hr; // both sides proven by trusted ledgers — no bytes cross the wire
                }
                else if (_options.Cache == CacheMode.Off)
                {
                    var r = await PairComparer.CompareAsync(le.FullPath, re.FullPath, hashes: false, ct).ConfigureAwait(false);
                    equal = r.Equal;
                    Interlocked.Add(ref bytesRead, 2 * le.Length); // upper bound (early exit on a difference)
                }
                else if (hasL || hasR)
                {
                    // One side cached: read only the other side, and compare hashes.
                    var (e, cache) = hasL ? (re, rightCache) : (le, leftCache);
                    var h = await PairComparer.HashAsync(e.FullPath, ct).ConfigureAwait(false);
                    cache.Record(e, h);
                    equal = h == (hasL ? hl : hr);
                    Interlocked.Add(ref bytesRead, e.Length);
                }
                else
                {
                    // Neither cached: read both at once, compare bytes, and keep both hashes for next time.
                    var r = await PairComparer.CompareAsync(le.FullPath, re.FullPath, hashes: true, ct).ConfigureAwait(false);
                    leftCache.Record(le, r.LeftHash!);
                    rightCache.Record(re, r.RightHash!);
                    equal = r.Equal;
                    Interlocked.Add(ref bytesRead, 2 * le.Length);
                }
                verdicts[i] = equal
                    ? new FileChange(le.RelativePath, ChangeStatus.Identical, null, le.Length, re.Length)
                    : Modified(classifier, le, re);
            }).GetAwaiter().GetResult();
        changes.AddRange(verdicts);

        AddResolvedAddsRemovesAndRenames(changes, removed, added);

        changes.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        leftCache.Save(lw.Files);
        rightCache.Save(rw.Files);

        return new CompareReport(changes, lw.DroppedDirectories, rw.DroppedDirectories)
        {
            CacheHits = leftCache.Hits + rightCache.Hits,
            CodeCompassHits = leftCache.CodeCompassHits + rightCache.CodeCompassHits,
            ComparedPairs = sameSize.Count,
            BytesRead = bytesRead,
        };
    }

    private static FileChange Modified(ReasonClassifier classifier, FileEntry le, FileEntry re)
        => new(le.RelativePath, ChangeStatus.Modified,
            classifier.Classify(le.FullPath, le.Length, re.FullPath, re.Length), le.Length, re.Length);

    /// <summary>
    /// Turn the held-back left-only / right-only entries into Renamed changes (when enabled) plus the
    /// leftover Added / Removed. A rename's destination path is its RelativePath; its source rides in
    /// RenamedFrom. Left/right sizes are carried so the summary can show both ends of a move.
    /// </summary>
    private void AddResolvedAddsRemovesAndRenames(List<FileChange> changes, List<FileEntry> removed, List<FileEntry> added)
    {
        IReadOnlyList<FileEntry> leftoverRemoved = removed;
        IReadOnlyList<FileEntry> leftoverAdded = added;

        if (_options.DetectRenames)
        {
            var result = new RenameDetector(_options).Detect(removed, added);
            leftoverRemoved = result.UnmatchedRemoved;
            leftoverAdded = result.UnmatchedAdded;

            var removedByPath = removed.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
            var addedByPath = added.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
            foreach (var r in result.Renames)
            {
                long fromSize = removedByPath.TryGetValue(r.From, out var fe) ? fe.Length : 0;
                long toSize = addedByPath.TryGetValue(r.To, out var te) ? te.Length : 0;
                changes.Add(new FileChange(r.To, ChangeStatus.Renamed, null, fromSize, toSize, r.From, r.SimilarityMilli));
            }
        }

        foreach (var e in leftoverRemoved)
            changes.Add(new FileChange(e.RelativePath, ChangeStatus.Removed, null, e.Length, 0));
        foreach (var e in leftoverAdded)
            changes.Add(new FileChange(e.RelativePath, ChangeStatus.Added, null, 0, e.Length));
    }
}
