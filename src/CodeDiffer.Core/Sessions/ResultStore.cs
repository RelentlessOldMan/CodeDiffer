using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.ThreeWay;
using CompareOptions = CodeDiffer.Core.Compare.CompareOptions;

namespace CodeDiffer.Core.Sessions;

/// <summary>A saved compare as listed (from its compare.json only — the change lists are not read).</summary>
public sealed record SavedCompare(
    string Dir, string Id, string Kind, string State, DateTime StartedUtc, TimeSpan Elapsed,
    IReadOnlyList<(string Name, string Path)> Roots, IReadOnlyList<(string Name, long Count)> Counts, string? Error,
    bool Alive = false);

/// <summary>
/// The on-disk result store (docs/OUTPUT.md §1). Each compare gets a fresh directory
/// <c>&lt;root&gt;\yyyyMMdd-HHmmss-&lt;id&gt;\</c>, never inside a compared tree (we never diff our own output),
/// never reused. It holds:
/// <list type="bullet">
/// <item><c>compare.json</c> — format/version, kind, id, state (running | done | failed | cancelled), roots, options, read
///   cost and phase timings, summary counts. Written first as "running", replaced atomically when finished.</item>
/// <item>2-way: <c>changes.jsonl</c> — one line per path (identical included), sorted by path.</item>
/// <item>3-way: <c>v1.jsonl</c> / <c>v2.jsonl</c> (each side's 2-way changes) and <c>entries.jsonl</c> (one line per
///   touched path with its merge outcome).</item>
/// <item>Whatever is derived from it later: capped <c>.patch</c> files, apply reports, the HTML <c>report\</c>.</item>
/// </list>
/// Only the verdicts are stored, not file contents: a reopened compare renders diffs from the live trees and
/// says so when a file's size no longer matches.
/// </summary>
public static class ResultStore
{
    public const string Format = "codediffer-result";
    public const int FormatVersion = 1;
    public const string MetaName = "compare.json";

    private static readonly JsonWriterOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonWriterOptions MetaOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = true };

    /// <summary>CODEDIFFER_RESULTS_DIR, else %LOCALAPPDATA%\CodeDiffer\results.</summary>
    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("CODEDIFFER_RESULTS_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeDiffer", "results");

    public static string Version =>
        typeof(ResultStore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    // ---- create / save ----

    /// <summary>Create the compare's fresh directory with a "running" compare.json. Refuses a results root
    /// inside (or equal to) any compared tree.</summary>
    public static string CreateRunDir(string root, string id, string kind, IReadOnlyList<string> roots)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (var r in roots)
            if (Overlaps(root, r, oneWay: true))
                throw new ArgumentException($"the results directory {root} is inside the compared tree {r}; a compare never writes into " +
                                            "its own input — set CODEDIFFER_RESULTS_DIR to somewhere else");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(root, $"{stamp}-{id}");
        for (int n = 2; Directory.Exists(dir); n++) dir = Path.Combine(root, $"{stamp}-{id}-{n}"); // never reuse
        Directory.CreateDirectory(dir);
        WriteMeta(dir, w =>
        {
            Head(w, kind, id, "running", DateTime.UtcNow, null);
            Roots(w, kind, roots);
        });
        return dir;
    }

    /// <summary>Save a finished compare: the change list(s) first, then compare.json flips to "done".</summary>
    internal static void Save(Session s, object result)
    {
        var dir = s.ResultDir!;
        switch (s, result)
        {
            case (CompareSession c, CompareReport r):
                WriteLines(Path.Combine(dir, "changes.jsonl"), r.Changes, WriteChange);
                WriteMeta(dir, w =>
                {
                    Head(w, "compare", s.Id, "done", s.StartedUtc, s.Elapsed);
                    Roots(w, "compare", [c.Left, c.Right]);
                    Options(w, s.Options);
                    w.WritePropertyName("report");
                    Stats(w, r);
                    w.WriteStartObject("counts");
                    foreach (var st in Enum.GetValues<ChangeStatus>()) w.WriteNumber(Token(st), r.Count(st));
                    w.WriteEndObject();
                });
                break;
            case (Compare3Session c3, ThreeWayReport t):
                WriteLines(Path.Combine(dir, "v1.jsonl"), t.V1Report.Changes, WriteChange);
                WriteLines(Path.Combine(dir, "v2.jsonl"), t.V2Report.Changes, WriteChange);
                WriteLines(Path.Combine(dir, "entries.jsonl"), t.Entries, WriteEntry);
                WriteMeta(dir, w =>
                {
                    Head(w, "compare3", s.Id, "done", s.StartedUtc, s.Elapsed);
                    Roots(w, "compare3", [c3.Base, c3.V1, c3.V2]);
                    Options(w, s.Options);
                    w.WritePropertyName("v1Report");
                    Stats(w, t.V1Report);
                    w.WritePropertyName("v2Report");
                    Stats(w, t.V2Report);
                    Timings(w, t.Timings);
                    w.WriteStartObject("counts");
                    foreach (var o in Enum.GetValues<Merge3Outcome>()) w.WriteNumber(Token(o), t.Count(o));
                    w.WriteNumber("conflictRegions", t.Entries.Sum(e => e.ConflictRegions));
                    w.WriteEndObject();
                });
                break;
            default:
                throw new ArgumentException($"can't save a {result.GetType().Name} for a {s.GetType().Name}");
        }
    }

    /// <summary>Best effort: record that the compare failed or was cancelled (so a listing doesn't show it as still running).</summary>
    /// <param name="state">"failed" or "cancelled".</param>
    internal static void TryMarkFailed(Session s, string state, string? error)
    {
        try
        {
            var (kind, roots) = s switch
            {
                CompareSession c => ("compare", new[] { c.Left, c.Right }),
                Compare3Session t => ("compare3", new[] { t.Base, t.V1, t.V2 }),
                _ => ("unknown", Array.Empty<string>()),
            };
            WriteMeta(s.ResultDir!, w =>
            {
                Head(w, kind, s.Id, state, s.StartedUtc, s.Elapsed);
                Roots(w, kind, roots);
                if (error is not null) w.WriteString("error", error);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---- find / list / load ----

    /// <summary>The newest result directory for this id under <paramref name="root"/>, or null.</summary>
    public static string? Find(string root, string id)
    {
        if (!Regex.IsMatch(id, "^[0-9A-Fa-f]{4}$") || !Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root, $"*-{id}*")
            .Where(d => Regex.IsMatch(Path.GetFileName(d), $"^\\d{{8}}-\\d{{6}}-{id}(-\\d+)?$", RegexOptions.IgnoreCase))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Saved compares, newest first (unreadable directories are skipped).</summary>
    public static IReadOnlyList<SavedCompare> List(string root, int max = 20)
    {
        if (!Directory.Exists(root)) return [];
        var list = new List<SavedCompare>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
        {
            if (list.Count >= max) break;
            try
            {
                // A junction or symlink here is not a result: listing it would let prune delete what it points at.
                if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                using var doc = ReadMeta(dir);
                var m = doc.RootElement;
                var rootsEl = m.GetProperty("roots");
                var roots = rootsEl.EnumerateObject().Where(p => !p.Name.EndsWith(Utf16Suffix, StringComparison.Ordinal))
                    .Select(p => (p.Name, PathOf(rootsEl, p.Name))).ToList();
                var counts = m.TryGetProperty("counts", out var cs) ? cs.EnumerateObject().Select(p => (p.Name, p.Value.GetInt64())).ToList() : [];
                var (state, alive) = Live(m);
                list.Add(new SavedCompare(dir, Str(m, "id"), Str(m, "kind"), state, Started(m), Elapsed(m), roots, counts,
                    m.TryGetProperty("error", out var e) ? e.GetString() : null, alive));
            }
            // A corrupt or foreign compare.json is left out of the list, whatever is wrong with it (a number out of range too).
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
                                           or KeyNotFoundException or InvalidOperationException or FormatException
                                           or ArgumentException or OverflowException) { }
        }
        return list;
    }

    // ---- prune ----

    /// <summary>A "running" result younger than this may belong to a live process (an MCP server); prune leaves it. One whose
    /// process is known to be gone is listed as "stopped" instead and needs no grace; one whose process is seen running
    /// here is never pruned, however long it has run.</summary>
    public static readonly TimeSpan RunningGrace = TimeSpan.FromDays(1);

    /// <summary>
    /// The saved compares <c>results --prune</c> would delete: all but the newest <paramref name="keep"/>, and of those
    /// only the ones older than <paramref name="olderThan"/> when it is given. A "running" result is never picked while its
    /// process is seen alive, nor within <see cref="RunningGrace"/> when that can't be checked (another host). Only directories that hold a
    /// CodeDiffer compare.json are considered, so nothing else under the root is ever touched.
    /// </summary>
    /// <remarks>Newest by start time (not by directory name, which is local time); only directories named the way a
    /// compare names them count, so renaming one (e.g. <c>baseline</c>) keeps it out of pruning.</remarks>
    public static IReadOnlyList<SavedCompare> PruneCandidates(string root, int keep, TimeSpan? olderThan, DateTime nowUtc)
        => List(root, int.MaxValue)
            .Where(c => Regex.IsMatch(Path.GetFileName(c.Dir), @"^\d{8}-\d{6}-[0-9a-f]{4}(-\d+)?$", RegexOptions.IgnoreCase))
            .OrderByDescending(c => c.StartedUtc).ThenByDescending(c => Path.GetFileName(c.Dir), StringComparer.Ordinal)
            .Skip(Math.Max(0, keep))
            .Where(c => olderThan is not { } age || c.StartedUtc < nowUtc - age)
            .Where(c => c.State != "running" || (!c.Alive && c.StartedUtc < nowUtc - RunningGrace))
            .ToList();

    /// <summary>Bytes on disk under a result directory (its patches and report included; links not followed), or -1
    /// when it can't be read.</summary>
    public static long SizeOf(string dir)
    {
        try
        {
            var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return new DirectoryInfo(dir).EnumerateFiles("*", opt).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return -1; }
    }

    /// <summary>Delete one saved compare. Refuses anything that is not a CodeDiffer result directly under <paramref name="root"/>.
    /// Read-only files go too; a link inside is removed, never followed. <see cref="MetaName"/> goes last, so a delete
    /// that fails part way (a file held open) leaves a result that is still listed and pruned next time.</summary>
    public static void Delete(string root, SavedCompare c)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(c.Dir));
        var parent = Path.GetDirectoryName(dir);
        if (!string.Equals(parent, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{dir} is not directly under the results directory {root}");
        if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new ArgumentException($"{dir} is a link, not a saved compare; refusing to delete through it");
        using (ReadMeta(dir)) { } // throws unless it is a CodeDiffer result
        DeleteContents(new DirectoryInfo(dir), keep: MetaName);
        var meta = new FileInfo(Path.Combine(dir, MetaName));
        if (meta.Exists)
        {
            meta.Attributes &= ~FileAttributes.ReadOnly;
            meta.Delete();
        }
        Directory.Delete(dir);
    }

    private static void DeleteContents(DirectoryInfo d, string? keep)
    {
        foreach (var e in d.EnumerateFileSystemInfos())
        {
            if (keep is not null && string.Equals(e.Name, keep, StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Attributes.HasFlag(FileAttributes.ReadOnly)) e.Attributes &= ~FileAttributes.ReadOnly;
            if (e is DirectoryInfo sub && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeleteContents(sub, null);
                sub.Delete();
            }
            else e.Delete(); // a file, or a junction/symlink (the link itself)
        }
    }

    /// <summary>Reopen a finished compare. Throws <see cref="InvalidDataException"/> if it is not a finished
    /// CodeDiffer result (unknown format/version, still running, failed, or a data file is missing).</summary>
    public static Session Load(string dir)
    {
        dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        using var doc = ReadMeta(dir);
        var m = doc.RootElement;
        try
        {
            var id = Str(m, "id");
            var state = State(m);
            if (state != "done")
                throw new InvalidDataException(state switch
                {
                    "failed" => $"compare {id} failed: {(m.TryGetProperty("error", out var e) ? e.GetString() : "?")}",
                    "cancelled" => $"compare {id} was cancelled before it finished",
                    "running" => $"compare {id} is still running (process {Pid(m)}); open it once it has finished",
                    _ => $"compare {id} in {dir} never finished — the process running it stopped",
                });
            var opt = m.GetProperty("options");
            var options = new CompareOptions
            {
                Cache = Enum.TryParse<CacheMode>(Str(opt, "cache"), true, out var cm) ? cm : CacheMode.On,
                Parallelism = Math.Clamp(opt.GetProperty("threads").GetInt32(), 1, 64),
                StrictStat = opt.GetProperty("strictStat").GetBoolean(),
            };
            var roots = m.GetProperty("roots");
            return Str(m, "kind") switch
            {
                "compare" => new CompareSession(id, PathOf(roots, "left"), PathOf(roots, "right"), options,
                    ReadReport(Path.Combine(dir, "changes.jsonl"), m.GetProperty("report")), dir, Started(m), Elapsed(m), reopened: true),
                "compare3" => new Compare3Session(id, PathOf(roots, "base"), PathOf(roots, "v1"), PathOf(roots, "v2"), options, new ThreeWayReport
                {
                    V1Report = ReadReport(Path.Combine(dir, "v1.jsonl"), m.GetProperty("v1Report")),
                    V2Report = ReadReport(Path.Combine(dir, "v2.jsonl"), m.GetProperty("v2Report")),
                    Entries = ReadLines(Path.Combine(dir, "entries.jsonl"), ReadEntry),
                    Timings = ReadTimings(m),
                }, dir, Started(m), Elapsed(m)),
                var k => throw new InvalidDataException($"unknown compare kind '{k}' in {dir}"),
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or KeyNotFoundException or JsonException or InvalidOperationException or FormatException
                                       or ArgumentException or OverflowException)
        {
            throw new InvalidDataException($"result {dir} is incomplete or corrupt: {ex.Message}", ex);
        }
    }

    // ---- compare.json ----

    private static void WriteMeta(string dir, Action<Utf8JsonWriter> body)
    {
        var path = Path.Combine(dir, MetaName);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new Utf8JsonWriter(fs, MetaOptions))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static JsonDocument ReadMeta(string dir)
    {
        var path = Path.Combine(dir, MetaName);
        if (!File.Exists(path)) throw new InvalidDataException($"{dir} is not a CodeDiffer result (no {MetaName})");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllBytes(path)); }
        catch (JsonException ex) { throw new InvalidDataException($"{path} is corrupt: {ex.Message}", ex); }
        var m = doc.RootElement;
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.String
            || f.GetString() != Format)
        {
            doc.Dispose();
            throw new InvalidDataException($"{path} is not a CodeDiffer result");
        }
        if (!m.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int ver) || ver != FormatVersion)
        {
            var msg = $"{path}: result format version {(v.ValueKind == JsonValueKind.Undefined ? "(none)" : v.GetRawText())} is not supported (this build reads {FormatVersion})";
            doc.Dispose();
            throw new InvalidDataException(msg);
        }
        return doc;
    }

    private static void Head(Utf8JsonWriter w, string kind, string id, string state, DateTime startedUtc, TimeSpan? elapsed)
    {
        w.WriteString("format", Format);
        w.WriteNumber("version", FormatVersion);
        w.WriteString("kind", kind);
        w.WriteString("id", id);
        w.WriteString("state", state);
        w.WriteString("tool", Version);
        w.WriteString("started", startedUtc.ToString("O", CultureInfo.InvariantCulture));
        if (elapsed is { } e) w.WriteNumber("elapsedSeconds", Math.Round(e.TotalSeconds, 3));
        if (state == "running")
        {
            // Who is running it: a listing can then tell a live compare from one whose process died.
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            w.WriteNumber("pid", me.Id);
            w.WriteString("host", Environment.MachineName);
        }
    }

    /// <summary>
    /// The state as saved, except that a "running" compare whose process is gone is "stopped": its process (same host,
    /// same pid, started no later than the compare) no longer runs. One from another host, or without a pid (saved
    /// before 2026-10-07), stays "running" — it can't be checked from here.
    /// </summary>
    private static string State(JsonElement m) => Live(m).State;

    /// <summary><see cref="State"/>, and whether a "running" compare's process was seen running here (false when it
    /// can't be checked).</summary>
    private static (string State, bool Alive) Live(JsonElement m)
    {
        var state = Str(m, "state");
        if (state != "running" || !m.TryGetProperty("pid", out var p) || !p.TryGetInt32(out int pid)
            || !string.Equals(m.TryGetProperty("host", out var h) ? h.GetString() : null, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return (state, false);
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            // A pid is reused: a process that started after the compare did is not the one running it.
            return proc.HasExited || proc.StartTime.ToUniversalTime() > Started(m).AddSeconds(5) ? ("stopped", false) : (state, true);
        }
        catch (ArgumentException) { return ("stopped", false); }              // no such process
        catch (InvalidOperationException) { return ("stopped", false); }      // it exited while we looked
        catch (System.ComponentModel.Win32Exception) { return (state, false); } // not allowed to look: can't tell
    }

    private static string Pid(JsonElement m) => m.TryGetProperty("pid", out var p) ? p.ToString() : "?";

    private static void Roots(Utf8JsonWriter w, string kind, IReadOnlyList<string> roots)
    {
        string[] names = kind == "compare3" ? ["base", "v1", "v2"] : ["left", "right"];
        w.WriteStartObject("roots");
        for (int i = 0; i < Math.Min(names.Length, roots.Count); i++) WritePath(w, names[i], roots[i]);
        w.WriteEndObject();
    }

    private static void Options(Utf8JsonWriter w, CompareOptions o)
    {
        w.WriteStartObject("options");
        w.WriteString("cache", o.Cache.ToString().ToLowerInvariant());
        w.WriteNumber("threads", o.Parallelism);
        w.WriteBoolean("strictStat", o.StrictStat);
        w.WriteEndObject();
    }

    private static void Stats(Utf8JsonWriter w, CompareReport r)
    {
        w.WriteStartObject();
        w.WriteNumber("comparedPairs", r.ComparedPairs);
        w.WriteNumber("cacheHits", r.CacheHits);
        w.WriteNumber("codeCompassHits", r.CodeCompassHits);
        w.WriteNumber("reusedLeftFiles", r.ReusedLeftFiles);
        w.WriteNumber("unstableFiles", r.UnstableFiles);
        w.WriteNumber("pendingFiles", r.PendingFiles);
        w.WriteNumber("bytesRead", r.BytesRead);
        w.WriteNumber("leftDroppedDirectories", r.LeftDroppedDirectories);
        w.WriteNumber("rightDroppedDirectories", r.RightDroppedDirectories);
        w.WriteNumber("skippedLinks", r.SkippedLinks);
        Skipped(w, "leftSkipped", r.LeftSkipped);
        Skipped(w, "rightSkipped", r.RightSkipped);
        if (r.CacheSaveError is { } se) w.WriteString("cacheSaveError", se);
        if (r.RenameLimit is { } rl) w.WriteString("renameLimit", rl);
        Timings(w, r.Timings);
        w.WriteEndObject();
    }

    private static void Timings(Utf8JsonWriter w, IReadOnlyList<(string Phase, TimeSpan Elapsed)> timings)
    {
        w.WriteStartArray("timings");
        foreach (var (phase, elapsed) in timings)
        {
            w.WriteStartObject();
            w.WriteString("phase", phase);
            w.WriteNumber("seconds", Math.Round(elapsed.TotalSeconds, 3));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static void Skipped(Utf8JsonWriter w, string name, IReadOnlyList<CodeDiffer.Core.Walk.SkippedPath> skipped)
    {
        if (skipped.Count == 0) return;
        w.WriteStartArray(name);
        foreach (var s in skipped)
        {
            w.WriteStartObject();
            WritePath(w, "path", s.Path);
            w.WriteString("kind", s.Kind == CodeDiffer.Core.Walk.SkipKind.Link ? "link" : "name");
            if (s.IsDirectory) w.WriteBoolean("dir", true);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static List<CodeDiffer.Core.Walk.SkippedPath> ReadSkipped(JsonElement stats, string name)
        => stats.TryGetProperty(name, out var a)
            ? a.EnumerateArray().Select(x => new CodeDiffer.Core.Walk.SkippedPath(RelOf(x, "path"),
                Str(x, "kind") == "link" ? CodeDiffer.Core.Walk.SkipKind.Link : CodeDiffer.Core.Walk.SkipKind.Name,
                x.TryGetProperty("dir", out var d) && d.GetBoolean())).ToList()
            : [];

    private static List<(string, TimeSpan)> ReadTimings(JsonElement e)
        => e.TryGetProperty("timings", out var t)
            ? t.EnumerateArray().Select(x => (Str(x, "phase"), TimeSpan.FromSeconds(x.GetProperty("seconds").GetDouble()))).ToList()
            : [];

    private static CompareReport ReadReport(string changesFile, JsonElement stats)
        => new(ReadLines(changesFile, ReadChange),
               stats.GetProperty("leftDroppedDirectories").GetInt32(), stats.GetProperty("rightDroppedDirectories").GetInt32())
        {
            ComparedPairs = stats.GetProperty("comparedPairs").GetInt32(),
            CacheHits = stats.GetProperty("cacheHits").GetInt32(),
            CodeCompassHits = stats.GetProperty("codeCompassHits").GetInt32(),
            ReusedLeftFiles = stats.TryGetProperty("reusedLeftFiles", out var reused) ? reused.GetInt32() : 0, // absent in results saved before 2026-10-06
            UnstableFiles = stats.GetProperty("unstableFiles").GetInt32(),
            PendingFiles = stats.GetProperty("pendingFiles").GetInt32(),
            BytesRead = stats.GetProperty("bytesRead").GetInt64(),
            SkippedLinks = stats.TryGetProperty("skippedLinks", out var links) ? links.GetInt32() : 0, // absent before 2026-10-07
            LeftSkipped = ReadSkipped(stats, "leftSkipped"), // absent before 2026-10-08
            RightSkipped = ReadSkipped(stats, "rightSkipped"),
            CacheSaveError = stats.TryGetProperty("cacheSaveError", out var cse) ? cse.GetString() : null,
            RenameLimit = stats.TryGetProperty("renameLimit", out var rlim) ? rlim.GetString() : null,
            Timings = ReadTimings(stats),
        };

    // ---- jsonl ----

    private static void WriteLines<T>(string path, IEnumerable<T> items, Action<Utf8JsonWriter, T> write)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var w = new Utf8JsonWriter(fs, LineOptions);
            foreach (var item in items)
            {
                write(w, item);
                w.Flush();
                fs.WriteByte((byte)'\n');
                w.Reset(fs);
            }
            w.Dispose();
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static List<T> ReadLines<T>(string path, Func<JsonElement, T> read)
    {
        var list = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            list.Add(read(doc.RootElement));
        }
        return list;
    }

    private static void WriteChange(Utf8JsonWriter w, FileChange c) => WriteChange(w, c, null);

    private static void WriteChange(Utf8JsonWriter w, FileChange c, string? name)
    {
        if (name is null) w.WriteStartObject(); else w.WriteStartObject(name);
        WritePath(w, "path", c.RelativePath);
        w.WriteString("status", Token(c.Status));
        if (c.Reason is { } r) w.WriteString("reason", CanonicalTokens.Token(r));
        w.WriteNumber("leftSize", c.LeftSize);
        w.WriteNumber("rightSize", c.RightSize);
        if (c.RenamedFrom is { } f) WritePath(w, "from", f);
        if (c.SimilarityMilli is { } s) w.WriteNumber("similarity", s);
        if (c.Unreadable is { } u) w.WriteString("unreadable", u);
        if (c.EditedRename) w.WriteBoolean("edited", true);
        if (c.BehindLink) w.WriteBoolean("behindLink", true);
        w.WriteEndObject();
    }

    private static FileChange ReadChange(JsonElement e)
    {
        var c = ReadChangeFields(e);
        if (c.Status == ChangeStatus.Renamed && c.RenamedFrom is null)
            throw new FormatException($"rename of {c.RelativePath} without its old path");
        return c;
    }

    private static FileChange ReadChangeFields(JsonElement e) => new(
        RelOf(e, "path"),
        Status(Str(e, "status")),
        e.TryGetProperty("reason", out var r) ? CanonicalTokens.Reason(r.GetString()!) : null,
        e.GetProperty("leftSize").GetInt64(),
        e.GetProperty("rightSize").GetInt64(),
        e.TryGetProperty("from", out _) ? RelOf(e, "from") : null,
        e.TryGetProperty("similarity", out var s) ? s.GetInt32() : null,
        e.TryGetProperty("unreadable", out var u) ? u.GetString() : null,
        e.TryGetProperty("edited", out var ed) && ed.GetBoolean(),
        e.TryGetProperty("behindLink", out var bl) && bl.GetBoolean());

    private static void WriteEntry(Utf8JsonWriter w, Merge3Entry e)
    {
        w.WriteStartObject();
        WritePath(w, "path", e.Path);
        w.WriteString("outcome", Token(e.Outcome));
        if (e.ConflictKind is { } k) w.WriteString("kind", k);
        w.WriteNumber("conflicts", e.ConflictRegions);
        w.WriteNumber("clean", e.CleanRegions);
        if (e.MergedPath is { } m) WritePath(w, "merged", m);
        if (e.Note is { } n) w.WriteString("note", n);
        if (e.V1 is { } v1) WriteChange(w, v1, "v1");
        if (e.V2 is { } v2) WriteChange(w, v2, "v2");
        w.WriteEndObject();
    }

    private static Merge3Entry ReadEntry(JsonElement e) => new(
        RelOf(e, "path"),
        e.TryGetProperty("v1", out var v1) ? ReadChange(v1) : null,
        e.TryGetProperty("v2", out var v2) ? ReadChange(v2) : null,
        Outcome(Str(e, "outcome")),
        e.TryGetProperty("kind", out var k) ? k.GetString() : null,
        e.GetProperty("conflicts").GetInt32(),
        e.GetProperty("clean").GetInt32(),
        e.TryGetProperty("merged", out _) ? RelOf(e, "merged") : null,
        e.TryGetProperty("note", out var n) ? n.GetString() : null);

    // ---- tokens / helpers ----

    internal static string Token(ChangeStatus s) => s.ToString().ToLowerInvariant();

    internal static string Token(Merge3Outcome o) => o switch
    {
        Merge3Outcome.V1Only => "v1only",
        Merge3Outcome.V2Only => "v2only",
        Merge3Outcome.Agreed => "agreed",
        Merge3Outcome.Merged => "merged",
        _ => "conflict",
    };

    /// <summary>A status as <see cref="Token(ChangeStatus)"/> writes it; anything else (a number too, which Enum.Parse would
    /// take) is a corrupt file.</summary>
    private static ChangeStatus Status(string t)
    {
        foreach (var v in Enum.GetValues<ChangeStatus>())
            if (string.Equals(Token(v), t, StringComparison.OrdinalIgnoreCase)) return v;
        throw new FormatException($"unknown status '{t}'");
    }

    private static Merge3Outcome Outcome(string t) => t switch
    {
        "v1only" => Merge3Outcome.V1Only,
        "v2only" => Merge3Outcome.V2Only,
        "agreed" => Merge3Outcome.Agreed,
        "merged" => Merge3Outcome.Merged,
        "conflict" => Merge3Outcome.Conflict,
        _ => throw new FormatException($"unknown outcome '{t}'"),
    };

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";

    private const string Utf16Suffix = "16";

    /// <summary>
    /// A path, written so it reads back exactly. A Windows name may hold an unpaired UTF-16 surrogate, which JSON's
    /// UTF-8 can't carry (it would come back as U+FFFD, naming another file): such a path also gets
    /// <c>&lt;name&gt;16</c>, its UTF-16 code units in hex, which <see cref="PathOf"/> prefers.
    /// </summary>
    private static void WritePath(Utf8JsonWriter w, string name, string path)
    {
        if (!HasLoneSurrogate(path))
        {
            w.WriteString(name, path);
            return;
        }
        var hex = new System.Text.StringBuilder(path.Length * 4);
        foreach (var ch in path) hex.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
        w.WriteString(name, Shown(path)); // readable, and what an older reader gets
        w.WriteString(name + Utf16Suffix, hex.ToString());
    }

    /// <summary>A path as UTF-8 shows it: each unpaired surrogate as U+FFFD (what an agent reads in a listing, and so
    /// may ask for).</summary>
    internal static string Shown(string path)
    {
        if (!HasLoneSurrogate(path)) return path;
        var shown = new System.Text.StringBuilder(path.Length);
        for (int i = 0; i < path.Length; i++)
        {
            bool pair = char.IsHighSurrogate(path[i]) && i + 1 < path.Length && char.IsLowSurrogate(path[i + 1]);
            if (pair) shown.Append(path, i++, 2);
            else shown.Append(char.IsSurrogate(path[i]) ? '\uFFFD' : path[i]);
        }
        return shown.ToString();
    }

    private static string PathOf(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name + Utf16Suffix, out var raw)) return Str(e, name);
        var hex = raw.GetString() ?? "";
        if (hex.Length % 4 != 0) throw new FormatException($"bad {name}{Utf16Suffix}");
        var chars = new char[hex.Length / 4];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)int.Parse(hex.AsSpan(i * 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new string(chars);
    }

    /// <summary>A path inside a tree, as a compare saves it ('/'-separated, relative). Anything that could reach outside the
    /// tree it is acted on in (rooted, a drive, an empty, "." or ".." part, a backslash on Windows) is a corrupt file.</summary>
    private static string RelOf(JsonElement e, string name)
    {
        var p = PathOf(e, name);
        if (!IsSafeRelative(p)) throw new FormatException($"{name} '{p}' is not a relative path inside the tree");
        return p;
    }

    internal static bool IsSafeRelative(string p)
        => p.Length > 0 && !Path.IsPathRooted(p) && p.IndexOf('\0') < 0
           && !(OperatingSystem.IsWindows() && (p.Contains('\\') || p.Contains(':')))
           && p.Split('/').All(s => s is not ("" or "." or ".."));

    internal static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            else if (char.IsSurrogate(s[i])) return true;
        }
        return false;
    }

    private static DateTime Started(JsonElement m)
        => DateTime.Parse(Str(m, "started"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static TimeSpan Elapsed(JsonElement m)
    {
        if (!m.TryGetProperty("elapsedSeconds", out var e)) return TimeSpan.Zero;
        double s = e.GetDouble();
        return double.IsFinite(s) && s >= 0 && s < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(s)
            : throw new FormatException($"elapsedSeconds {s} is out of range");
    }

    /// <summary>Is <paramref name="path"/> equal to or inside <paramref name="tree"/>? (case-insensitive on Windows)</summary>
    internal static bool IsUnder(string path, string tree)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static string Dir(string d) // a drive root (C:\) keeps its separator when trimmed: add one only if missing
        {
            d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(d));
            return Path.EndsInDirectorySeparator(d) ? d : d + Path.DirectorySeparatorChar;
        }
        return Dir(path).StartsWith(Dir(tree), cmp);
    }

    /// <summary>Is <paramref name="path"/> inside <paramref name="tree"/> (or, unless <paramref name="oneWay"/>, the other
    /// way round), by name or by what the names resolve to — a junction, symlink, subst or mapped drive naming the tree
    /// by another path counts. For the gates that keep an output out of an input, not for per-file checks.</summary>
    internal static bool Overlaps(string path, string tree, bool oneWay = false)
    {
        if (IsUnder(path, tree) || (!oneWay && IsUnder(tree, path))) return true;
        string rp = CodeDiffer.Core.Walk.RealPath.Of(path), rt = CodeDiffer.Core.Walk.RealPath.Of(tree);
        return IsUnder(rp, rt) || (!oneWay && IsUnder(rt, rp));
    }
}
