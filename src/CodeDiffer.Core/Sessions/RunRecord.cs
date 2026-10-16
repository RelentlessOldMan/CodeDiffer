using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CodeDiffer.Core.Compare;

namespace CodeDiffer.Core.Sessions;

/// <summary>
/// <c>run.json</c> in a compare's result directory: how the run went, in numbers only (no path, no file name, no
/// error text), so it can be read out and shared as is. Rewritten every <see cref="Interval"/> while the compare
/// runs (a heartbeat: a process killed outright leaves its last one, saying how far it got and what it was using)
/// and once more when it ends, with how. <c>codediffer runs</c> prints it.
/// </summary>
internal sealed class RunRecord : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    public const string FileName = "run.json";

    private readonly Session _session;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private long _peakGcHeap, _peakLive;
    private bool _ended;

    public RunRecord(Session session)
    {
        _session = session;
        Write(null, null);
        _timer = new Timer(_ => Beat(), null, Interval, Interval);
    }

    private void Beat()
    {
        lock (_gate) if (!_ended) Write(null, null);
    }

    /// <summary>The last record: <paramref name="how"/> is done, failed, cancelled, or why it was stopped.</summary>
    public void End(string how, string? errorType)
    {
        lock (_gate)
        {
            if (_ended) return;
            _ended = true;
            Write(how, errorType);
        }
        _timer.Dispose();
    }

    public void Dispose() => _timer.Dispose();

    private void Write(string? ended, string? errorType)
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            long heap = GC.GetTotalMemory(false);
            if (heap > _peakGcHeap) _peakGcHeap = heap;
            // The heap above counts garbage not yet collected; what the last GC found still in use is what the run holds.
            long live = gc.HeapSizeBytes - gc.FragmentedBytes;
            if (live > _peakLive) _peakLive = live;
            var dir = _session.ResultDir!;
            var tmp = Path.Combine(dir, FileName + ".tmp");
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteString("format", "codediffer-run");
                w.WriteNumber("version", 1);
                w.WriteString("id", _session.Id);
                w.WriteString("kind", _session is Compare3Session ? "compare3" : "compare");
                w.WriteString("tool", ResultStore.Version);
                w.WriteString("started", _session.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
                w.WriteString("written", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                w.WriteNumber("elapsedSeconds", Math.Round(_session.Elapsed.TotalSeconds, 1));
                w.WriteNumber("heartbeatSeconds", Interval.TotalSeconds);
                if (ended is not null) w.WriteString("ended", ended);
                if (errorType is not null) w.WriteString("errorType", errorType);

                w.WriteStartObject("options");
                w.WriteNumber("threads", _session.Options.Parallelism);
                w.WriteString("cache", _session.Options.CacheLabel);
                w.WriteBoolean("strictStat", _session.Options.StrictStat);
                w.WriteEndObject();

                w.WriteStartObject("process");
                w.WriteNumber("workingSetMB", me.WorkingSet64 >> 20);
                w.WriteNumber("peakWorkingSetMB", me.PeakWorkingSet64 >> 20);
                w.WriteNumber("privateMB", me.PrivateMemorySize64 >> 20);
                w.WriteNumber("gcHeapMB", heap >> 20);
                w.WriteNumber("peakGcHeapMB", _peakGcHeap >> 20);
                w.WriteNumber("peakLiveAfterGcMB", _peakLive >> 20);
                w.WriteNumber("allocatedMB", GC.GetTotalAllocatedBytes() >> 20);
                w.WriteNumber("cpuSeconds", Math.Round(me.TotalProcessorTime.TotalSeconds, 1));
                w.WriteNumber("threads", me.Threads.Count);
                w.WriteNumber("handles", me.HandleCount);
                w.WriteNumber("gen2Collections", GC.CollectionCount(2));
                w.WriteEndObject();

                w.WriteStartObject("machine");
                w.WriteNumber("cores", Environment.ProcessorCount);
                w.WriteNumber("memoryMB", gc.TotalAvailableMemoryBytes >> 20);
                w.WriteNumber("memoryLoadPercent", gc.TotalAvailableMemoryBytes > 0 ? (int)(100 * gc.MemoryLoadBytes / gc.TotalAvailableMemoryBytes) : 0);
                w.WriteEndObject();

                w.WriteStartArray("compares");
                foreach (var (name, p) in _session.Progresses())
                {
                    w.WriteStartObject();
                    w.WriteString("name", name);
                    w.WriteString("phase", p.Phase.ToString().ToLowerInvariant());
                    w.WriteNumber("listedLeft", p.ListedLeft);
                    w.WriteNumber("listedRight", p.ListedRight);
                    w.WriteNumber("paths", p.Paths);
                    w.WriteNumber("sameSizePairs", p.SameSizePairs);
                    w.WriteNumber("pairsDone", p.PairsDone);
                    w.WriteNumber("sameSizeBytes", p.SameSizeBytes);
                    w.WriteNumber("bytesDone", p.BytesDone);
                    w.WriteNumber("bytesRead", p.BytesRead);
                    w.WriteNumber("cacheSides", p.CacheSides);
                    w.WriteNumber("differencesFound", p.FoundCount);
                    w.WriteNumber("unreadableSoFar", p.UnreadableSoFar);
                    if (p.Pauses > 0)
                    {
                        w.WriteNumber("pauses", p.Pauses);
                        w.WriteNumber("pausedSeconds", Math.Round(p.PausedTime.TotalSeconds, 1));
                        w.WriteBoolean("pausedNow", p.PausedSinceUtc is not null);
                        w.WriteNumber("pauseErrorCode", p.PauseCode);
                    }
                    w.WriteStartObject("phaseSeconds");
                    foreach (var (ph, t) in p.PhaseTimes()) w.WriteNumber(ph.ToString().ToLowerInvariant(), Math.Round(t.TotalSeconds, 1));
                    w.WriteEndObject();
                    w.WriteStartObject("contentWorkerSeconds");
                    w.WriteNumber("opening", Math.Round(p.Io.Opening.TotalSeconds, 1));
                    w.WriteNumber("waitingForBytes", Math.Round(p.Io.ReadWait.TotalSeconds, 1));
                    w.WriteNumber("comparingAndHashing", Math.Round(p.Io.Work.TotalSeconds, 1));
                    w.WriteNumber("filesOpened", p.Io.Opens);
                    w.WriteEndObject();
                    w.WriteNumber("checkpoints", p.Checkpoints);
                    w.WriteNumber("checkpointSeconds", Math.Round(p.CheckpointTime.TotalSeconds, 1));
                    if (p.LastCheckpointUtc is { } lc)
                        w.WriteString("lastCheckpoint", lc.ToString("O", CultureInfo.InvariantCulture));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.Move(tmp, Path.Combine(dir, FileName), overwrite: true);
        }
        // Telemetry never costs the compare: a record that can't be written is skipped (the next beat tries again).
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }
}
