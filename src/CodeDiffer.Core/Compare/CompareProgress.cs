using System.Diagnostics;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Compare;

/// <summary>Where a running 2-way compare is.</summary>
public enum ComparePhase { Starting, Walking, Pairing, Contents, Renames, Saving, Done }

/// <summary>A difference found before the compare finished. An add or remove may still pair up into a rename.</summary>
public readonly record struct PartialChange(FileChange Change, bool MayBeRename);

/// <summary>
/// Live progress of one 2-way compare, written by <see cref="DirectoryComparer"/> and read from other threads
/// (the MCP views, the CLI's progress line). Besides counters it keeps the differences found so far, in the
/// order found, so a caller can start on them while the rest is still being read: adds and removes are known
/// right after the walk (a rename pairs them later), size-changed files right after that, and same-size
/// files as their contents are checked.
/// </summary>
public sealed class CompareProgress
{
    private readonly object _gate = new();
    private readonly List<PartialChange> _found = [];
    private readonly Stopwatch _contentClock = new();
    private long _listedLeft, _listedRight, _pairsDone, _bytesDone, _bytesRead, _cacheSides;
    private volatile ComparePhase _phase = ComparePhase.Starting;
    private readonly Stopwatch _phaseClock = Stopwatch.StartNew();
    private readonly TimeSpan[] _phaseTimes = new TimeSpan[Enum.GetValues<ComparePhase>().Length];
    private readonly long[] _phaseAllocated = new long[Enum.GetValues<ComparePhase>().Length];
    private long _phaseAllocStart = GC.GetTotalAllocatedBytes();
    private long _checkpoints, _checkpointTicks, _lastCheckpointUtc;

    /// <summary>Read failures by cause, and the pauses (see <see cref="ShareGuard"/>).</summary>
    public ReadTally Reads { get; } = new();

    /// <summary>Where the content phase's worker time goes (opening, waiting for bytes, comparing/hashing).</summary>
    public IoMeter Io { get; } = new();

    /// <summary>Wall time spent in each phase so far, the current one included (for a run that never finishes too).</summary>
    public IReadOnlyList<(ComparePhase Phase, TimeSpan Elapsed)> PhaseTimes()
    {
        lock (_gate)
        {
            var now = _phaseClock.Elapsed;
            return [.. _phaseTimes.Select((t, i) => ((ComparePhase)i, (ComparePhase)i == _phase ? t + now : t)).Where(x => x.Item2 > TimeSpan.Zero)];
        }
    }

    /// <summary>Bytes the process allocated during each phase so far, the current one included (the whole process's:
    /// a compare3's two compares run one after the other, so each phase is its own).</summary>
    public IReadOnlyList<(ComparePhase Phase, long Bytes)> PhaseAllocated()
    {
        lock (_gate)
        {
            long now = GC.GetTotalAllocatedBytes() - _phaseAllocStart;
            return [.. _phaseAllocated.Select((b, i) => ((ComparePhase)i, (ComparePhase)i == _phase ? b + now : b)).Where(x => x.Item2 > 0)];
        }
    }

    /// <summary>Hash ledgers saved mid-compare (so a killed run keeps what it read), the time they took, and when the last
    /// one was (UTC; null before the first).</summary>
    public long Checkpoints => Interlocked.Read(ref _checkpoints);
    public TimeSpan CheckpointTime => TimeSpan.FromTicks(Interlocked.Read(ref _checkpointTicks));
    public DateTime? LastCheckpointUtc => Interlocked.Read(ref _lastCheckpointUtc) is > 0 and var t ? new DateTime(t, DateTimeKind.Utc) : null;

    internal void Checkpointed(TimeSpan took)
    {
        Interlocked.Increment(ref _checkpoints);
        Interlocked.Add(ref _checkpointTicks, took.Ticks);
        Interlocked.Exchange(ref _lastCheckpointUtc, DateTime.UtcNow.Ticks);
    }

    // Pauses while the trees don't answer (ShareGuard), and the lines a console prints as they happen.
    private readonly List<string> _notices = [];
    private long _pausedSinceUtc;
    private string? _pauseWhy;
    private TimeSpan _pauseLimit;
    private long _unreadable, _pauses, _pausedTicks;
    private int _pauseCode;

    /// <summary>When the current pause began (UTC; null while not paused), why, and how long it may last.</summary>
    public DateTime? PausedSinceUtc => Interlocked.Read(ref _pausedSinceUtc) is > 0 and var t ? new DateTime(t, DateTimeKind.Utc) : null;
    public string? PauseReason { get { lock (_gate) return _pauseWhy; } }
    public TimeSpan PauseLimit { get { lock (_gate) return _pauseLimit; } }
    /// <summary>Pairs found unreadable so far (each may still be read on the try at the end).</summary>
    public long UnreadableSoFar => Interlocked.Read(ref _unreadable);
    /// <summary>Pauses so far, their time (the current one included) and the Windows error code of the last one's
    /// cause (0: none, or not a Windows error) — counts only, for the run record.</summary>
    public long Pauses => Interlocked.Read(ref _pauses);
    public TimeSpan PausedTime => TimeSpan.FromTicks(Interlocked.Read(ref _pausedTicks)) + (PausedSinceUtc is { } s ? DateTime.UtcNow - s : TimeSpan.Zero);
    public int PauseCode => Volatile.Read(ref _pauseCode);

    /// <summary>Lines worth a line of their own (a pause, its end), oldest first; a console prints the new ones.</summary>
    public string[] Notices()
    {
        lock (_gate) return [.. _notices];
    }

    private void Notice(string text)
    {
        lock (_gate) _notices.Add(text);
    }

    internal void SharePaused(string why, int code, TimeSpan limit)
    {
        lock (_gate) (_pauseWhy, _pauseLimit) = (why, limit);
        Interlocked.Increment(ref _pauses);
        Volatile.Write(ref _pauseCode, code);
        Interlocked.Exchange(ref _pausedSinceUtc, DateTime.UtcNow.Ticks);
        Notice($"PAUSED: the trees stopped answering ({why}). Nothing is marked unreadable for it: the compare waits, " +
               $"checking again every few seconds, for up to {Sessions.ProgressView.Clock(limit)}, and carries on when they answer.");
    }

    internal void ShareResumed(TimeSpan took)
    {
        Interlocked.Add(ref _pausedTicks, took.Ticks);
        Interlocked.Exchange(ref _pausedSinceUtc, 0);
        Notice($"resumed: the trees answer again after {Sessions.ProgressView.Clock(took)} paused.");
    }

    internal void ShareLost(TimeSpan took)
    {
        Interlocked.Add(ref _pausedTicks, took.Ticks);
        Interlocked.Exchange(ref _pausedSinceUtc, 0);
        Notice("stopping: the trees did not answer again in time; what was read is kept in the hash cache.");
    }

    public ComparePhase Phase => _phase;
    public long ListedLeft => Interlocked.Read(ref _listedLeft);
    public long ListedRight => Interlocked.Read(ref _listedRight);
    /// <summary>Paths in either tree (known once the walk is done).</summary>
    public int Paths { get; private set; }
    /// <summary>Same-size pairs whose contents must be checked, and their total size (one side).</summary>
    public int SameSizePairs { get; private set; }
    public long SameSizeBytes { get; private set; }
    public long PairsDone => Interlocked.Read(ref _pairsDone);
    public long BytesDone => Interlocked.Read(ref _bytesDone);
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    public long CacheSides => Interlocked.Read(ref _cacheSides);
    /// <summary>Time spent checking contents so far (the rate and ETA are over this).</summary>
    public TimeSpan ContentElapsed => _contentClock.Elapsed;

    /// <summary>The differences found so far, in the order found (a stable order, so pages don't shift).</summary>
    public PartialChange[] Found()
    {
        lock (_gate) return [.. _found];
    }

    public int FoundCount
    {
        get { lock (_gate) return _released >= 0 ? _released : _found.Count; }
    }

    private int _released = -1;

    /// <summary>The compare has ended: its views read the report now, so the differences found while it ran are let
    /// go (an add or remove held its own copy, for as long as the session was kept). The count stays.</summary>
    internal void Release()
    {
        lock (_gate)
        {
            if (_released >= 0) return;
            _released = _found.Count;
            _found.Clear();
            _found.TrimExcess();
        }
    }

    /// <summary>Rough time left for the content phase; null until there is enough to go on.</summary>
    public TimeSpan? Remaining() => Remaining(_contentClock.Elapsed);

    internal TimeSpan? Remaining(TimeSpan contentElapsed)
    {
        if (_phase != ComparePhase.Contents) return null;
        double secs = contentElapsed.TotalSeconds;
        long pairs = PairsDone, bytes = BytesDone;
        if (secs < 5 || SameSizePairs == 0) return null;
        // Files are checked biggest first. When files are being READ, time follows bytes: a per-file rate taken
        // over the first few giants would project hours of them onto 60k small files. When everything comes
        // from the cache, bytes "finish" at once and time follows the per-file cost (a stat each).
        // The exception is the tail: once the bytes are nearly all done, tens of thousands of small files can
        // remain (death: 58k files, 0.1 GB, 3.5 min), so there it is the recent files-per-second rate that counts.
        if (BytesRead > 0)
        {
            if (bytes == 0 || SameSizeBytes == 0) return null;
            double byBytes = (SameSizeBytes - bytes) * secs / bytes;
            bool tail = SameSizeBytes - bytes < SameSizeBytes / 50;
            double? rate = RecentFileRate(secs);
            return TimeSpan.FromSeconds(tail && rate > 0 ? Math.Max(byBytes, (SameSizePairs - pairs) / rate.Value) : byBytes);
        }
        return pairs > 0 ? TimeSpan.FromSeconds((SameSizePairs - pairs) * secs / pairs) : null;
    }

    // Files-done samples at least 10 s apart (content-phase seconds): the recent rate is over the older one.
    private double _sampleOldT = -1, _sampleNewT;
    private long _sampleOldPairs, _sampleNewPairs;

    internal void Sample(double secs)
    {
        if (secs - Volatile.Read(ref _sampleNewT) < 10) return;
        lock (_gate)
        {
            if (secs - _sampleNewT < 10) return;
            (_sampleOldT, _sampleOldPairs) = (_sampleNewT, _sampleNewPairs);
            (_sampleNewT, _sampleNewPairs) = (secs, PairsDone);
        }
    }

    private double? RecentFileRate(double now)
    {
        lock (_gate)
            return _sampleOldT < 0 || now <= _sampleOldT ? null : (PairsDone - _sampleOldPairs) / (now - _sampleOldT);
    }

    internal void SetPhase(ComparePhase phase)
    {
        if (phase == ComparePhase.Contents) _contentClock.Start();
        else _contentClock.Stop();
        lock (_gate)
        {
            _phaseTimes[(int)_phase] += _phaseClock.Elapsed;
            _phaseClock.Restart();
            long alloc = GC.GetTotalAllocatedBytes();
            _phaseAllocated[(int)_phase] += alloc - _phaseAllocStart;
            _phaseAllocStart = alloc;
            _phase = phase;
        }
    }

    internal void Listed(bool left, int files)
    {
        if (left) Interlocked.Add(ref _listedLeft, files);
        else Interlocked.Add(ref _listedRight, files);
    }

    internal void Paired(int paths, int sameSizePairs, long sameSizeBytes)
    {
        Paths = paths;
        SameSizePairs = sameSizePairs;
        SameSizeBytes = sameSizeBytes;
    }

    /// <summary>A chunk read mid-pair: bytes count as they stream, so a multi-GB file shows progress before it ends.</summary>
    internal void Streamed(int advance, int read)
    {
        Interlocked.Add(ref _bytesDone, advance);
        Interlocked.Add(ref _bytesRead, read);
    }

    /// <summary>What rename detection read (hashing same-size adds/removes, reading text candidates).</summary>
    internal void RenamesRead(long bytes) => Interlocked.Add(ref _bytesRead, bytes);

    /// <param name="size">The pair's size not already counted by <see cref="Streamed"/>.</param>
    internal void PairChecked(long size, long read, int cacheSides)
    {
        Interlocked.Increment(ref _pairsDone);
        Sample(_contentClock.Elapsed.TotalSeconds);
        Interlocked.Add(ref _bytesDone, size);
        if (read > 0) Interlocked.Add(ref _bytesRead, read);
        if (cacheSides > 0) Interlocked.Add(ref _cacheSides, cacheSides);
        AfterPairChecked?.Invoke(PairsDone);
    }

    /// <summary>Tests: called after each pair is checked, with the number done (e.g. to cancel at a known point).</summary>
    internal Action<long>? AfterPairChecked { get; set; }

    internal void Add(FileChange c, bool mayBeRename = false)
    {
        if (c.Unreadable is not null && !c.BehindLink) Interlocked.Increment(ref _unreadable);
        lock (_gate)
            if (_released < 0) _found.Add(new PartialChange(c, mayBeRename));
    }
}
