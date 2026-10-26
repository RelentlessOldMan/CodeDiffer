using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeDiffer.Core.Compare;

namespace CodeDiffer.Core.Sessions;

/// <summary>
/// A saved compare's run record (<see cref="RunRecord"/>) as a few lines of numbers, safe to share as is: no path, no
/// file name, no error text. For a run that ended without a word (killed outright, the machine went down), its
/// last heartbeat says how far it got and what it was using then.
/// </summary>
public static class RunSummary
{
    public static string Format(SavedCompare c)
    {
        var sb = new StringBuilder();
        var head = $"run {c.Id}  {c.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {c.Kind}";
        var path = Path.Combine(c.Dir, RunRecord.FileName);
        if (!File.Exists(path))
            return $"{head}  {c.State}  (no run record: made by CodeDiffer 1.0.66 or earlier)\n";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            double elapsed = Num(r, "elapsedSeconds");
            string how;
            if (r.TryGetProperty("ended", out var e))
                how = e.GetString() + (r.TryGetProperty("errorType", out var et) ? $" ({et.GetString()})" : "") + $" after {Clock(elapsed)}";
            else if (c.State == "running" && c.Alive)
                how = $"running, {Clock(elapsed)} in";
            else
                how = $"ENDED WITHOUT A CLEAN STOP (killed or crashed) between {Clock(elapsed)} and {Clock(elapsed + Num(r, "heartbeatSeconds"))} in";
            sb.AppendLine($"{head}  {how}  (CodeDiffer {Str(r, "tool")})");

            var o = r.GetProperty("options");
            var m = r.GetProperty("machine");
            var p = r.GetProperty("process");
            sb.AppendLine($"  machine   {Num(m, "cores"):0} cores · {Gb(Num(m, "memoryMB")):0.#} GB, {Num(m, "memoryLoadPercent"):0}% in use · " +
                          $"{Num(o, "threads"):0} threads · cache {Str(o, "cache")}");
            sb.AppendLine($"  memory    peak {Num(p, "peakWorkingSetMB"):N0} MB (now {Num(p, "workingSetMB"):N0} MB, private {Num(p, "privateMB"):N0} MB) · " +
                          $".NET heap peak {Num(p, "peakGcHeapMB"):N0} MB" +
                          (p.TryGetProperty("peakLiveAfterGcMB", out _) ? $" ({Num(p, "peakLiveAfterGcMB"):N0} MB in use after a GC, {Num(p, "allocatedMB"):N0} MB allocated in all)" : "") +
                          $" · {Num(p, "gen2Collections"):0} full GCs");
            sb.AppendLine($"  cpu       {Clock(Num(p, "cpuSeconds"))} CPU over {Clock(elapsed)}" +
                          (Str(p, "cpuFrom") is { Length: > 0 } from ? $" ({from})" : "") +
                          $" · {Num(p, "threads"):0} threads · {Num(p, "handles"):N0} handles");
            if (Counts(p, "exceptions") is { } ex) sb.AppendLine($"  errors    thrown (caught or not): {ex}");
            if (Counts(p, "liveStats") is { } ls) sb.AppendLine($"  stats     file stats and opens by way: {ls}");

            foreach (var x in r.GetProperty("compares").EnumerateArray())
            {
                string name = Str(x, "name");
                double pairs = Num(x, "sameSizePairs"), done = Num(x, "pairsDone"), bytes = Num(x, "sameSizeBytes"), bdone = Num(x, "bytesDone");
                sb.AppendLine($"  {name,-9} phase {Str(x, "phase")} · listed {Num(x, "listedLeft"):N0} / {Num(x, "listedRight"):N0} files · " +
                              $"checked {done:N0} of {pairs:N0} same-size pairs, {Gb(bdone / 1048576):N1} of {Gb(bytes / 1048576):N1} GB" +
                              (bytes > 0 ? $" ({100 * bdone / bytes:0}%)" : "") +
                              $" · read {Gb(Num(x, "bytesRead") / 1048576):N1} GB · {Num(x, "cacheSides"):N0} sides from the cache · {Num(x, "differencesFound"):N0} differences so far");
                if (Num(x, "unreadableSoFar") > 0 || Num(x, "pauses") > 0)
                    sb.AppendLine($"            unreadable so far {Num(x, "unreadableSoFar"):N0}" +
                                  (Num(x, "pauses") > 0 ? $" · trees stopped answering {Num(x, "pauses"):0} time(s), paused {Clock(Num(x, "pausedSeconds"))} in all" +
                                                          (x.TryGetProperty("pausedNow", out var pn) && pn.GetBoolean() ? " (paused now)" : "") +
                                                          $" · last cause Windows error {Num(x, "pauseErrorCode"):0}" : ""));
                if (x.TryGetProperty("reads", out var reads))
                {
                    sb.AppendLine($"            failed reads by cause: {AgentViews.FailureLine(ResultStore.ReadReadFailures(reads))}");
                    var spans = ResultStore.ReadPauseLog(reads);
                    if (spans.Count > 0)
                        sb.AppendLine("            pauses: " + string.Join(" · ", spans.Select(s =>
                            $"at {Clock(s.AtSeconds)} for {(s.Seconds < 0 ? "(still paused)" : Clock(s.Seconds))}, " +
                            $"{new ReadFailures(s.Cause, 0, 0, 0, 0).Label}{(s.Seconds >= 0 && !s.Resumed ? ", stopped" : "")}")));
                }
                var ph = x.GetProperty("phaseSeconds").EnumerateObject().Select(q => $"{q.Name} {Clock(q.Value.GetDouble())}");
                sb.AppendLine($"            time: {string.Join(" · ", ph)}");
                if (x.TryGetProperty("phaseAllocatedMB", out var pa))
                    sb.AppendLine($"            allocated: {string.Join(" · ", pa.EnumerateObject().Select(q => $"{q.Name} {q.Value.GetDouble():N0} MB"))}");
                var io = x.GetProperty("contentWorkerSeconds");
                double open = Num(io, "opening"), wait = Num(io, "waitingForBytes"), work = Num(io, "comparingAndHashing"), all = open + wait + work;
                double contents = x.GetProperty("phaseSeconds").TryGetProperty("contents", out var cs) ? cs.GetDouble() : 0;
                if (all > 0)
                    sb.AppendLine($"            contents, worker time: opening files {Pct(open, all)} · waiting for bytes {Pct(wait, all)} · comparing+hashing {Pct(work, all)}" +
                                  $" ({Num(io, "filesOpened"):N0} opens" + (contents > 0 ? $", {Gb(Num(x, "bytesRead") / 1048576) * 1024 / contents:N1} MB/s" : "") + ")");
                double cps = Num(x, "checkpoints");
                if (cps > 0 || r.TryGetProperty("ended", out _) == false)
                {
                    var last = x.TryGetProperty("lastCheckpoint", out var lc) && DateTime.TryParse(lc.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lt)
                        ? $", the last {Clock((DateTime.Parse(Str(r, "written"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) - lt).TotalSeconds)} before this record"
                        : "";
                    sb.AppendLine($"            hashes saved mid-run {cps:0} time(s), {Num(x, "checkpointSeconds"):0.#} s in all{last}");
                }
            }
            if (r.TryGetProperty("merge", out var mg))
            {
                sb.AppendLine($"  merge     {Num(mg, "entries"):N0} changed paths classified and merged in {Clock(Num(mg, "seconds"))} · allocated {Num(mg, "allocatedMB"):N0} MB");
                if (mg.TryGetProperty("steps", out var st))
                {
                    sb.AppendLine($"            by step (thread time, allocated): {string.Join(" · ", st.EnumerateObject().Select(q => $"{q.Name} {Clock(Num(q.Value, "seconds"))} {Num(q.Value, "allocatedMB"):N0} MB"))}");
                    var ways = mg.GetProperty("diffs").EnumerateObject().Select(q => $"{q.Value.GetDouble():N0} {Way(q.Name)}");
                    sb.AppendLine($"            {Num(mg, "filesMerged"):N0} files merged line by line ({Num(mg, "linesMerged"):N0} lines, {Num(mg, "readMB"):N0} MB read) · line diffs: {string.Join(" · ", ways)}");
                }
            }
            if (r.TryGetProperty("save", out var sv))
                sb.AppendLine($"  save      result saved in {Clock(Num(sv, "seconds"))} · allocated {Num(sv, "allocatedMB"):N0} MB");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or FormatException)
        {
            return $"{head}  {c.State}  (run record unreadable)\n";
        }
        return sb.ToString();
    }

    /// <summary>A record's counts by key as one line ("IOException 87 2 · ..."), or null when it has none.</summary>
    private static string? Counts(JsonElement e, string k)
        => e.TryGetProperty(k, out var o) && o.ValueKind == JsonValueKind.Object && o.EnumerateObject().Any()
            ? string.Join(" · ", o.EnumerateObject().Select(q => $"{q.Name} ×{q.Value.GetDouble():N0}"))
            : null;

    private static double Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    private static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
    private static double Gb(double mb) => mb / 1024;
    private static string Way(string w) => w switch { "Prefix" => "one side a prefix", "Exact" => "exact", "LinearSpace" => "linear-space", "Coarse" => "coarse", _ => w };
    private static string Pct(double part, double all) => $"{100 * part / all:0}%";

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:00}s" : $"{t.TotalSeconds:0.#}s";
    }
}
