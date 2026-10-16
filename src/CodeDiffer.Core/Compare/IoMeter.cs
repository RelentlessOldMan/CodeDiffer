using System.Diagnostics;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// Where the content phase's time goes, summed over its parallel workers (worker-seconds, so with 8 workers a
/// minute of wall time is up to 8 minutes here): opening and stat-ing files (a round trip each over SMB), waiting
/// for bytes to arrive, and the work done on them (comparing and hashing). Read on another thread for telemetry.
/// </summary>
public sealed class IoMeter
{
    private long _openTicks, _readTicks, _workTicks, _opens, _bytes;

    public long Opens => Interlocked.Read(ref _opens);
    public long Bytes => Interlocked.Read(ref _bytes);
    public TimeSpan Opening => Ticks(ref _openTicks);
    public TimeSpan ReadWait => Ticks(ref _readTicks);
    public TimeSpan Work => Ticks(ref _workTicks);

    internal static long Now() => Stopwatch.GetTimestamp();

    internal void Opened(long since)
    {
        Interlocked.Increment(ref _opens);
        Interlocked.Add(ref _openTicks, Stopwatch.GetTimestamp() - since);
    }

    internal void Read(long since, long bytes)
    {
        Interlocked.Add(ref _readTicks, Stopwatch.GetTimestamp() - since);
        Interlocked.Add(ref _bytes, bytes);
    }

    internal void Worked(long since) => Interlocked.Add(ref _workTicks, Stopwatch.GetTimestamp() - since);

    private static TimeSpan Ticks(ref long t) => TimeSpan.FromSeconds((double)Interlocked.Read(ref t) / Stopwatch.Frequency);
}
