using System.Globalization;
using System.Text;
using System.Text.Json;

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
                          $".NET heap peak {Num(p, "peakGcHeapMB"):N0} MB · {Num(p, "gen2Collections"):0} full GCs");
            sb.AppendLine($"  cpu       {Clock(Num(p, "cpuSeconds"))} CPU over {Clock(elapsed)} · {Num(p, "threads"):0} threads · {Num(p, "handles"):N0} handles");

            foreach (var x in r.GetProperty("compares").EnumerateArray())
            {
                string name = Str(x, "name");
                double pairs = Num(x, "sameSizePairs"), done = Num(x, "pairsDone"), bytes = Num(x, "sameSizeBytes"), bdone = Num(x, "bytesDone");
                sb.AppendLine($"  {name,-9} phase {Str(x, "phase")} · listed {Num(x, "listedLeft"):N0} / {Num(x, "listedRight"):N0} files · " +
                              $"checked {done:N0} of {pairs:N0} same-size pairs, {Gb(bdone / 1048576):N1} of {Gb(bytes / 1048576):N1} GB" +
                              (bytes > 0 ? $" ({100 * bdone / bytes:0}%)" : "") +
                              $" · read {Gb(Num(x, "bytesRead") / 1048576):N1} GB · {Num(x, "cacheSides"):N0} sides from the cache · {Num(x, "differencesFound"):N0} differences so far");
                var ph = x.GetProperty("phaseSeconds").EnumerateObject().Select(q => $"{q.Name} {Clock(q.Value.GetDouble())}");
                sb.AppendLine($"            time: {string.Join(" · ", ph)}");
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
                                       or InvalidOperationException or FormatException)
        {
            return $"{head}  {c.State}  (run record unreadable)\n";
        }
        return sb.ToString();
    }

    private static double Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    private static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
    private static double Gb(double mb) => mb / 1024;
    private static string Pct(double part, double all) => $"{100 * part / all:0}%";

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:00}s" : $"{t.TotalSeconds:0.#}s";
    }
}
