using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Walk;

namespace CodeDiffer.Core.Compare;

public sealed class CompareOptions
{
    /// <summary>Directory names skipped whole during the walk. Default ignores VCS metadata only.</summary>
    public IReadOnlyCollection<string> IgnoredDirectoryNames { get; init; } = [".git"];

    /// <summary>Above this per-file size a pair's reason is found by a streamed byte comparison, not by decoding it
    /// whole; above 64 MB it is content unchecked (see <see cref="ReasonClassifier"/>).</summary>
    public long MaxClassifyBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>Resolve added/removed pairs into renames (pure + edited). On by default.</summary>
    public bool DetectRenames { get; init; } = true;

    /// <summary>Minimum similarityMilli for an EDITED rename to be kept. 500 = git's -M50% default.</summary>
    public int RenameSimilarityThresholdMilli { get; init; } = 500;

    /// <summary>Edited renames are looked for only up to this many (removed × added) text candidates — every pair is
    /// scored, so the cost grows with the product (like git's renameLimit; at the bound, minutes: ~160 s for 5,000 × 5,000
    /// files of 2,000 lines, ~6 s for 1,000 × 1,000). Past it the pass is
    /// skipped and said (<see cref="CompareReport.RenameLimit"/>); pure renames are always found.</summary>
    public long MaxEditedRenamePairs { get; init; } = 5_000L * 5_000;

    /// <summary>... and up to this many bytes of candidate text, read and held at once to score them.</summary>
    public long MaxEditedRenameBytes { get; init; } = 1L << 30;

    /// <summary>
    /// Concurrent directory listings / file-pair reads per side. Over SMB, overlapping round-trips is what
    /// fills the link; locally it washes. Default min(cores, 8) — CodeCompass's measured SMB sweet spot.
    /// </summary>
    public int Parallelism { get; init; } = Math.Min(Environment.ProcessorCount, 8);

    /// <summary>Hash-ledger use: On (default; trust path+size+mtime), Off (--no-cache), Rehash (--rehash).</summary>
    public CacheMode Cache { get; init; } = CacheMode.On;

    /// <summary>
    /// Confirm every cache hit against the live HANDLE stat (~0.4 ms/file over SMB), not just the listing —
    /// a listing can be stale while a writer holds the file open. Default on; off = listing-only (faster).
    /// </summary>
    public bool StrictStat { get; init; } = true;

    /// <summary>Override for the ledger base dir (tests); default %LOCALAPPDATA%\CodeDiffer or CODEDIFFER_CACHE_DIR.</summary>
    public string? CacheBaseDir { get; init; }

    /// <summary>Override for where CodeCompass ledgers are looked up (tests); default its own base dir.</summary>
    public string? CodeCompassBaseDir { get; init; }

    /// <summary>Override for the ledger's trust margins (tests); default 3 s local / 1 h share, 3 s settle.</summary>
    internal TrustTiming? Timing { get; init; }

    /// <summary>Tests: called after each content pair a session's compare checks (to cancel at a known point).</summary>
    internal Action<long>? AfterPairChecked { get; init; }
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

    /// <param name="progress">Optional live progress, including the differences found so far (see <see cref="CompareProgress"/>).</param>
    /// <param name="sharedLeft">Optional: the left tree shared with another compare of the same left root. The first
    /// compare fills it (listing + content ids proven this run); a later one reuses them instead of re-listing and
    /// re-checking the left tree.</param>
    /// <param name="ct">Cancels the compare: it throws <see cref="OperationCanceledException"/> promptly (within a chunk
    /// per file in flight), after saving the hashes already read, so a re-run picks up from there.</param>
    public CompareReport Compare(string left, string right, CompareProgress? progress = null, SharedTree? sharedLeft = null,
        CancellationToken ct = default)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var timings = new List<(string, TimeSpan)>();
        void Phase(string name) { timings.Add((name, timer.Elapsed)); timer.Restart(); }

        InputValidation.ValidateTrees(left, right);
        Phase("validate");

        // Walk both sides at once — two independent trees (often two shares) shouldn't queue behind each other.
        progress?.SetPhase(ComparePhase.Walking);
        var walker = new TreeWalker(_options.IgnoredDirectoryNames, _options.Parallelism);
        Action<int>? listedL = progress is null ? null : n => progress.Listed(true, n);
        Action<int>? listedR = progress is null ? null : n => progress.Listed(false, n);
        var leftFull = Path.GetFullPath(left);
        var reused = sharedLeft?.For(leftFull);
        var leftWalk = reused is not null ? Task.FromResult(reused) : Task.Run(() => walker.WalkAll(left, listedL, ct), ct);
        var rightWalk = Task.Run(() => walker.WalkAll(right, listedR, ct), ct);
        var (lw, rw) = (leftWalk.GetAwaiter().GetResult(), rightWalk.GetAwaiter().GetResult());
        if (reused is not null) progress?.Listed(true, lw.Files.Count);
        else sharedLeft?.Begin(leftFull, lw);
        var shareIds = reused is null ? sharedLeft?.Ids : null; // filled by this compare for the next one
        int reusedFiles = 0;
        Phase("walk");
        progress?.SetPhase(ComparePhase.Pairing);
        var leftMap = lw.Files.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var rightMap = rw.Files.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var classifier = new ReasonClassifier(_options.MaxClassifyBytes);
        var leftCache = HashCache.Open(left, _options.Cache, _options.StrictStat, _options.CacheBaseDir, _options.CodeCompassBaseDir, _options.Timing,
            sharedLeft?.StartedUtcTicks ?? 0);
        var rightCache = HashCache.Open(right, _options.Cache, _options.StrictStat, _options.CacheBaseDir, _options.CodeCompassBaseDir, _options.Timing);
        long bytesRead = 0;

        var paths = new SortedSet<string>(StringComparer.Ordinal);
        paths.UnionWith(leftMap.Keys);
        paths.UnionWith(rightMap.Keys);

        var changes = new List<FileChange>(paths.Count);
        var removed = new List<FileEntry>();
        var added = new List<FileEntry>();
        var behindLink = new List<FileChange>();
        var sameSize = new List<(FileEntry L, FileEntry R)>();
        var sizeChanged = new List<(FileEntry L, FileEntry R)>();
        string? renameLimit = null;
        try
        {
            // A path behind a link the other tree has in its place (not followed) is not known to be added or removed.
            var leftLinks = lw.SkippedPaths.Where(s => s.Kind == SkipKind.Link).Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
            var rightLinks = rw.SkippedPaths.Where(s => s.Kind == SkipKind.Link).Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                bool inLeft = leftMap.TryGetValue(path, out var le);
                bool inRight = rightMap.TryGetValue(path, out var re);

                if (inLeft && !inRight && BehindLink(path, rightLinks) is { } rl)
                {
                    behindLink.Add(new FileChange(path, ChangeStatus.Removed, null, le.Length, 0, BehindLink: true,
                        Unreadable: $"the right tree has a symlink/junction at {rl} (not followed): whether it was removed is unknown"));
                    continue;
                }
                if (!inLeft && inRight && BehindLink(path, leftLinks) is { } ll)
                {
                    behindLink.Add(new FileChange(path, ChangeStatus.Added, null, 0, re.Length, BehindLink: true,
                        Unreadable: $"the left tree has a symlink/junction at {ll} (not followed): whether it was added is unknown"));
                    continue;
                }
                if (inLeft && !inRight) { removed.Add(le); continue; } // held back — may resolve into a rename
                if (!inLeft && inRight) { added.Add(re); continue; }

                if (le.Length == re.Length)
                    sameSize.Add((le, re)); // size is the cheap filter; same size still needs the bytes
                else
                    sizeChanged.Add((le, re)); // modified for sure; only the reason needs the bytes
            }
            if (progress is not null)
            {
                // Partial answers: adds and removes are known now (a rename may still pair some of them up).
                progress.Paired(paths.Count, sameSize.Count, sameSize.Sum(p => p.L.Length));
                foreach (var e in removed) progress.Add(new FileChange(e.RelativePath, ChangeStatus.Removed, null, e.Length, 0), _options.DetectRenames);
                foreach (var e in added) progress.Add(new FileChange(e.RelativePath, ChangeStatus.Added, null, 0, e.Length), _options.DetectRenames);
                foreach (var c in behindLink) progress.Add(c);
            }
            changes.AddRange(behindLink);

            // Classify the size-changed pairs concurrently: each is a couple of opens/reads, and over SMB those
            // round-trips must overlap, not stack (serially, ~33 files cost ~10 s against a real share).
            var classified = new FileChange[sizeChanged.Count];
            Parallel.For(0, sizeChanged.Count, new ParallelOptions { MaxDegreeOfParallelism = _options.Parallelism, CancellationToken = ct }, i =>
            {
                try { classified[i] = Modified(classifier, sizeChanged[i].L, sizeChanged[i].R, ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    classified[i] = Unknown(sizeChanged[i].L, sizeChanged[i].R, ex); // modified for sure; why is unknown
                }
                progress?.Add(classified[i]);
            });
            changes.AddRange(classified);

            // Same-size pairs: the only place bytes cross the wire. Biggest first so a 1.5 GB header starts
            // early instead of becoming the lone straggler at the end (top 2% of files ≈ 80% of the bytes).
            Phase("pair+classify size-changed");
            progress?.SetPhase(ComparePhase.Contents);
            sameSize.Sort((a, b) => b.L.Length.CompareTo(a.L.Length));
            var verdicts = new FileChange[sameSize.Count];
            Parallel.ForEachAsync(
                Enumerable.Range(0, sameSize.Count),
                new ParallelOptions { MaxDegreeOfParallelism = _options.Parallelism, CancellationToken = ct },
                async (i, ct) =>
                {
                    var (le, re) = sameSize[i];
                    long streamed = 0; // this pair's bytes already shown by progress as they were read
                    Action<int, int>? onChunk = progress is null ? null : (advance, bytes) => { streamed += advance; progress.Streamed(advance, bytes); };
                    bool hasL = false, hasR = false;
                    try
                    {
                        (verdicts[i], hasL, hasR) = await Pair(le, re, onChunk, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Locked, vanished or denied: one file must not cost the whole compare. Never "identical".
                        verdicts[i] = Unknown(le, re, ex);
                    }
                    if (progress is not null)
                    {
                        if (verdicts[i].Status != ChangeStatus.Identical) progress.Add(verdicts[i]);
                        progress.PairChecked(le.Length - streamed, 0, (hasL ? 1 : 0) + (hasR ? 1 : 0));
                    }
                }).GetAwaiter().GetResult();
            changes.AddRange(verdicts);

            async Task<(FileChange Verdict, bool HasL, bool HasR)> Pair(FileEntry le, FileEntry re, Action<int, int>? onChunk, CancellationToken ct)
            {
                bool equal;
                long read = 0;
                ContentId cl = default;
                bool shared = reused is not null && sharedLeft!.Ids.TryGetValue(le.RelativePath, out cl);
                if (shared) Interlocked.Increment(ref reusedFiles);
                bool hasL = shared || leftCache.TryGet(le, out cl);
                bool hasR = rightCache.TryGet(re, out var cr);
                ContentId? provenL = hasL ? cl : null; // the left id this run established, passed on via shareIds
                bool? cached = hasL && hasR ? ContentId.Same(cl, cr) : null;
                if (cached is { } same)
                {
                    equal = same; // both sides proven by trusted ledgers — no bytes cross the wire
                }
                else if (_options.Cache == CacheMode.Off)
                {
                    var r = await PairComparer.CompareAsync(le, re, hashes: false, ct, onChunk).ConfigureAwait(false);
                    equal = r.Equal;
                    read = 2 * le.Length; // upper bound (early exit on a difference)
                }
                else if (hasL != hasR)
                {
                    // One side cached: read only the other side, and compare hashes (SHA-256 when both have it).
                    var (e, cache, known) = hasL ? (re, rightCache, cl) : (le, leftCache, cr);
                    var h = await PairComparer.HashAsync(e, ct, onChunk).ConfigureAwait(false);
                    cache.Record(e, h);
                    equal = ContentId.Same(known, new ContentId(h.XxHash128, h.Sha256))!.Value;
                    if (!hasL && h.Stable) provenL = new ContentId(h.XxHash128, h.Sha256);
                    read = e.Length;
                }
                else
                {
                    // Neither usable: read both at once, compare bytes, and keep both hashes for next time.
                    var r = await PairComparer.CompareAsync(le, re, hashes: true, ct, onChunk).ConfigureAwait(false);
                    leftCache.Record(le, r.Left!);
                    rightCache.Record(re, r.Right!);
                    equal = r.Equal;
                    if (r.Left!.Stable) provenL = new ContentId(r.Left.XxHash128, r.Left.Sha256);
                    read = 2 * le.Length;
                }
                if (shareIds is not null && provenL is { } id) shareIds[le.RelativePath] = id;
                Interlocked.Add(ref bytesRead, read);
                var verdict = equal
                    ? new FileChange(le.RelativePath, ChangeStatus.Identical, null, le.Length, re.Length)
                    : Modified(classifier, le, re, ct);
                return (verdict, hasL, hasR);
            }

            Phase("same-size content");
            ct.ThrowIfCancellationRequested();
            progress?.SetPhase(ComparePhase.Renames);
            (long renameBytes, renameLimit) = AddResolvedAddsRemovesAndRenames(changes, removed, added, leftCache, rightCache, ct);
            bytesRead += renameBytes;
            progress?.RenamesRead(renameBytes);
            Phase("renames");
        }
        catch (Exception)
        {
            // Keep what was read: every hash recorded so far goes into the ledgers, so re-running a cancelled (or
            // failed) cold compare only reads what this one didn't get to.
            try { leftCache.Save(lw.Files, finished: false); rightCache.Save(rw.Files, finished: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }

        changes.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        progress?.SetPhase(ComparePhase.Saving);
        // The verdicts are done: a ledger that can't be saved (disk full, a file held by antivirus, no rights) costs the
        // next run a re-read, never this result.
        string? saveError = null;
        try
        {
            leftCache.Save(lw.Files);
            rightCache.Save(rw.Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saveError = ex.Message; }
        Phase("ledger save");
        progress?.SetPhase(ComparePhase.Done);

        return new CompareReport(changes, lw.DroppedDirectories, rw.DroppedDirectories)
        {
            SkippedLinks = lw.SkippedLinks + rw.SkippedLinks,
            LeftSkipped = lw.SkippedPaths,
            RightSkipped = rw.SkippedPaths,
            CacheSaveError = saveError,
            RenameLimit = renameLimit,
            CacheHits = leftCache.Hits + rightCache.Hits,
            CodeCompassHits = leftCache.CodeCompassHits + rightCache.CodeCompassHits,
            ReusedLeftFiles = reusedFiles,
            UnstableFiles = leftCache.Unstable + rightCache.Unstable,
            PendingFiles = leftCache.Pending + rightCache.Pending,
            ComparedPairs = sameSize.Count,
            BytesRead = bytesRead,
            Timings = timings,
        };
    }

    /// <summary>The link (one of <paramref name="links"/>) that <paramref name="path"/> is at or under, or null.</summary>
    private static string? BehindLink(string path, HashSet<string> links)
    {
        if (links.Count == 0) return null;
        for (var p = path; ; p = p[..p.LastIndexOf('/')])
        {
            if (links.Contains(p)) return p;
            if (p.LastIndexOf('/') < 0) return null;
        }
    }

    private static FileChange Modified(ReasonClassifier classifier, FileEntry le, FileEntry re, CancellationToken ct)
        => new(le.RelativePath, ChangeStatus.Modified,
            classifier.Classify(le.FullPath, le.Length, re.FullPath, re.Length, ct), le.Length, re.Length);

    /// <summary>A pair that could not be read: modified with no reason, and why.</summary>
    private static FileChange Unknown(FileEntry le, FileEntry re, Exception ex)
        => new(le.RelativePath, ChangeStatus.Modified, null, le.Length, re.Length, Unreadable: ex.Message);

    /// <summary>
    /// Turn the held-back left-only / right-only entries into Renamed changes (when enabled) plus the
    /// leftover Added / Removed. A rename's destination path is its RelativePath; its source rides in
    /// RenamedFrom. Left/right sizes are carried so the summary can show both ends of a move.
    /// </summary>
    /// <returns>The bytes rename detection read (ledger hits read none), and why edited renames weren't looked for.</returns>
    private (long Read, string? Limit) AddResolvedAddsRemovesAndRenames(List<FileChange> changes, List<FileEntry> removed, List<FileEntry> added,
        HashCache leftCache, HashCache rightCache, CancellationToken ct)
    {
        IReadOnlyList<FileEntry> leftoverRemoved = removed;
        IReadOnlyList<FileEntry> leftoverAdded = added;
        IReadOnlyDictionary<string, string>? unreadable = null;
        long read = 0;
        string? limit = null;

        if (_options.DetectRenames)
        {
            var result = new RenameDetector(_options).Detect(removed, added, leftCache, rightCache, ct);
            read = result.BytesRead;
            leftoverRemoved = result.UnmatchedRemoved;
            leftoverAdded = result.UnmatchedAdded;
            unreadable = result.Unreadable;
            limit = result.EditedSkipped;

            var removedByPath = removed.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
            var addedByPath = added.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
            foreach (var r in result.Renames)
            {
                long fromSize = removedByPath.TryGetValue(r.From, out var fe) ? fe.Length : 0;
                long toSize = addedByPath.TryGetValue(r.To, out var te) ? te.Length : 0;
                changes.Add(new FileChange(r.To, ChangeStatus.Renamed, null, fromSize, toSize, r.From, r.SimilarityMilli,
                    EditedRename: result.Edited?.Contains(r.To) ?? false));
            }
        }

        string? Why(FileEntry e) => unreadable is not null && unreadable.TryGetValue(e.RelativePath, out var why) ? why : null;
        foreach (var e in leftoverRemoved)
            changes.Add(new FileChange(e.RelativePath, ChangeStatus.Removed, null, e.Length, 0, Unreadable: Why(e)));
        foreach (var e in leftoverAdded)
            changes.Add(new FileChange(e.RelativePath, ChangeStatus.Added, null, 0, e.Length, Unreadable: Why(e)));
        return (read, limit);
    }
}
