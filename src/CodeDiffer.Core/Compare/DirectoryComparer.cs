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

    /// <summary>--rehash-side: with the cache on, these trees (full paths) are re-read and their cache refreshed, as
    /// --rehash does, while the other trees' cache is used. <see cref="RehashSides"/> names them as given (left,
    /// right; base, v1, v2) for the summary.</summary>
    public IReadOnlyList<string> RehashRoots { get; init; } = [];
    public string RehashSides { get; init; } = "";

    /// <summary>The cache mode as a summary shows it: on, off, rehash, or "on, rehash v1".</summary>
    public string CacheLabel => Cache.ToString().ToLowerInvariant() + (RehashSides.Length > 0 ? $", rehash {RehashSides}" : "");

    /// <summary>The cache mode for one tree: <see cref="Cache"/>, or Rehash for a tree in <see cref="RehashRoots"/>.</summary>
    internal CacheMode CacheFor(string root)
    {
        if (Cache != CacheMode.On || RehashRoots.Count == 0) return Cache;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return RehashRoots.Any(r => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)), full, cmp)) ? CacheMode.Rehash : Cache;
    }

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

    /// <summary>How often, at least, the hash ledgers are saved while contents are read (a killed run keeps what it
    /// read up to the last save). Slower when a save is slow: never more than about 5% of the time.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a compare waits, paused, for trees that stopped answering (a share gone quiet, a dropped
    /// session) before it stops with what it read kept (see <see cref="ShareGuard"/>). Default 30 minutes.</summary>
    public TimeSpan ShareWait { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Tests: the waits before a file that alone failed is tried again (default 1 s, 4 s), and between
    /// checks of trees that stopped answering (default 1 s growing to 30 s).</summary>
    internal IReadOnlyList<TimeSpan>? RetryDelays { get; init; }
    internal IReadOnlyList<TimeSpan>? ProbeDelays { get; init; }
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
        // Both walks are in ordinal path order: pair them by a merge, not by maps and a sorted union of every path.
        var leftFiles = Ordered(lw.Files);
        var rightFiles = Ordered(rw.Files);
        var classifier = new ReasonClassifier(_options.MaxClassifyBytes);
        var leftCache = HashCache.Open(left, _options.CacheFor(left), _options.StrictStat, _options.CacheBaseDir, _options.CodeCompassBaseDir, _options.Timing,
            sharedLeft?.StartedUtcTicks ?? 0);
        var rightCache = HashCache.Open(right, _options.CacheFor(right), _options.StrictStat, _options.CacheBaseDir, _options.CodeCompassBaseDir, _options.Timing);
        var guard = new ShareGuard([left, right], _options, progress);
        long bytesRead = 0;
        int retriedAtEnd = 0;

        int pathCount = 0;
        var changes = new List<FileChange>(Math.Max(leftFiles.Count, rightFiles.Count));
        var removed = new List<FileEntry>();
        var added = new List<FileEntry>();
        var behindLink = new List<FileChange>();
        // Pairs as indices into the two walks: a pair of entries held twice over cost 96 B each, for the whole compare.
        var sameSize = new List<(int L, int R)>();
        var sizeChanged = new List<(int L, int R)>();
        string? renameLimit = null;
        var io = progress?.Io;
        // From here on hashes are recorded: saved now and then, so a run killed outright keeps what it read.
        using var checkpoint = _options.Cache == CacheMode.Off ? null : new LedgerCheckpoint(
            () => { leftCache.Save(lw.Files, finished: false); rightCache.Save(rw.Files, finished: false); },
            () => leftCache.Recorded + rightCache.Recorded, _options.CheckpointInterval, progress);
        try
        {
            // A path behind a link the other tree has in its place (not followed), or under a directory the other tree
            // could not list, is not known to be added or removed.
            var leftLinks = lw.SkippedPaths.Where(s => s.Kind != SkipKind.Name).ToDictionary(s => s.Path, s => s.Kind, StringComparer.Ordinal);
            var rightLinks = rw.SkippedPaths.Where(s => s.Kind != SkipKind.Name).ToDictionary(s => s.Path, s => s.Kind, StringComparer.Ordinal);
            var linkNotes = new Dictionary<(string Link, bool Left), string>(); // one note a link, shared by every path behind it
            string LinkNote(string link, bool left) => linkNotes.TryGetValue((link, left), out var n) ? n : linkNotes[(link, left)] =
                (left ? leftLinks : rightLinks)[link] == SkipKind.Unlisted
                ? $"the {(left ? "left" : "right")} tree's directory {link} could not be listed: whether it was {(left ? "added" : "removed")} is unknown"
                : left
                ? $"the left tree has a symlink/junction at {link} (not followed): whether it was added is unknown"
                : $"the right tree has a symlink/junction at {link} (not followed): whether it was removed is unknown";
            for (int li = 0, ri = 0; li < leftFiles.Count || ri < rightFiles.Count;)
            {
                int c = li == leftFiles.Count ? 1 : ri == rightFiles.Count ? -1
                      : string.CompareOrdinal(leftFiles[li].RelativePath, rightFiles[ri].RelativePath);
                bool inLeft = c <= 0, inRight = c >= 0;
                int lx = li, rx = ri;
                var le = inLeft ? leftFiles[li++] : default;
                var re = inRight ? rightFiles[ri++] : default;
                var path = inLeft ? le.RelativePath : re.RelativePath;
                pathCount++;

                if (inLeft && !inRight && BehindLink(path, rightLinks) is { } rl)
                {
                    behindLink.Add(new FileChange(path, ChangeStatus.Removed, null, le.Length, 0, BehindLink: true, Unreadable: LinkNote(rl, left: false)));
                    continue;
                }
                if (!inLeft && inRight && BehindLink(path, leftLinks) is { } ll)
                {
                    behindLink.Add(new FileChange(path, ChangeStatus.Added, null, 0, re.Length, BehindLink: true, Unreadable: LinkNote(ll, left: true)));
                    continue;
                }
                if (inLeft && !inRight) { removed.Add(le); continue; } // held back — may resolve into a rename
                if (!inLeft && inRight) { added.Add(re); continue; }

                if (le.Length == re.Length)
                    sameSize.Add((lx, rx)); // size is the cheap filter; same size still needs the bytes
                else
                    sizeChanged.Add((lx, rx)); // modified for sure; only the reason needs the bytes
            }
            behindLink.AddRange(Unopenable(lw.SkippedPaths, rw.SkippedPaths));
            if (progress is not null)
            {
                // Partial answers: adds and removes are known now (a rename may still pair some of them up).
                progress.Paired(pathCount, sameSize.Count, sameSize.Sum(p => leftFiles[p.L].Length));
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
                var (le, re) = (leftFiles[sizeChanged[i].L], rightFiles[sizeChanged[i].R]);
                try { classified[i] = guard.Run(le.FullPath, () => Modified(classifier, le, re, ct), ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    classified[i] = Unknown(le, re, ex); // modified for sure; why is unknown
                }
                progress?.Add(classified[i]);
            });
            changes.AddRange(classified);

            // Same-size pairs: the only place bytes cross the wire. Biggest first so a 1.5 GB header starts
            // early instead of becoming the lone straggler at the end (top 2% of files ≈ 80% of the bytes).
            Phase("pair+classify size-changed");
            progress?.SetPhase(ComparePhase.Contents);
            sameSize.Sort((a, b) => leftFiles[b.L].Length.CompareTo(leftFiles[a.L].Length));
            var verdicts = new FileChange[sameSize.Count];
            Parallel.ForEachAsync(
                Enumerable.Range(0, sameSize.Count),
                new ParallelOptions { MaxDegreeOfParallelism = _options.Parallelism, CancellationToken = ct },
                async (i, ct) =>
                {
                    var (le, re) = (leftFiles[sameSize[i].L], rightFiles[sameSize[i].R]);
                    long streamed = 0; // this pair's bytes already shown by progress as they were read
                    Action<int, int>? onChunk = progress is null ? null : (advance, bytes) => { streamed += advance; progress.Streamed(advance, bytes); };
                    bool hasL = false, hasR = false;
                    try
                    {
                        (verdicts[i], hasL, hasR) = await guard.RunAsync(le.FullPath, () => Pair(le, re, onChunk, ct), ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Locked, vanished or denied: one file must not cost the whole compare. Never "identical".
                        verdicts[i] = Unknown(le, re, ex);
                    }
                    if (progress is not null)
                    {
                        if (verdicts[i].Status != ChangeStatus.Identical) progress.Add(verdicts[i]);
                        progress.PairChecked(Math.Max(0, le.Length - streamed), 0, (hasL ? 1 : 0) + (hasR ? 1 : 0));
                    }
                }).GetAwaiter().GetResult();
            // One more try for each pair that couldn't be read, now that the rest is done: a file locked for a
            // moment, or read while the trees were briefly unreachable, is usually readable by now.
            for (int i = 0; i < verdicts.Length; i++)
            {
                if (verdicts[i].Unreadable is null) continue;
                var (le, re) = (leftFiles[sameSize[i].L], rightFiles[sameSize[i].R]);
                try
                {
                    verdicts[i] = guard.RunAsync(le.FullPath, () => Pair(le, re, null, ct), ct).GetAwaiter().GetResult().Verdict;
                    retriedAtEnd++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { verdicts[i] = Unknown(le, re, ex); }
            }
            changes.AddRange(verdicts);

            async Task<(FileChange Verdict, bool HasL, bool HasR)> Pair(FileEntry le, FileEntry re, Action<int, int>? onChunk, CancellationToken ct)
            {
                bool equal;
                long read = 0;
                byte[]? readL = null, readR = null; // a small differing pair's bytes, just read: classified from them
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
                    var r = await PairComparer.CompareAsync(le, re, hashes: false, ct, onChunk, io, keepSmall: true).ConfigureAwait(false);
                    equal = r.Equal;
                    (readL, readR) = (r.LeftBytes, r.RightBytes);
                    read = 2 * le.Length; // upper bound (early exit on a difference)
                }
                else if (hasL != hasR)
                {
                    // One side cached: read only the other side, and compare hashes (SHA-256 when both have it).
                    var (e, cache, known) = hasL ? (re, rightCache, cl) : (le, leftCache, cr);
                    var h = await PairComparer.HashAsync(e, ct, onChunk, io).ConfigureAwait(false);
                    cache.Record(e, h);
                    equal = ContentId.Same(known, new ContentId(h.XxHash128, h.Sha256))!.Value;
                    if (!hasL && h.Stable) provenL = new ContentId(h.XxHash128, h.Sha256);
                    read = e.Length;
                }
                else
                {
                    // Neither usable: read both at once, compare bytes, and keep both hashes for next time.
                    var r = await PairComparer.CompareAsync(le, re, hashes: true, ct, onChunk, io, keepSmall: true).ConfigureAwait(false);
                    leftCache.Record(le, r.Left!);
                    rightCache.Record(re, r.Right!);
                    equal = r.Equal;
                    (readL, readR) = (r.LeftBytes, r.RightBytes);
                    if (r.Left!.Stable) provenL = new ContentId(r.Left.XxHash128, r.Left.Sha256);
                    read = 2 * le.Length;
                }
                if (shareIds is not null && provenL is { } id) shareIds[le.RelativePath] = id;
                Interlocked.Add(ref bytesRead, read);
                var verdict = equal
                    ? new FileChange(le.RelativePath, ChangeStatus.Identical, null, le.Length, re.Length)
                    : readL is not null && readR is not null
                        ? new FileChange(le.RelativePath, ChangeStatus.Modified, classifier.Classify(le.FullPath, readL, re.FullPath, readR, ct), le.Length, re.Length)
                        : Modified(classifier, le, re, ct);
                return (verdict, hasL, hasR);
            }

            Phase("same-size content");
            ct.ThrowIfCancellationRequested();
            progress?.SetPhase(ComparePhase.Renames);
            (long renameBytes, renameLimit) = AddResolvedAddsRemovesAndRenames(changes, removed, added, leftCache, rightCache, guard, ct);
            bytesRead += renameBytes;
            progress?.RenamesRead(renameBytes);
            Phase("renames");
        }
        catch (Exception)
        {
            // Keep what was read: every hash recorded so far goes into the ledgers, so re-running a cancelled (or
            // failed) cold compare only reads what this one didn't get to.
            checkpoint?.Stop();
            try { leftCache.Save(lw.Files, finished: false); rightCache.Save(rw.Files, finished: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }

        checkpoint?.Stop();
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
            UnstableFiles = Unstable(leftCache, rightCache, null),
            UnstableChanged = Unstable(leftCache, rightCache, ReadDoubt.Changed),
            UnstableListing = Unstable(leftCache, rightCache, ReadDoubt.Listing),
            UnstableNoChangeTime = Unstable(leftCache, rightCache, ReadDoubt.NoChangeTime),
            PendingFiles = leftCache.Pending + rightCache.Pending,
            CachedWithoutId = leftCache.WithoutId + rightCache.WithoutId,
            SharePauses = guard.Pauses,
            SharePaused = guard.Paused,
            RetriedReads = guard.Retried + retriedAtEnd,
            ReadFailures = guard.Tally.ByCause(),
            PauseLog = guard.Tally.Pauses(),
            ComparedPairs = sameSize.Count,
            BytesRead = bytesRead,
            Timings = timings,
        };
    }

    /// <summary>The walk's files in strictly ascending ordinal path order (a walk already is; anything else is put
    /// in order, and a path twice still throws as it did when they were mapped).</summary>
    private static IReadOnlyList<FileEntry> Ordered(IReadOnlyList<FileEntry> files)
    {
        for (int i = 1; i < files.Count; i++)
            if (string.CompareOrdinal(files[i - 1].RelativePath, files[i].RelativePath) >= 0)
                return files.ToDictionary(e => e.RelativePath, StringComparer.Ordinal).Values
                            .OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToArray();
        return files;
    }

    /// <summary>The paths read unstably on either side (<paramref name="doubt"/>'s, or any), each once: a file
    /// read on both sides is one file.</summary>
    private static int Unstable(HashCache left, HashCache right, ReadDoubt? doubt)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cache in new[] { left, right })
            foreach (var (path, d) in cache.Unstable)
                if (doubt is null || d == doubt) paths.Add(path);
        return paths.Count;
    }

    /// <summary>
    /// A name the walks skipped because Windows can't open it by path (ending in '.' or ' ', a device name): a row whose
    /// verdict is unknown, in a side or both (a directory as one row: nothing in it was listed), never acted on — left out,
    /// it read as the trees agreeing there.
    /// </summary>
    private static IEnumerable<FileChange> Unopenable(IReadOnlyList<SkippedPath> left, IReadOnlyList<SkippedPath> right)
    {
        var l = left.Where(s => s.Kind == SkipKind.Name).ToDictionary(s => s.Path, StringComparer.Ordinal);
        var r = right.Where(s => s.Kind == SkipKind.Name).ToDictionary(s => s.Path, StringComparer.Ordinal);
        foreach (var path in l.Keys.Union(r.Keys, StringComparer.Ordinal))
        {
            bool inL = l.TryGetValue(path, out var a), inR = r.TryGetValue(path, out var b);
            var what = (inL ? a : b).IsDirectory ? "a directory (nothing in it listed)" : "a file";
            yield return new FileChange(path, inL && inR ? ChangeStatus.Modified : inL ? ChangeStatus.Removed : ChangeStatus.Added, null, 0, 0,
                Unreadable: $"{what} that can't be opened by path on Windows (its name ends in '.' or ' ', or is a device name): " +
                            "not compared, whether it changed is unknown", BehindLink: true);
        }
    }

    /// <summary>The link or unlisted directory (one of <paramref name="links"/>) that <paramref name="path"/> is at or under, or null.</summary>
    private static string? BehindLink(string path, Dictionary<string, SkipKind> links)
    {
        if (links.Count == 0) return null;
        for (var p = path; ; p = p[..p.LastIndexOf('/')])
        {
            if (links.ContainsKey(p)) return p;
            if (p.LastIndexOf('/') < 0) return null;
        }
    }

    /// <summary>A pair the listing says changed size: modified, with its reason — or identical, when the bytes say a
    /// directory entry's size was stale.</summary>
    private static FileChange Modified(ReasonClassifier classifier, FileEntry le, FileEntry re, CancellationToken ct)
        => classifier.ClassifyResized(le.FullPath, le.Length, re.FullPath, re.Length, out long same, ct) is { } reason
            ? new(le.RelativePath, ChangeStatus.Modified, reason, le.Length, re.Length)
            : new(le.RelativePath, ChangeStatus.Identical, null, same, same);

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
        HashCache leftCache, HashCache rightCache, ShareGuard guard, CancellationToken ct)
    {
        IReadOnlyList<FileEntry> leftoverRemoved = removed;
        IReadOnlyList<FileEntry> leftoverAdded = added;
        IReadOnlyDictionary<string, string>? unreadable = null;
        long read = 0;
        string? limit = null;

        if (_options.DetectRenames)
        {
            var result = new RenameDetector(_options) { Guard = guard }.Detect(removed, added, leftCache, rightCache, ct);
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
                    EditedRename: result.Edited?.Contains(r.To) ?? false, RenameHeld: r.Held));
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
