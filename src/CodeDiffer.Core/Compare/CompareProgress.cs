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
        get { lock (_gate) return _found.Count; }
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
        _phase = phase;
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
        lock (_gate) _found.Add(new PartialChange(c, mayBeRename));
    }
}
