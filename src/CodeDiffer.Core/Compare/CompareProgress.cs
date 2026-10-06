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
    public TimeSpan? Remaining()
    {
        if (_phase != ComparePhase.Contents) return null;
        double secs = _contentClock.Elapsed.TotalSeconds;
        long pairs = PairsDone, bytes = BytesDone;
        if (secs < 5 || pairs == 0 || SameSizePairs == 0) return null;
        // Both a per-file and a per-byte estimate, and the larger wins: biggest files go first, so the byte
        // rate is front-loaded, and a warm cache makes the per-file cost (a stat each) what dominates.
        double byPairs = (SameSizePairs - pairs) * secs / pairs;
        double byBytes = bytes > 0 && SameSizeBytes > 0 ? (SameSizeBytes - bytes) * secs / bytes : 0;
        return TimeSpan.FromSeconds(Math.Max(byPairs, byBytes));
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

    internal void PairChecked(long size, long read, int cacheSides)
    {
        Interlocked.Increment(ref _pairsDone);
        Interlocked.Add(ref _bytesDone, size);
        if (read > 0) Interlocked.Add(ref _bytesRead, read);
        if (cacheSides > 0) Interlocked.Add(ref _cacheSides, cacheSides);
    }

    internal void Add(FileChange c, bool mayBeRename = false)
    {
        lock (_gate) _found.Add(new PartialChange(c, mayBeRename));
    }
}
