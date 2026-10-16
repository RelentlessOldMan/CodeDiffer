using System.Diagnostics;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// Saves the hash ledgers now and then while a compare reads, so a process killed outright (no cancel, no catch:
/// taskkill, a crash, a host closing the server) keeps most of what it read: a re-run reads only the rest. A save
/// happens only when something new was hashed since the last one, and never more than about 5% of the time: the
/// next one waits at least <see cref="CompareOptions.CheckpointInterval"/>, and 20 times as long as the last took.
/// <see cref="Stop"/> waits for a save in progress, so the compare's own final save never runs alongside one.
/// </summary>
internal sealed class LedgerCheckpoint : IDisposable
{
    private readonly Action _save;
    private readonly Func<long> _recorded;
    private readonly CompareProgress? _progress;
    private readonly TimeSpan _interval;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _stopped;
    private long _saved;

    public LedgerCheckpoint(Action save, Func<long> recorded, TimeSpan interval, CompareProgress? progress)
    {
        _save = save;
        _recorded = recorded;
        _interval = interval;
        _progress = progress;
        _saved = recorded();
        _timer = new Timer(_ => Tick(), null, interval, Timeout.InfiniteTimeSpan);
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_stopped) return;
            var next = _interval;
            long now = _recorded();
            if (now != _saved)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    _save();
                    _saved = now;
                    _progress?.Checkpointed(sw.Elapsed);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // the final save still runs
                if (sw.Elapsed * 20 > next) next = sw.Elapsed * 20;
            }
            _timer.Change(next, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>No more saves; returns once a save in progress has finished.</summary>
    public void Stop()
    {
        lock (_gate) _stopped = true;
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }
}
