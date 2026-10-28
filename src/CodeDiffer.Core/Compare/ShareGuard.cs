using System.Diagnostics;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// The share stopped answering and didn't come back within <see cref="CompareOptions.ShareWait"/>. The compare stops
/// (what was read is kept in the hash cache, so the same compare run again reads only the rest), rather than listing
/// every file left as unreadable.
/// </summary>
public sealed class ShareUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>Failed reads of one cause (a Windows error code, or the exception's type when there is none): every failed
/// try, the reads that then worked, the reads given up on (listed unreadable), and the pauses it began.</summary>
public sealed record ReadFailures(string Cause, int Failed, int Recovered, int GaveUp, int Pauses)
{
    /// <summary>"Windows error 64", or the type name.</summary>
    public string Label => int.TryParse(Cause, out _) ? $"Windows error {Cause}" : Cause;
}

/// <summary>One pause: when it began (seconds into the compare), how long it lasted, its cause, and whether the trees
/// answered again (false: the compare stopped).</summary>
public sealed record PauseSpan(double AtSeconds, double Seconds, string Cause, bool Resumed);

/// <summary>
/// The read failures of one compare, by cause, and its pauses: counts and codes only (no paths or messages), so they
/// can be shown and shared as they are. Filled by <see cref="ShareGuard"/>; read live (the run record) and at the end
/// (the report).
/// </summary>
public sealed class ReadTally
{
    private const int MaxPauses = 100;
    private readonly object _gate = new();
    private readonly Dictionary<string, int[]> _by = new(StringComparer.Ordinal); // failed, recovered, gave up, pauses
    private readonly List<PauseSpan> _pauses = [];

    private void Add(string cause, int what)
    {
        lock (_gate)
        {
            if (!_by.TryGetValue(cause, out var n)) _by[cause] = n = new int[4];
            n[what]++;
        }
    }

    internal void Failed(string cause) => Add(cause, 0);
    internal void Recovered(string cause) => Add(cause, 1);
    internal void GaveUp(string cause) => Add(cause, 2);

    internal void PauseBegan(string cause, double at)
    {
        Add(cause, 3);
        lock (_gate)
            if (_pauses.Count < MaxPauses) _pauses.Add(new PauseSpan(at, -1, cause, false));
    }

    internal void PauseEnded(double seconds, bool resumed)
    {
        lock (_gate)
            if (_pauses.Count > 0 && _pauses[^1].Seconds < 0) _pauses[^1] = _pauses[^1] with { Seconds = seconds, Resumed = resumed };
    }

    /// <summary>Causes, most failed tries first.</summary>
    public IReadOnlyList<ReadFailures> ByCause()
    {
        lock (_gate)
            return [.. _by.Select(kv => new ReadFailures(kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]))
                          .OrderByDescending(f => f.Failed).ThenBy(f => f.Cause, StringComparer.Ordinal)];
    }

    /// <summary>The pauses in order (the first 100); one still going has Seconds -1.</summary>
    public IReadOnlyList<PauseSpan> Pauses()
    {
        lock (_gate) return [.. _pauses];
    }
}

/// <summary>
/// Every read of a compare goes through this, so a failure is told apart: one file that can't be read (locked,
/// vanished, denied) or the trees as a whole that can't (a share gone quiet, a dropped session, this process unable
/// to open anything). After a failed read it checks that each tree's root is still there and that a file read earlier
/// in the run still opens. If so, the file alone is at fault: it's tried again a couple of times, then listed as
/// unreadable as before. If not, the whole compare pauses: no read starts, the check is repeated with a growing delay,
/// and once it passes every paused read carries on where it was. Nothing read before the pause is lost, and nothing is
/// marked unreadable because of it. Only a pause longer than <see cref="CompareOptions.ShareWait"/> ends the compare,
/// with <see cref="ShareUnavailableException"/>.
/// </summary>
internal sealed class ShareGuard
{
    private static readonly TimeSpan[] DefaultProbeDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    private readonly string[] _roots;
    private readonly TimeSpan _maxWait;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly IReadOnlyList<TimeSpan> _probeDelays;
    private readonly CompareProgress? _progress;
    private readonly Func<bool> _answers;
    private readonly ReadTally _tally;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly string?[] _good = new string?[4]; // files read this run, the newest few: one must still open
    private int _goodNext;
    private Task? _pause;               // set while paused; completes when the trees answer again
    private ShareUnavailableException? _lost;
    private int _pauses, _retried;
    private long _pausedTicks;
    private (long At, bool Ok)? _lastProbe; // the last check of the trees: its time (clock ticks) and answer

    public ShareGuard(IEnumerable<string> roots, CompareOptions options, CompareProgress? progress, Func<bool>? answers = null)
    {
        _roots = [.. roots.Select(Path.GetFullPath).Distinct()];
        _maxWait = options.ShareWait;
        _retryDelays = options.RetryDelays ?? [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)];
        _probeDelays = options.ProbeDelays ?? DefaultProbeDelays;
        _progress = progress;
        _answers = answers ?? Answers;
        _tally = progress?.Reads ?? new ReadTally();
    }

    /// <summary>The read failures by cause and the pauses so far.</summary>
    public ReadTally Tally => _tally;

    /// <summary>Times the trees stopped answering and the compare paused, and for how long in all.</summary>
    public int Pauses => Volatile.Read(ref _pauses);
    public TimeSpan Paused => TimeSpan.FromTicks(Interlocked.Read(ref _pausedTicks));
    /// <summary>Reads that failed and then worked when tried again (after a pause or a retry).</summary>
    public int Retried => Volatile.Read(ref _retried);

    public T Run<T>(string path, Func<T> read, CancellationToken ct)
        => RunAsync(path, () => Task.FromResult(read()), ct).GetAwaiter().GetResult();

    /// <summary>
    /// Do <paramref name="read"/> (of <paramref name="path"/>, and anything else it reads), waiting out a pause before
    /// it and after a failure. Throws the read's own exception when the file alone can't be read, and
    /// <see cref="ShareUnavailableException"/> when the trees didn't come back in time.
    /// </summary>
    public async Task<T> RunAsync<T>(string path, Func<Task<T>> read, CancellationToken ct)
    {
        string? failed = null; // the cause of the last failed try
        for (int retry = 0; ; )
        {
            await WaitOutPause(ct).ConfigureAwait(false);
            try
            {
                var result = await read().ConfigureAwait(false);
                Good(path);
                if (failed is not null)
                {
                    Interlocked.Increment(ref _retried);
                    _tally.Recovered(failed);
                }
                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ct.ThrowIfCancellationRequested();
                failed = Cause(ex);
                _tally.Failed(failed);
                if (!Probe())
                {
                    Pause(ex);
                    continue; // a pause doesn't count as a retry: the read starts over once the trees answer
                }
                // The trees answer, so it's this file: a vanished one won't come back, nor one the server refuses the
                // same way every time; a locked one may, shortly.
                if (ex is FileNotFoundException or DirectoryNotFoundException || Permanent(ex) || retry >= _retryDelays.Count)
                {
                    _tally.GaveUp(failed);
                    throw;
                }
                await Task.Delay(_retryDelays[retry++], ct).ConfigureAwait(false);
            }
        }
    }

    private void Good(string path)
    {
        int i = (Interlocked.Increment(ref _goodNext) & int.MaxValue) % _good.Length;
        Volatile.Write(ref _good[i], path);
    }

    /// <summary>Do the trees answer? One check a second at most, however many reads fail at once.</summary>
    private bool Probe()
    {
        lock (_gate)
        {
            if (_pause is not null) return false;
            if (_lastProbe is { } last && _clock.ElapsedTicks - last.At < Stopwatch.Frequency) return last.Ok;
            bool ok = _answers();
            _lastProbe = (_clock.ElapsedTicks, ok);
            return ok;
        }
    }

    /// <summary>Each root still lists as a directory, and a file read earlier in the run still opens and reads.</summary>
    private bool Answers()
    {
        foreach (var root in _roots)
            if (!Directory.Exists(root)) return false;
        bool any = false;
        for (int i = 0; i < _good.Length; i++)
        {
            if (Volatile.Read(ref _good[i]) is not { } path) continue;
            any = true;
            try
            {
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> one = stackalloc byte[1];
                RandomAccess.Read(h, one, 0);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return !any;
    }

    private void Pause(Exception ex)
    {
        lock (_gate)
        {
            if (_pause is not null) return;
            Interlocked.Increment(ref _pauses);
            var since = _clock.Elapsed;
            var why = Describe(ex);
            _progress?.SharePaused(why, WindowsCode(ex), _maxWait);
            _tally.PauseBegan(Cause(ex), Math.Round(since.TotalSeconds, 1));
            _pause = Task.Run(() => WaitForTrees(since, why, ex));
        }
    }

    private async Task WaitForTrees(TimeSpan since, string why, Exception first)
    {
        for (int i = 0; ; i++)
        {
            var waited = _clock.Elapsed - since;
            if (waited >= _maxWait)
            {
                Interlocked.Add(ref _pausedTicks, waited.Ticks);
                var lost = new ShareUnavailableException(
                    $"the trees stopped answering {Sessions.ProgressView.Clock(since)} into the compare ({why}) and did not come back " +
                    $"within {Sessions.ProgressView.Clock(_maxWait)}. What was read before is kept in the hash cache: run the same compare " +
                    "again once the share answers, and it reads only the rest (a new run also opens the share afresh, " +
                    "in case only this process lost it).", first);
                _progress?.ShareLost(waited);
                _tally.PauseEnded(Math.Round(waited.TotalSeconds, 1), resumed: false);
                lock (_gate) _lost = lost;
                return;
            }
            var delay = _probeDelays[Math.Min(i, _probeDelays.Count - 1)];
            if (delay > _maxWait - waited) delay = _maxWait - waited;
            await Task.Delay(delay).ConfigureAwait(false);
            if (_answers())
            {
                var took = _clock.Elapsed - since;
                Interlocked.Add(ref _pausedTicks, took.Ticks);
                lock (_gate)
                {
                    _lastProbe = (_clock.ElapsedTicks, true);
                    _pause = null;
                }
                _progress?.ShareResumed(took);
                _tally.PauseEnded(Math.Round(took.TotalSeconds, 1), resumed: true);
                return;
            }
        }
    }

    private async Task WaitOutPause(CancellationToken ct)
    {
        while (true)
        {
            Task? pause;
            lock (_gate)
            {
                if (_lost is not null) throw _lost;
                pause = _pause;
            }
            if (pause is null) return;
            await pause.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>The failure as a line: its message and, for a Windows error, its code (what tells a dropped session
    /// from a timeout or a denial).</summary>
    internal static string Describe(Exception ex)
        => WindowsCode(ex) is > 0 and var code && !ex.Message.Contains($"(win32 error {code})", StringComparison.Ordinal) // CodeDiffer's own say it
            ? $"{ex.Message.TrimEnd()} [Windows error {code}]" : ex.Message.TrimEnd();

    /// <summary>A failure's cause as tallied: its Windows error code, else its type's name.</summary>
    internal static string Cause(Exception ex) => WindowsCode(ex) is > 0 and var code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : ex.GetType().Name;

    internal static int WindowsCode(Exception ex) => (ex.HResult & 0xFFFF0000) == 0x80070000 ? ex.HResult & 0xFFFF : 0;

    /// <summary>A failure that is the same however often it's tried: an invalid parameter (87), name (123) or path
    /// (161), or a name too long (206). Seen on a work share: two files failing with 87 on every try cost a compare
    /// ~15 s of waits before retries that never worked.</summary>
    internal static bool Permanent(Exception ex) => WindowsCode(ex) is 87 or 123 or 161 or 206;
}
