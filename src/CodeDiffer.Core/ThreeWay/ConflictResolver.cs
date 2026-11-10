using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Port;
using CodeDiffer.Core.Sessions;

namespace CodeDiffer.Core.ThreeWay;

/// <summary>
/// One conflict of a merge overlay, as its <c>conflicts.json</c> records it. <see cref="Path"/>: the base path (the
/// compare3 entry's). <see cref="Markers"/>: where <c>files\</c> holds it with diff3 markers, and <see cref="AlsoDelete"/>
/// the v1 path deleted for that file (a rename); null for a conflict not written. <see cref="V1At"/>/<see cref="V2At"/>:
/// where each side has the file (null: that side deleted it, or doesn't have it); <see cref="V2Size"/>/<see cref="V2Ticks"/>:
/// v2's file when the overlay was written (size -1: it couldn't be read). <see cref="Failed"/>: a file the overlay failed
/// to write — not a choice to make here; write the overlay again. <see cref="MarkersSize"/>/<see cref="MarkersTicks"/>:
/// the markers file as the overlay wrote it (size -1: not recorded), so one edited by hand since is never set aside.
/// <see cref="Blocks"/>: its conflict blocks' line counts, in file order (null: not recorded, an older overlay), so they
/// can be settled one at a time (<see cref="ConflictBlocks"/>).
/// </summary>
public sealed record OverlayConflict(string Path, string Kind, string? Note, string? Markers, string? AlsoDelete,
    string? V1At, string? V2At, long V2Size, long V2Ticks, bool Failed, long MarkersSize = -1, long MarkersTicks = 0,
    IReadOnlyList<ConflictBlock>? Blocks = null);

/// <summary>A choice recorded for one conflict, and what it did to the overlay (so it can be undone): the markers file
/// moved to <c>set-aside\</c>, v2's file copied into <c>files\</c>, and the lines it added to and removed from
/// <c>deletes.txt</c>.</summary>
public sealed record ConflictDecision(string Path, string Choice, string Rule, DateTime AtUtc, string? SetAside, string? Copied,
    IReadOnlyList<string> AddedDeletes, IReadOnlyList<string> RemovedDeletes);

/// <summary>A merge overlay's conflicts and the choices recorded for them.</summary>
public sealed class OverlayConflicts
{
    public const string FileName = "conflicts.json", DecisionsName = "decisions.json", ResolvingName = "RESOLVING.json";
    public const string SetAsideDir = "set-aside", OriginalsDir = "originals", HandMergesDir = "hand-merges", BlocksName = "blocks.json";

    public required string Dir { get; init; }
    public required string Base { get; init; }
    public required string V1 { get; init; }
    public required string V2 { get; init; }
    /// <summary>Sorted by path (ordinal).</summary>
    public required IReadOnlyList<OverlayConflict> All { get; init; }
    /// <summary>By conflict path.</summary>
    public required IReadOnlyDictionary<string, ConflictDecision> Decisions { get; init; }
    /// <summary>By conflict path, the undecided ones whose markers file was edited since the overlay wrote it: how many
    /// conflict blocks it still has. None left: merged by hand, which counts as decided.</summary>
    public required IReadOnlyDictionary<string, int> Edited { get; init; }
    /// <summary>The edited ones whose markers file is as a block resolve last wrote it (its size and time, kept in
    /// <c>blocks.json</c>): settled by blocks, not by hand. Edited by hand since, it reads as merged by hand again.</summary>
    public IReadOnlySet<string> ByBlocks { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool HandMerged(OverlayConflict c) => Edited.TryGetValue(c.Path, out var left) && left == 0;
    /// <summary>How an edited conflict is being merged, or was: "settled by blocks" or "merged by hand".</summary>
    public string HowMerged(OverlayConflict c) => ByBlocks.Contains(c.Path) ? "settled by blocks" : "merged by hand";
    public bool IsDecided(OverlayConflict c) => Decisions.ContainsKey(c.Path) || HandMerged(c);
    public IEnumerable<OverlayConflict> Undecided => All.Where(c => !IsDecided(c));

    /// <summary>Written by <see cref="MergeOverlay.Write"/>. Paths are written with an unpaired surrogate as <c>\uXXXX</c>.</summary>
    internal static void Write(string dir, string baseDir, string v1, string v2, IEnumerable<OverlayConflict> conflicts)
    {
        using var fs = new FileStream(Path.Combine(dir, FileName), FileMode.Create, FileAccess.Write);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteNumber("format", 1);
        w.WriteString("base", baseDir);
        w.WriteString("v1", v1);
        w.WriteString("v2", v2);
        w.WriteStartArray("conflicts");
        foreach (var c in conflicts.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.WriteString("path", ResultStore.Escaped(c.Path));
            w.WriteString("kind", c.Kind);
            Opt(w, "note", c.Note);
            Opt(w, "markers", c.Markers);
            Opt(w, "alsoDelete", c.AlsoDelete);
            Opt(w, "v1", c.V1At);
            Opt(w, "v2", c.V2At);
            w.WriteNumber("v2Size", c.V2Size);
            w.WriteNumber("v2Ticks", c.V2Ticks);
            if (c.Failed) w.WriteBoolean("failed", true);
            if (c.Markers is not null && c.MarkersSize >= 0)
            {
                w.WriteNumber("markersSize", c.MarkersSize);
                w.WriteNumber("markersTicks", c.MarkersTicks);
            }
            if (c.Markers is not null && c.Blocks is { } bs)
            {
                // Each block as [v1 lines, base lines, v2 lines, open]: open's bits 1 (v1), 2 (base), 4 (v2) say which
                // section's last line had no line ending (the marker after it had one added).
                w.WriteStartArray("blocks");
                foreach (var b in bs)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(b.V1);
                    w.WriteNumberValue(b.Base);
                    w.WriteNumberValue(b.V2);
                    w.WriteNumberValue(b.Open);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void Opt(Utf8JsonWriter w, string name, string? path)
    {
        if (path is null) w.WriteNull(name);
        else w.WriteString(name, ResultStore.Escaped(path));
    }

    /// <summary>The overlay's conflicts and decisions. Throws <see cref="ArgumentException"/> for a directory that isn't a
    /// finished overlay, or one written before CodeDiffer recorded its conflicts as JSON.</summary>
    public static OverlayConflicts Load(string overlay)
    {
        overlay = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(overlay));
        if (!File.Exists(System.IO.Path.Combine(overlay, "OVERLAY.txt")))
            throw new ArgumentException($"{overlay} is not a merge overlay (no OVERLAY.txt)");
        if (File.Exists(System.IO.Path.Combine(overlay, "INCOMPLETE.txt")))
            throw new ArgumentException($"{overlay} was not finished (INCOMPLETE.txt): write the overlay again");
        var file = System.IO.Path.Combine(overlay, FileName);
        if (!File.Exists(file))
            throw new ArgumentException($"{overlay} has no {FileName} (written by an older CodeDiffer): write the overlay again to decide its conflicts here");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var root = doc.RootElement;
        var all = new List<OverlayConflict>();
        foreach (var c in root.GetProperty("conflicts").EnumerateArray())
            all.Add(new OverlayConflict(Rel(c, "path")!, c.GetProperty("kind").GetString()!, P(c, "note"),
                Rel(c, "markers"), Rel(c, "alsoDelete"), Rel(c, "v1"), Rel(c, "v2"), c.GetProperty("v2Size").GetInt64(),
                c.GetProperty("v2Ticks").GetInt64(), c.TryGetProperty("failed", out var f) && f.GetBoolean(),
                c.TryGetProperty("markersSize", out var ms) ? ms.GetInt64() : -1, c.TryGetProperty("markersTicks", out var mt) ? mt.GetInt64() : 0,
                c.TryGetProperty("blocks", out var bs) ? [.. bs.EnumerateArray().Select(Block)] : null));
        var decisions = ReadDecisions(System.IO.Path.Combine(overlay, DecisionsName));
        var edited = new Dictionary<string, int>(StringComparer.Ordinal);
        var written = ReadBlockWrites(System.IO.Path.Combine(overlay, BlocksName));
        var byBlocks = new HashSet<string>(StringComparer.Ordinal);
        var files = System.IO.Path.Combine(overlay, "files");
        foreach (var c in all)
            if (!decisions.ContainsKey(c.Path) && c.Markers is { } m && c.MarkersSize >= 0)
            {
                var full = System.IO.Path.Combine(files, m.Replace('/', System.IO.Path.DirectorySeparatorChar));
                var fi = new FileInfo(full);
                // Saved with the same bytes (an editor's save, a copy onto coarser file times) is not an edit.
                try
                {
                    if (fi.Exists && (fi.Length != c.MarkersSize || fi.LastWriteTimeUtc.Ticks != c.MarkersTicks)
                        && !(fi.Length == c.MarkersSize && SameBytes(full, System.IO.Path.Combine(overlay, OriginalsDir, m.Replace('/', System.IO.Path.DirectorySeparatorChar)))))
                    {
                        edited[c.Path] = MarkerBlocks(full);
                        if (written.TryGetValue(c.Path, out var w) && w.Size == fi.Length && w.Ticks == fi.LastWriteTimeUtc.Ticks) byBlocks.Add(c.Path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new ArgumentException($"files\\{m.Replace('/', '\\')} can't be read ({ex.Message}): close what holds it, then run this again");
                }
            }
        return new OverlayConflicts
        {
            Dir = overlay,
            Base = root.GetProperty("base").GetString()!,
            V1 = root.GetProperty("v1").GetString()!,
            V2 = root.GetProperty("v2").GetString()!,
            All = all,
            Decisions = decisions,
            Edited = edited,
            ByBlocks = byBlocks,
        };
    }

    /// <summary>blocks.json: by conflict path, its markers file's size and time as a block resolve last wrote it.</summary>
    internal static Dictionary<string, (long Size, long Ticks)> ReadBlockWrites(string file)
    {
        var d = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        if (!File.Exists(file)) return d;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var e in doc.RootElement.GetProperty("files").EnumerateArray())
            d[Rel(e, "path")!] = (e.GetProperty("size").GetInt64(), e.GetProperty("ticks").GetInt64());
        return d;
    }

    internal static byte[] BlockWritesJson(Dictionary<string, (long Size, long Ticks)> d)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("format", 1);
            w.WriteStartArray("files");
            foreach (var (path, (size, ticks)) in d.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("path", ResultStore.Escaped(path));
                w.WriteNumber("size", size);
                w.WriteNumber("ticks", ticks);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>Whether two files hold the same bytes; false when <paramref name="b"/> isn't there.</summary>
    private static bool SameBytes(string a, string b)
    {
        if (!File.Exists(b)) return false;
        using var x = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var y = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        if (x.Length != y.Length) return false;
        var p = new byte[1 << 16];
        var q = new byte[1 << 16];
        for (int n; (n = x.ReadAtLeast(p, p.Length, throwOnEndOfStream: false)) > 0;)
            if (y.ReadAtLeast(q, n, throwOnEndOfStream: false) != n || !p.AsSpan(0, n).SequenceEqual(q.AsSpan(0, n))) return false;
        return true;
    }

    /// <summary>How many conflict blocks a markers file holds: the most of any one of the marker lines the merger writes
    /// (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; v1 (</c>, <c>||||||| base (</c>, <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; v2 (</c>), so a block with only some of its
    /// lines removed still counts. A bare <c>=======</c> is not counted: text has those of its own.</summary>
    public static int MarkerBlocks(string path)
    {
        int open = 0, mid = 0, close = 0;
        using var r = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        for (var line = r.ReadLine(); line is not null; line = r.ReadLine())
        {
            if (line.StartsWith("<<<<<<< v1 (", StringComparison.Ordinal)) open++;
            else if (line.StartsWith("||||||| base (", StringComparison.Ordinal)) mid++;
            else if (line.StartsWith(">>>>>>> v2 (", StringComparison.Ordinal)) close++;
        }
        return Math.Max(open, Math.Max(mid, close));
    }

    private static ConflictBlock Block(JsonElement b)
    {
        int Count(int i) => b[i].GetInt32() is >= 0 and var n ? n : throw new ArgumentException("a conflict block's line count is negative — this overlay's records were edited; write the overlay again");
        return new ConflictBlock(Count(0), Count(1), Count(2), b[3].GetInt32() & 7);
    }

    private static string? P(JsonElement c, string name)
        => c.GetProperty(name).GetString() is { } s ? ResultStore.Unescaped(s) : null;

    /// <summary>A path inside a tree, as the overlay's own files hold it; anything else (rooted, "..", empty parts) is
    /// refused, so an edited conflicts.json, decisions.json or journal can never reach outside the overlay and the trees.</summary>
    internal static string? Rel(JsonElement c, string name) => P(c, name) is { } p ? Plain(p) : null;

    internal static string Plain(string p)
    {
        if (p.Length == 0 || System.IO.Path.IsPathRooted(p) || (OperatingSystem.IsWindows() && p.Contains(':')) || p.Split('/', '\\').Any(x => x is "" or "." or ".."))
            throw new ArgumentException($"not a plain path inside a tree: \"{ResultStore.Escaped(p)}\" — this overlay's records were edited; write the overlay again");
        return p;
    }

    internal static Dictionary<string, ConflictDecision> ReadDecisions(string file)
    {
        var d = new Dictionary<string, ConflictDecision>(StringComparer.Ordinal);
        if (!File.Exists(file)) return d;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var e in doc.RootElement.GetProperty("decisions").EnumerateArray()) d[Decision(e).Path] = Decision(e);
        return d;
    }

    internal static ConflictDecision Decision(JsonElement e)
        => new(Rel(e, "path")!, e.GetProperty("choice").GetString()!, e.GetProperty("rule").GetString()!,
            e.GetProperty("at").GetDateTime(), Rel(e, "setAside"), Rel(e, "copied"),
            [.. e.GetProperty("addedDeletes").EnumerateArray().Select(x => Plain(ResultStore.Unescaped(x.GetString()!)))],
            [.. e.GetProperty("removedDeletes").EnumerateArray().Select(x => Plain(ResultStore.Unescaped(x.GetString()!)))]);

    internal static void WriteDecision(Utf8JsonWriter w, ConflictDecision d)
    {
        w.WriteStartObject();
        w.WriteString("path", ResultStore.Escaped(d.Path));
        w.WriteString("choice", d.Choice);
        w.WriteString("rule", d.Rule);
        w.WriteString("at", d.AtUtc);
        Opt(w, "setAside", d.SetAside);
        Opt(w, "copied", d.Copied);
        w.WriteStartArray("addedDeletes");
        foreach (var x in d.AddedDeletes) w.WriteStringValue(ResultStore.Escaped(x));
        w.WriteEndArray();
        w.WriteStartArray("removedDeletes");
        foreach (var x in d.RemovedDeletes) w.WriteStringValue(ResultStore.Escaped(x));
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>The conflicts grouped (undecided ones by kind, folder and file type) so whole groups can be settled with
    /// one <c>resolve</c>; with <paramref name="list"/>, the conflicts matching <paramref name="patterns"/> and
    /// <paramref name="kinds"/> one per line too, at most <paramref name="max"/>.</summary>
    public string Text(IReadOnlyList<string> patterns, IReadOnlyList<string> kinds, int depth = 1, bool list = false, int max = 50)
    {
        var o = new StringBuilder();
        var matcher = new ConflictMatcher(patterns, kinds);
        var picked = All.Where(matcher.Matches).ToList();
        var open = picked.Where(c => !IsDecided(c)).ToList();
        var decided = picked.Where(IsDecided).ToList();
        string Choice(OverlayConflict c) => Decisions.TryGetValue(c.Path, out var d) ? d.Choice : HowMerged(c);
        string scope = matcher.Any ? $" matching {matcher}" : "";
        o.Append($"{picked.Count:N0} conflict(s){scope} in {Dir} · {open.Count:N0} undecided · {decided.Count:N0} decided");
        if (decided.Count > 0)
            o.Append(" (" + string.Join(" · ", decided.GroupBy(Choice).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Count():N0} {g.Key}")) + ")");
        o.Append('\n');
        var partly = open.Where(c => Edited.ContainsKey(c.Path)).ToLookup(c => ByBlocks.Contains(c.Path));
        if (partly[false].Any())
            o.Append($"{partly[false].Count():N0} being merged by hand: edited, with {partly[false].Sum(c => Edited[c.Path]):N0} conflict block(s) still in them\n");
        if (partly[true].Any())
            o.Append($"{partly[true].Count():N0} being settled by blocks, with {partly[true].Sum(c => Edited[c.Path]):N0} conflict block(s) still in them\n");
        if (open.Count > 0)
        {
            Group(o, "undecided, by kind", open, c => c.Kind + (c.Markers is not null ? " (markers in files\\)" : ""));
            Group(o, $"undecided, by folder (depth {depth}; --depth N for more)", open, c => Folder(c.Path, depth));
            Group(o, "undecided, by file type", open, c => System.IO.Path.GetExtension(c.Path) is { Length: > 0 } x ? x.ToLowerInvariant() : "(no extension)");
        }
        if (list)
        {
            if (open.Count > 0) o.Append("undecided:\n");
            foreach (var c in open.Take(max))
                o.Append($"  {c.Kind,-14} {ResultStore.Escaped(c.Path)}").Append(c.Note is { } n ? $"  — {n}" : "")
                 .Append(Edited.TryGetValue(c.Path, out var left) ? $"  (being {HowMerged(c)}: {left:N0} block(s) left)" : "").Append('\n');
            if (open.Count > max) o.Append($"  ... {open.Count - max:N0} more (--max N)\n");
            if (decided.Count > 0) o.Append("decided:\n");
            foreach (var c in decided.Take(max))
                o.Append(Decisions.TryGetValue(c.Path, out var d) ? $"  {d.Choice,-10} {ResultStore.Escaped(c.Path)}  (rule: {d.Rule})\n"
                    : $"  {"merged",-10} {ResultStore.Escaped(c.Path)}  ({(ByBlocks.Contains(c.Path) ? "by blocks" : "by hand")}: files\\{ResultStore.Escaped(c.Markers!).Replace('/', '\\')})\n");
            if (decided.Count > max) o.Append($"  ... {decided.Count - max:N0} more (--max N)\n");
        }
        if (open.Any(c => c.Failed))
            o.Append($"{open.Count(c => c.Failed):N0} \"not written\" are files the overlay failed to write: write the overlay again for them\n");
        if (open.Count == 0) o.Append("nothing left to decide here: apply it with codediffer apply-overlay\n");
        else
        {
            if (open.Any(c => c.Markers is not null))
            {
                o.Append("merge one by hand: edit its file in files\\ and save it; with no markers left it counts as merged\n");
                o.Append("or pick a side per block: codediffer conflicts <overlay> <pattern> --blocks, then resolve ... v1|v2|both <pattern> --blocks N\n");
            }
            o.Append($"settle a group: codediffer resolve \"{Dir}\" v1|v2|regenerate <pattern>... [--kind K]   (dry run; --write records it)\n");
        }
        return o.ToString();
    }

    private static void Group(StringBuilder o, string title, List<OverlayConflict> cs, Func<OverlayConflict, string> key, int top = 15)
    {
        var groups = cs.GroupBy(key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        o.Append(title).Append(":\n");
        int width = Math.Min(40, groups.Take(top).Max(g => g.Key.Length));
        foreach (var g in groups.Take(top)) o.Append($"  {ResultStore.Escaped(g.Key).PadRight(width)}  {g.Count(),7:N0}\n");
        if (groups.Count > top) o.Append($"  ... {groups.Count - top:N0} more ({groups.Skip(top).Sum(g => g.Count()):N0} conflict(s))\n");
    }

    /// <summary>The first <paramref name="depth"/> folders of a path, with a trailing '/'; "(top)" for a file above that depth's folders.</summary>
    internal static string Folder(string path, int depth)
    {
        var parts = path.Split('/');
        if (parts.Length == 1) return "(top)";
        return string.Join('/', parts.Take(Math.Min(depth, parts.Length - 1))) + "/";
    }
}

/// <summary>Which conflicts a rule picks: any of its path patterns (none: every path) and any of its kinds (none: every
/// kind). A pattern is '/'-separated, with <c>*</c> (within a name), <c>?</c> and <c>**</c> (any folders). Without a '/'
/// it matches a file or folder name at any depth (<c>*.h</c>, <c>generated</c>); with one (a leading one too:
/// <c>/generated</c>) it is anchored at the tree's top and matches that path or anything under it (<c>src/gen</c>,
/// <c>src/**/regs_*.h</c>). A leading <c>./</c> is dropped; an empty pattern is refused (it would pick everything). A conflict matches by any
/// path it has: its base path, or where v1 or v2 has it. Case is ignored where the file system ignores it.</summary>
public sealed class ConflictMatcher
{
    private readonly IReadOnlyList<string> _patterns, _kinds;
    private readonly List<(Regex Re, bool Name)> _res = [];

    public ConflictMatcher(IReadOnlyList<string> patterns, IReadOnlyList<string> kinds)
    {
        _patterns = patterns;
        _kinds = kinds;
        foreach (var raw in patterns)
        {
            var p = raw.Trim().Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..]; // what a shell's completion gives: .\src\gen
            bool anchored = p.StartsWith('/');
            p = p.Trim('/');
            // An empty pattern would pick every conflict: --all says that out loud instead.
            if (p.Length == 0 || p == ".") throw new ArgumentException($"an empty pattern (\"{raw}\") picks nothing: name a path or a name, or use --all for every conflict");
            bool name = !anchored && !p.Contains('/');
            var re = new StringBuilder("^");
            for (int i = 0; i < p.Length; i++)
            {
                if (p[i] == '*' && i + 1 < p.Length && p[i + 1] == '*')
                {
                    // "**/" is any folders, none included; a "**" elsewhere is anything.
                    if (i + 2 < p.Length && p[i + 2] == '/') { re.Append("(?:.*/)?"); i += 2; }
                    else { re.Append(".*"); i++; }
                }
                else if (p[i] == '*') re.Append("[^/]*");
                else if (p[i] == '?') re.Append("[^/]");
                else re.Append(Regex.Escape(p[i].ToString()));
            }
            re.Append(name ? "$" : "(?:/.*)?$");
            var opts = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
            _res.Add((new Regex(re.ToString(), opts), name));
        }
    }

    public bool Any => _patterns.Count > 0 || _kinds.Count > 0;

    public bool Matches(OverlayConflict c)
    {
        if (_kinds.Count > 0 && !_kinds.Contains(c.Kind, StringComparer.OrdinalIgnoreCase)) return false;
        if (_res.Count == 0) return true;
        foreach (var path in new[] { c.Path, c.V1At, c.V2At })
            if (path is not null && _res.Any(r => r.Name ? path.Split('/').Any(r.Re.IsMatch) : r.Re.IsMatch(path))) return true;
        return false;
    }

    public override string ToString()
        => string.Join(" ", _patterns.Select(p => p.Contains(' ') ? $"\"{p}\"" : p)
            .Concat(_kinds.Count > 0 ? [$"--kind \"{string.Join(",", _kinds)}\""] : []));
}

/// <summary>What <see cref="ConflictResolver.Run"/> did (or, on a dry run, would do).</summary>
public sealed record ResolveResult(string Overlay, string Choice, string Rule, bool Written, IReadOnlyList<ConflictDecision> Done,
    IReadOnlyList<(string Path, string Why)> Refused, int AlreadyDecided, int NotDecided, int Undecided, bool FinishedStopped,
    IReadOnlyList<HandUndo> Restored, IReadOnlyList<BlocksSettled>? Blocks = null, IReadOnlyList<string>? Notes = null);

/// <summary>Blocks of one markers file settled by a side (<see cref="ConflictBlocks"/>): their numbers, and how many
/// blocks the file holds after (none: it is merged).</summary>
public sealed record BlocksSettled(string Path, string Markers, IReadOnlyList<int> Numbers, int Left);

/// <summary>A hand merge undone: <see cref="Markers"/> (in <c>files\</c>) put back as the overlay wrote it, from
/// <c>originals\</c>; the edited file, if <see cref="Existed"/>, moved to <c>set-aside\</c><see cref="Keep"/> first.
/// <see cref="WasMerged"/>: it had no markers left (it counted as decided). <see cref="ByBlocks"/>: it was as a block
/// resolve wrote it, not edited by hand.</summary>
public sealed record HandUndo(string Path, string Markers, string Keep, bool WasMerged, bool Existed, bool ByBlocks = false);

/// <summary>
/// Settles a merge overlay's conflicts in bulk, before it is applied: every conflict a rule (path patterns and kinds,
/// <see cref="ConflictMatcher"/>) picks is taken from v1, taken from v2, or marked to regenerate — or a choice made
/// earlier is undone. It records the choice in the overlay itself, so <see cref="OverlayApplier"/> applies it unchanged:
/// <list type="bullet">
/// <item>v1 (and regenerate, which keeps v1's file until it is regenerated): a markers file in <c>files\</c> is moved
///   to <c>set-aside\</c>, and a delete the overlay made for it (a rename) is dropped, so v1's file stays.</item>
/// <item>v2: the same, then v2's file is copied into <c>files\</c> at v2's path (only if it is still the file the
///   overlay saw: its size and time), and v1's copy at another path, or one v2 deleted, goes into <c>deletes.txt</c>.</item>
/// </list>
/// Dry run unless <c>write</c>. A conflict already decided is left alone (undo it first): by a choice, or merged by
/// hand (its markers file edited, no markers left). So is one being merged by hand (edited, markers left). Undo takes a
/// hand merge back too: the markers file as written comes back from <c>originals\</c>, the edit kept in
/// <c>set-aside\hand-merges\</c>. v2 is never taken where its file would land on
/// another file: one v1 has of its own, another conflict's, or another write in <c>files\</c>; nor for a kind whose
/// sides aren't known well enough (<see cref="NotFromV2"/>): decide those by hand. While it writes, the overlay holds
/// <c>RESOLVING.json</c> (what it is doing; created only if absent, so two resolves never write at once), and
/// apply-overlay refuses it; a resolve that was stopped is finished by the next one, which drops (and reports) a choice
/// of v2 whose file changed in between. An overlay already applied (or being applied) is refused: a choice made now
/// would not reach that tree.
/// </summary>
public static class ConflictResolver
{
    public static readonly string[] Choices = ["v1", "v2", "regenerate", "undo", "both"];

    /// <summary>Kinds v2 is never taken for: v2's path is where another change lands or a directory, or a side is a link
    /// not followed, a directory not listed, a name Windows can't open, or a file that couldn't be read.</summary>
    public static readonly IReadOnlySet<string> NotFromV2 = new HashSet<string>(StringComparer.Ordinal)
        { "path collision", "file/directory clash", "unreadable", "behind a link", "unlisted directory", "unopenable name" };

    /// <summary>Where a resolve makes its copies, in the overlay but outside files\ and set-aside\.</summary>
    public const string TempDir = ".resolve-tmp";

    /// <param name="blocks">Settle conflict blocks instead of whole conflicts (<see cref="ConflictBlocks"/>): "all", or
    /// numbers like 1,3,5-7 (of one markers file); the choice is then v1, v2 or both.</param>
    public static ResolveResult Run(string overlay, string choice, IReadOnlyList<string> patterns, IReadOnlyList<string> kinds, bool write,
        string? blocks = null)
    {
        if (!Choices.Contains(choice)) throw new ArgumentException($"the choice is v1, v2, regenerate, undo or (with blocks) both, not \"{choice}\"");
        if (blocks is not null && choice is not ("v1" or "v2" or "both"))
            throw new ArgumentException($"blocks are settled by v1, v2 or both, not {choice} (undo the file to start it over)");
        if (blocks is null && choice == "both") throw new ArgumentException("both is a choice for blocks: give --blocks N|all");
        var numbers = blocks is null ? null : ConflictBlocks.Parse(blocks);
        var matcher = new ConflictMatcher(patterns, kinds);
        var s = OverlayConflicts.Load(overlay);
        RefuseApplied(s.Dir);
        var journal = Path.Combine(s.Dir, OverlayConflicts.ResolvingName);
        bool finished = false;
        var refused = new List<(string, string)>();
        var notes = new List<string>();
        if (File.Exists(journal))
        {
            if (!write) throw new ArgumentException($"a resolve was stopped part way ({OverlayConflicts.ResolvingName}): run a resolve with --write to finish it first");
            using (var held = Hold(journal)) refused.AddRange(Finish(s, journal, held, notes));
            finished = true;
            s = OverlayConflicts.Load(s.Dir);
        }
        if (blocks is not null) return RunBlocks(s, journal, choice, matcher, numbers, write, finished, refused, notes);
        var stamp = Stamp(s.Dir);

        string rule = matcher.ToString() is { Length: > 0 } r ? r : "--all";
        var deletes = ReadDeletes(s.Dir);
        var files = Path.Combine(s.Dir, "files");
        var aside = Path.Combine(s.Dir, OverlayConflicts.SetAsideDir);
        var done = new List<ConflictDecision>();
        var restores = new List<HandUndo>();
        int already = 0, notDecided = 0;
        var now = DateTime.UtcNow;
        string kept = $"{OverlayConflicts.HandMergesDir}/{now:yyyyMMdd-HHmmss-fff}"; // where this undo keeps the hand merges it takes back
        var claimed = new HashSet<string>(TreeMerger.PathComparer); // files\ paths this run copies into
        // How many conflicts have each path (on a side, or as their markers): v2's file never lands on another's.
        var holders = new Dictionary<string, int>(TreeMerger.PathComparer);
        foreach (var c in s.All)
            foreach (var p in new[] { c.V1At, c.V2At, c.Markers }.OfType<string>().Distinct(TreeMerger.PathComparer))
                holders[p] = holders.GetValueOrDefault(p) + 1;
        // Deletes a choice made, or dropped: a later choice that needs the same is recorded with it too, so undoing one
        // of the two leaves it for the other.
        var choiceAdded = s.Decisions.Values.SelectMany(d => d.AddedDeletes).ToHashSet(StringComparer.Ordinal);
        var choiceRemoved = s.Decisions.Values.SelectMany(d => d.RemovedDeletes).ToHashSet(StringComparer.Ordinal);
        foreach (var c in s.All.Where(matcher.Matches))
        {
            bool isDecided = s.Decisions.TryGetValue(c.Path, out var was);
            if (choice == "undo")
            {
                if (!isDecided)
                {
                    // A markers file edited (merged by hand, or part way) or deleted: the file as written goes back.
                    var m0 = c.Markers;
                    bool there = m0 is not null && File.Exists(Path.Combine(files, Native(m0)));
                    if (m0 is null || c.MarkersSize < 0 || there && !s.Edited.ContainsKey(c.Path)) { notDecided++; continue; }
                    if (!File.Exists(Path.Combine(s.Dir, OverlayConflicts.OriginalsDir, Native(m0))))
                    {
                        refused.Add((c.Path, $"no copy of files\\{m0} as written is kept (an overlay from an older CodeDiffer): write the overlay again"));
                        continue;
                    }
                    restores.Add(new HandUndo(c.Path, m0, $"{kept}/{m0}", s.HandMerged(c), there, s.ByBlocks.Contains(c.Path)));
                    continue;
                }
                // Its markers file goes back where it was, unless another file is there now.
                bool same = was!.SetAside is not null && was.Copied is not null && TreeMerger.PathComparer.Equals(was.SetAside, was.Copied);
                if (was.SetAside is { } m && !same && File.Exists(Path.Combine(aside, Native(m))) && File.Exists(Path.Combine(files, Native(m))))
                {
                    refused.Add((c.Path, $"files\\{m} holds another file now — undo the choice that put it there first"));
                    continue;
                }
                done.Add(was);
                continue;
            }
            if (isDecided || s.HandMerged(c)) { already++; continue; }
            if (c.Failed) { refused.Add((c.Path, "the overlay failed to write it: write the overlay again")); continue; }
            if (s.Edited.TryGetValue(c.Path, out var left))
            {
                refused.Add((c.Path, s.ByBlocks.Contains(c.Path)
                    ? $"files\\{c.Markers} is being settled by blocks ({left:N0} conflict block(s) left): settle the rest with --blocks, or undo it to start over"
                    : $"files\\{c.Markers} is being merged by hand ({left:N0} conflict block(s) left): finish it, or undo it to start over"));
                continue;
            }
            if (choice != "v2")
            {
                var removed = c.AlsoDelete is { } ad && (deletes.Contains(ad) || choiceRemoved.Contains(ad)) ? new[] { ad } : [];
                done.Add(new ConflictDecision(c.Path, choice, rule, now, c.Markers, null, [], removed));
                continue;
            }
            if (NotFromV2.Contains(c.Kind))
            {
                refused.Add((c.Path, $"{c.Kind}: v2's side can't be taken in bulk — decide it by hand"));
                continue;
            }
            if (c.V2At is { } v2At)
            {
                if (c.V2Size < 0) { refused.Add((c.Path, "v2's file couldn't be read (or wasn't the file compared) when the overlay was written")); continue; }
                if (V2Changed(s, c) is { } why) { refused.Add((c.Path, why)); continue; }
                var dest = Path.Combine(files, Native(v2At));
                bool own = c.Markers is not null && TreeMerger.PathComparer.Equals(c.Markers, v2At);
                bool v1Own = c.V1At is not null && TreeMerger.PathComparer.Equals(c.V1At, v2At);
                string? clash =
                    !own && (File.Exists(dest) || Directory.Exists(dest)) || claimed.Contains(v2At) ? $"another change also writes {v2At} in files\\"
                    : holders.GetValueOrDefault(v2At) > 1 ? $"another conflict has {v2At} too"
                    : !v1Own && (File.Exists(Path.Combine(s.V1, Native(v2At))) || Directory.Exists(Path.Combine(s.V1, Native(v2At))))
                        ? $"v1 has its own {v2At}: v2's would overwrite it"
                    : FileInTheWay(files, v2At) is { } f ? $"files\\{f} is a file where {v2At} needs a folder"
                    : null;
                if (clash is not null) { refused.Add((c.Path, clash + " — decide it by hand")); continue; }
                claimed.Add(v2At);
            }
            var added = new List<string>();
            if (c.V1At is { } at && (c.V2At is null || !string.Equals(at, c.V2At, StringComparison.Ordinal))
                && (!deletes.Contains(at) || choiceAdded.Contains(at))) added.Add(at);
            var removedDeletes = c.V2At is { } v2d && (deletes.Contains(v2d) || choiceRemoved.Contains(v2d)) ? new[] { v2d } : [];
            done.Add(new ConflictDecision(c.Path, choice, rule, now, c.Markers, c.V2At, added, removedDeletes));
        }

        if (write && done.Count + restores.Count > 0)
        {
            using var held = Claim(journal, choice == "undo", done, restores);
            try
            {
                // Taken: now nothing else changes it. Check nothing did between reading it and taking it.
                RefuseApplied(s.Dir);
                if (Stamp(s.Dir) != stamp) throw new ArgumentException("another resolve changed this overlay meanwhile: run it again");
            }
            catch { held.Dispose(); File.Delete(journal); throw; }
            var dropped = Finish(s, journal, held, notes);
            refused.AddRange(dropped);
            done.RemoveAll(d => dropped.Any(x => x.Path == d.Path));
            restores.RemoveAll(h => dropped.Any(x => x.Path == h.Path));
        }
        int undecided = s.Undecided.Count() + (choice == "undo" ? done.Count + restores.Count(h => h.WasMerged) : -done.Count);
        return new ResolveResult(s.Dir, choice, rule, write, done, refused, already, notDecided, undecided, finished, restores, Notes: notes);
    }

    /// <summary>Conflict blocks settled by a side: each markers file the rule picks is written again in files\ with the
    /// blocks replaced and its size and time kept in blocks.json, so it reads as settled by blocks (merged once no block is
    /// left), and undo puts it back as written.</summary>
    private static ResolveResult RunBlocks(OverlayConflicts s, string journal, string choice, ConflictMatcher matcher, SortedSet<int>? numbers,
        bool write, bool finished, List<(string Path, string Why)> refused, List<string> notes)
    {
        var stamp = Stamp(s.Dir);
        string rule = (matcher.ToString() is { Length: > 0 } r ? r : "--all") + " --blocks " + (numbers is null ? "all" : ConflictBlocks.Ranges(numbers));
        var files = Path.Combine(s.Dir, "files");
        var picked = s.All.Where(matcher.Matches).ToList();
        int open = picked.Count(c => c.Markers is not null && !s.IsDecided(c));
        if (numbers is not null && open > 1)
            throw new ArgumentException($"block numbers are one file's: the rule picks {open:N0} files with markers — pick one, or give --blocks all");
        var plan = new List<(OverlayConflict C, string Full, long Size, long Ticks, byte[] Bytes, BlocksSettled Done)>();
        int already = 0, none = 0;
        foreach (var c in picked)
        {
            if (s.IsDecided(c))
            {
                // Block numbers aimed at a file already decided: say so, rather than settle nothing quietly.
                if (numbers is not null && open == 0 && c.Markers is not null)
                    refused.Add((c.Path, s.HandMerged(c) ? $"it is {s.HowMerged(c)} already: undo it to settle its blocks"
                                                         : "it is decided already: undo it to settle its blocks"));
                else already++;
                continue;
            }
            if (c.Markers is not { } m) { none++; continue; } // no blocks to settle: settle it whole
            if (c.Failed) { refused.Add((c.Path, "the overlay failed to write it: write the overlay again")); continue; }
            var original = Path.Combine(s.Dir, OverlayConflicts.OriginalsDir, Native(m));
            if (c.Blocks is not { } geometry || c.MarkersSize < 0 || !File.Exists(original))
            {
                refused.Add((c.Path, "this overlay doesn't record its blocks (written by an older CodeDiffer): write the overlay again"));
                continue;
            }
            var full = Path.Combine(files, Native(m));
            var fi = new FileInfo(full);
            if (!fi.Exists) { refused.Add((c.Path, $"files\\{Native(m)} is gone: undo it to put it back")); continue; }
            var was = ConflictBlocks.Read(File.ReadAllBytes(original));
            var now = ConflictBlocks.Read(File.ReadAllBytes(full));
            if (was is null || now is null || was.Encoding.WebName != now.Encoding.WebName)
            {
                refused.Add((c.Path, $"files\\{Native(m)} can't be read as the text it was written in — merge it by hand"));
                continue;
            }
            if (ConflictBlocks.HeadersOf(was.Lines, geometry) is not { } hs)
            {
                refused.Add((c.Path, "its blocks aren't where the overlay recorded them: write the overlay again"));
                continue;
            }
            var found = ConflictBlocks.Find(now.Lines, was.Lines, geometry, hs);
            var want = numbers ?? [.. found.Keys];
            if (want.Where(n => n > geometry.Count).ToList() is { Count: > 0 } past)
            {
                refused.Add((c.Path, $"it has {geometry.Count:N0} block(s), so no block {ConflictBlocks.Ranges(past)}"));
                continue;
            }
            if (want.Count == 0 || want.Where(n => !found.ContainsKey(n)).ToList() is { Count: > 0 })
            {
                var gone = want.Where(n => !found.ContainsKey(n)).ToList();
                refused.Add((c.Path, want.Count == 0
                    ? $"no block is left as written in files\\{Native(m)} (edited by hand): finish it by hand, or undo it to start over"
                    : $"block(s) {ConflictBlocks.Ranges(gone)} are not as written any more (settled, or edited by hand): pick others, or undo it to start over"));
                continue;
            }
            var lines = ConflictBlocks.Settle(now.Lines, want.Select(n => found[n]), choice);
            var bytes = (now with { Lines = lines }).Bytes();
            plan.Add((c, full, fi.Length, fi.LastWriteTimeUtc.Ticks, bytes, new BlocksSettled(c.Path, m, [.. want], ConflictBlocks.Left(lines))));
        }

        var done = plan.Select(p => p.Done).ToList();
        if (write && plan.Count > 0)
        {
            using var held = Claim(journal, false, [], []);
            try
            {
                RefuseApplied(s.Dir);
                if (Stamp(s.Dir) != stamp) throw new ArgumentException("another resolve changed this overlay meanwhile: run it again");
            }
            catch { held.Dispose(); File.Delete(journal); throw; }
            // Written beside files\ first, then moved over the markers file: a stopped run leaves each file whole.
            var temp = Path.Combine(s.Dir, TempDir);
            var writes = OverlayConflicts.ReadBlockWrites(Path.Combine(s.Dir, OverlayConflicts.BlocksName));
            foreach (var p in plan)
            {
                var fi = new FileInfo(p.Full);
                if (!fi.Exists || fi.Length != p.Size || fi.LastWriteTimeUtc.Ticks != p.Ticks)
                {
                    refused.Add((p.C.Path, $"files\\{Native(p.Done.Markers)} changed while this ran: run it again"));
                    done.Remove(p.Done);
                    continue;
                }
                Directory.CreateDirectory(temp);
                var tmp = Path.Combine(temp, Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(tmp, p.Bytes);
                Writable(p.Full);
                File.Move(tmp, p.Full, overwrite: true);
                // Recorded as settled by blocks, so it isn't called merged by hand; one edited by hand before stays so.
                if (!s.Edited.ContainsKey(p.C.Path) || s.ByBlocks.Contains(p.C.Path))
                {
                    var after = new FileInfo(p.Full);
                    writes[p.C.Path] = (after.Length, after.LastWriteTimeUtc.Ticks);
                }
                else writes.Remove(p.C.Path);
            }
            SaveBlockWrites(s.Dir, writes);
            refused.AddRange(Finish(s, journal, held, notes));
        }
        int undecided = s.Undecided.Count() - done.Count(d => d.Left == 0);
        return new ResolveResult(s.Dir, choice, rule, write, [], refused, already, none, undecided, finished, [], done, notes);
    }

    private static void RefuseApplied(string dir)
    {
        foreach (var f in new[] { OverlayApplier.AppliedName, OverlayApplier.ApplyingName })
            if (File.Exists(Path.Combine(dir, f)))
                throw new ArgumentException($"this overlay has been applied (see {f}): a choice made now would not reach that tree — " +
                                            "write the overlay again and decide before applying");
    }

    /// <summary>deletes.txt and decisions.json as they are (length and time): what a resolve changes.</summary>
    private static string Stamp(string dir)
        => string.Join("|", new[] { "deletes.txt", OverlayConflicts.DecisionsName }.Select(n => new FileInfo(Path.Combine(dir, n)))
            .Select(f => f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "-"));

    /// <summary>The first folder of <paramref name="rel"/> that <c>files\</c> holds as a file.</summary>
    private static string? FileInTheWay(string files, string rel)
    {
        var parts = rel.Split('/');
        for (int i = 1; i < parts.Length; i++)
        {
            var p = string.Join('/', parts.Take(i));
            if (File.Exists(Path.Combine(files, Native(p)))) return p;
        }
        return null;
    }

    /// <summary>Carry out a journal: its file moves and copies (each safe to do twice), then deletes.txt and
    /// decisions.json written whole, then the journal deleted. A choice of v2 whose file changed since it was made (a
    /// stopped resolve finished later) is dropped, its markers put back, and returned with why; so is one that can't be
    /// carried out as it is now (a file in the way), so a journal is always finished. Edits kept rather than deleted are
    /// added to <paramref name="notes"/>.</summary>
    private static List<(string Path, string Why)> Finish(OverlayConflicts s, string journal, FileStream held, List<string> notes)
    {
        bool undo;
        List<ConflictDecision> ds;
        List<(string Path, string Markers, string Keep)> restores = [];
        var bytes = new byte[held.Length];
        held.ReadExactly(bytes);
        using (var doc = JsonDocument.Parse(bytes))
        {
            undo = doc.RootElement.GetProperty("undo").GetBoolean();
            ds = [.. doc.RootElement.GetProperty("decisions").EnumerateArray().Select(OverlayConflicts.Decision)];
            if (doc.RootElement.TryGetProperty("restore", out var rs))
                restores = [.. rs.EnumerateArray().Select(e => (OverlayConflicts.Rel(e, "path")!, OverlayConflicts.Rel(e, "markers")!, OverlayConflicts.Rel(e, "keep")!))];
        }
        var files = Path.Combine(s.Dir, "files");
        var aside = Path.Combine(s.Dir, OverlayConflicts.SetAsideDir);
        // Copies are made here, outside files\ and set-aside\ (which hold the tree's own folders), so one stopped part
        // way is never applied.
        var temp = Path.Combine(s.Dir, TempDir);
        if (Directory.Exists(temp)) // a copy a stopped run left may still be read-only
        {
            ResultStore.DeleteContents(new DirectoryInfo(temp), keep: null);
            Directory.Delete(temp);
        }
        // Older runs copied into set-aside\.copying: only their own copies (named by a GUID) go, never a tree's .copying.
        var legacy = Path.Combine(aside, ".copying");
        if (Directory.Exists(legacy))
        {
            foreach (var f in Directory.EnumerateFiles(legacy).Where(f => Guid.TryParseExact(Path.GetFileName(f), "N", out _)).ToList())
            {
                MergeOverlay.Writable(f);
                File.Delete(f);
            }
            Prune(legacy, aside);
        }
        var byPath = s.All.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var dropped = new List<(string, string)>();
        var kept = new List<ConflictDecision>();
        string? editsKept = null; // set-aside\hand-merges\<time>: v2's copies edited by hand, kept when their choice is undone
        foreach (var d in ds)
        {
            if (!undo)
            {
                if (d.Copied is { } at)
                {
                    var c = byPath[d.Path];
                    var src = Path.Combine(s.V2, Native(at));
                    var dest = Path.Combine(files, Native(at));
                    if (!(File.Exists(dest) && TextInspector.SameBytes(dest, src)))
                    {
                        if (V2Changed(s, c) is { } why)
                        {
                            // Not taken: a copy of v2's file this choice made goes (the overlay wrote nothing at its path,
                            // or wrote the markers file there, now aside), and the markers file comes back.
                            bool own = d.SetAside is { } ms && TreeMerger.PathComparer.Equals(ms, at);
                            bool asideNow = d.SetAside is { } ma && File.Exists(Path.Combine(aside, Native(ma)));
                            if (File.Exists(dest) && (!own || asideNow))
                            {
                                Writable(dest);
                                File.Delete(dest);
                                Prune(Path.GetDirectoryName(dest)!, files);
                            }
                            if (d.SetAside is { } m0 && asideNow && !File.Exists(Path.Combine(files, Native(m0))))
                                MoveBack(aside, files, m0);
                            dropped.Add((d.Path, why + " (not taken)"));
                            continue;
                        }
                        if (d.SetAside is { } m1) SetAside(files, aside, m1);
                        Directory.CreateDirectory(temp);
                        var tmp = Path.Combine(temp, Guid.NewGuid().ToString("N"));
                        File.Copy(src, tmp, overwrite: true);
                        MergeOverlay.Writable(tmp);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        Writable(dest);
                        File.Move(tmp, dest, overwrite: true);
                    }
                    else if (d.SetAside is { } m2 && !TreeMerger.PathComparer.Equals(m2, at)) SetAside(files, aside, m2);
                }
                else if (d.SetAside is { } m) SetAside(files, aside, m);
            }
            else
            {
                bool same = d.SetAside is not null && d.Copied is not null && TreeMerger.PathComparer.Equals(d.SetAside, d.Copied);
                bool asideNow = d.SetAside is { } ma && File.Exists(Path.Combine(aside, Native(ma)));
                if (d.SetAside is { } m1 && asideNow && !same && File.Exists(Path.Combine(files, Native(m1))))
                {
                    // Checked before anything moves: the choice stays as it is, and the rest of the journal is done.
                    dropped.Add((d.Path, $"not undone: files\\{Native(m1)} holds another file now — undo the choice that put it there first"));
                    continue;
                }
                // v2's copy goes, unless it is where the markers file comes back (a run already put it back if none is
                // aside); a copy edited since it was taken is kept in set-aside\hand-merges\, never deleted.
                if (d.Copied is { } at && (!same || asideNow) && Path.Combine(files, Native(at)) is var copy && File.Exists(copy))
                {
                    if (EditedCopy(s, byPath[d.Path], copy))
                    {
                        editsKept ??= EditsKept(aside);
                        var to = Path.Combine(aside, Native(editsKept), Native(at));
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        Writable(copy);
                        File.Move(copy, to);
                        notes.Add($"{ResultStore.Escaped(d.Path)}: files\\{Native(at)} was edited after v2's file was copied in — the edit is kept in " +
                                  $"{OverlayConflicts.SetAsideDir}\\{Native(editsKept)}\\{Native(at)}");
                    }
                    else if (!same)
                    {
                        Writable(copy);
                        File.Delete(copy);
                    }
                    Prune(Path.GetDirectoryName(copy)!, files);
                }
                if (d.SetAside is { } m && asideNow) MoveBack(aside, files, m, overwrite: same);
            }
            kept.Add(d);
        }
        // Hand merges undone: the markers file as written copied out first, then the edited file set aside and the copy
        // put in its place with its size and time, so it reads as untouched again; a copy that fails leaves the edit
        // where it is. Each step is skipped once done.
        foreach (var (path, m, keep) in restores)
        {
            var c = byPath[path];
            var dest = Path.Combine(files, Native(m));
            var fi = new FileInfo(dest);
            if (fi.Exists && fi.Length == c.MarkersSize && fi.LastWriteTimeUtc.Ticks == c.MarkersTicks) continue;
            var original = Path.Combine(s.Dir, OverlayConflicts.OriginalsDir, Native(m));
            if (!File.Exists(original))
            {
                dropped.Add((path, $"not undone: originals\\{Native(m)} is gone, so files\\{Native(m)} was left as it is"));
                continue;
            }
            var to = Path.Combine(aside, Native(keep));
            if (fi.Exists && File.Exists(to))
            {
                dropped.Add((path, $"not undone: {OverlayConflicts.SetAsideDir}\\{Native(keep)} is there already, so files\\{Native(m)} was left as it is"));
                continue;
            }
            Directory.CreateDirectory(temp);
            var tmp = Path.Combine(temp, Guid.NewGuid().ToString("N"));
            File.Copy(original, tmp, overwrite: true);
            MergeOverlay.Writable(tmp);
            File.SetLastWriteTimeUtc(tmp, new DateTime(c.MarkersTicks, DateTimeKind.Utc));
            if (fi.Exists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(dest, to);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(tmp, dest);
        }
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        // A file put back as written is no longer settled by blocks.
        if (restores.Count > 0 && OverlayConflicts.ReadBlockWrites(Path.Combine(s.Dir, OverlayConflicts.BlocksName)) is { Count: > 0 } writes)
        {
            foreach (var (path, _, _) in restores) if (!dropped.Any(x => x.Item1 == path)) writes.Remove(path);
            SaveBlockWrites(s.Dir, writes);
        }

        var decisions = OverlayConflicts.ReadDecisions(Path.Combine(s.Dir, OverlayConflicts.DecisionsName));
        var deletes = ReadDeletes(s.Dir);
        // An undo leaves a delete that a choice it doesn't undo made too (two conflicts with the same v1 file).
        var undone = kept.Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        var staying = decisions.Values.Where(d => !undone.Contains(d.Path)).ToList();
        foreach (var d in kept)
        {
            foreach (var x in d.AddedDeletes)
            {
                if (!undo) deletes.Add(x);
                else if (!staying.Any(o => o.AddedDeletes.Contains(x, StringComparer.Ordinal))) deletes.Remove(x);
            }
            foreach (var x in d.RemovedDeletes)
            {
                if (!undo) deletes.Remove(x);
                else if (!staying.Any(o => o.RemovedDeletes.Contains(x, StringComparer.Ordinal))) deletes.Add(x);
            }
        }
        Replace(Path.Combine(s.Dir, "deletes.txt"), Encoding.UTF8.GetBytes(string.Concat(deletes.Select(d => ResultStore.Escaped(d) + "\n"))));

        foreach (var d in kept) { if (undo) decisions.Remove(d.Path); else decisions[d.Path] = d; }
        using (var ms = new MemoryStream())
        {
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteStartArray("decisions");
                foreach (var d in decisions.Values.OrderBy(d => d.Path, StringComparer.Ordinal)) OverlayConflicts.WriteDecision(w, d);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            Replace(Path.Combine(s.Dir, OverlayConflicts.DecisionsName), ms.ToArray());
        }
        File.Delete(journal);
        return dropped;
    }

    /// <summary>Whether v2's file copied into files\ was changed since: not v2's size and time as compared, nor v2's bytes.</summary>
    private static bool EditedCopy(OverlayConflicts s, OverlayConflict c, string copy)
    {
        var fi = new FileInfo(copy);
        if (fi.Length == c.V2Size && fi.LastWriteTimeUtc.Ticks == c.V2Ticks) return false;
        return c.V2At is not { } v2At || !TextInspector.SameBytes(copy, Path.Combine(s.V2, Native(v2At)));
    }

    /// <summary>A new folder under set-aside\hand-merges\ (relative to set-aside\) for edits an undo keeps.</summary>
    private static string EditsKept(string aside)
    {
        var at = $"{OverlayConflicts.HandMergesDir}/{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        while (Directory.Exists(Path.Combine(aside, Native(at)))) at += "x";
        return at;
    }

    /// <summary>Move the markers file aside, once: a second run finds it aside already (and whatever is in files\ now is
    /// v2's copy, not the markers). The folder it leaves empty goes.</summary>
    private static void SetAside(string files, string aside, string rel)
    {
        string from = Path.Combine(files, Native(rel)), to = Path.Combine(aside, Native(rel));
        if (File.Exists(to) || !File.Exists(from)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
        Prune(Path.GetDirectoryName(from)!, files);
    }

    private static void MoveBack(string aside, string files, string rel, bool overwrite = false)
    {
        string from = Path.Combine(aside, Native(rel)), to = Path.Combine(files, Native(rel));
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (overwrite) Writable(to);
        File.Move(from, to, overwrite);
        Prune(Path.GetDirectoryName(from)!, aside);
    }

    /// <summary>Clear the read-only flag of a file in the overlay, if it is there: an overlay written before copies were
    /// cleared holds v2's files read-only when v2's tree is.</summary>
    private static void Writable(string path)
    {
        if (File.Exists(path)) MergeOverlay.Writable(path);
    }

    /// <summary>Remove <paramref name="dir"/> and its parents while they're empty, up to (not including) <paramref name="stop"/>.</summary>
    private static void Prune(string dir, string stop)
    {
        for (; dir.Length > stop.Length && ResultStore.IsUnder(dir, stop); dir = Path.GetDirectoryName(dir)!)
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) return;
            try { Directory.Delete(dir); } catch (IOException) { return; }
        }
    }

    private static string? V2Changed(OverlayConflicts s, OverlayConflict c)
    {
        try
        {
            var fi = new FileInfo(Path.Combine(s.V2, Native(c.V2At!)));
            if (!fi.Exists) return $"v2's file {c.V2At} is gone since the overlay was written — compare again";
            if (fi.Length != c.V2Size || fi.LastWriteTimeUtc.Ticks != c.V2Ticks)
                return $"v2's file {c.V2At} changed since the overlay was written — compare again";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"v2's file {c.V2At} can't be read: {ex.Message}"; }
    }

    /// <summary>Write the journal, only if there is none, and hold it: it is also the lock, so two resolves never write
    /// at once.</summary>
    private static FileStream Claim(string journal, bool undo, IReadOnlyList<ConflictDecision> ds, IReadOnlyList<HandUndo> restores)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("what", "a resolve being written: until it is gone, apply-overlay refuses this overlay; the next resolve --write finishes it");
            w.WriteBoolean("undo", undo);
            w.WriteStartArray("decisions");
            foreach (var d in ds) OverlayConflicts.WriteDecision(w, d);
            w.WriteEndArray();
            w.WriteStartArray("restore");
            foreach (var h in restores)
            {
                w.WriteStartObject();
                w.WriteString("path", ResultStore.Escaped(h.Path));
                w.WriteString("markers", ResultStore.Escaped(h.Markers));
                w.WriteString("keep", ResultStore.Escaped(h.Keep));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        var tmp = journal + "." + Guid.NewGuid().ToString("N") + ".codediffer-tmp";
        File.WriteAllBytes(tmp, ms.ToArray());
        try { File.Move(tmp, journal, overwrite: false); }
        catch (IOException) when (File.Exists(journal))
        {
            File.Delete(tmp);
            throw new ArgumentException("another resolve is writing this overlay: wait for it, then run this one again");
        }
        return Hold(journal);
    }

    /// <summary>Open the journal so that no other resolve can while this one carries it out: a journal no one holds is
    /// one a resolve was stopped part way through.</summary>
    private static FileStream Hold(string journal)
    {
        try { return new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Delete); }
        catch (IOException)
        {
            throw new ArgumentException("another resolve is writing this overlay: wait for it, then run this one again");
        }
    }

    private static SortedSet<string> ReadDeletes(string dir)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        var file = Path.Combine(dir, "deletes.txt");
        if (File.Exists(file))
            foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                if (line.Length > 0) set.Add(ResultStore.Unescaped(line));
        return set;
    }

    /// <summary>Write a file whole: beside it, then moved over it.</summary>
    private static void Replace(string path, byte[] bytes)
    {
        var tmp = path + ".codediffer-tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>blocks.json written whole, or removed when no file is settled by blocks.</summary>
    private static void SaveBlockWrites(string dir, Dictionary<string, (long Size, long Ticks)> writes)
    {
        var file = Path.Combine(dir, OverlayConflicts.BlocksName);
        if (writes.Count > 0) Replace(file, OverlayConflicts.BlockWritesJson(writes));
        else File.Delete(file);
    }

    private static string Native(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>The bounded report (the CLI prints it).</summary>
    public static string Text(ResolveResult r, int maxList = 20)
    {
        if (r.Blocks is { } bs) return BlocksText(r, bs, maxList);
        var o = new StringBuilder();
        string verb = r.Choice switch { "v1" => "take v1", "v2" => "take v2", "regenerate" => "regenerate (v1 kept until then)", _ => "undo" };
        o.Append($"{(r.Written ? "RESOLVED" : "dry run")} · {verb} · rule {r.Rule} · overlay {r.Overlay}\n");
        if (r.FinishedStopped) o.Append("finished a resolve that was stopped part way first\n");
        string w = r.Written ? "" : "would be ";
        o.Append(r.Choice == "undo"
            ? $"{r.Done.Count + r.Restored.Count:N0} choice(s) {w}undone" + (r.NotDecided > 0 ? $" · {r.NotDecided:N0} matching not decided" : "")
              + (r.Refused.Count > 0 ? $" · {r.Refused.Count:N0} refused" : "") + "\n"
            : $"{r.Done.Count:N0} conflict(s) {w}decided" + (r.AlreadyDecided > 0 ? $" · {r.AlreadyDecided:N0} already decided (left alone; undo them first)" : "")
              + (r.Refused.Count > 0 ? $" · {r.Refused.Count:N0} refused" : "") + "\n");
        static string Undone(HandUndo h) => h.ByBlocks ? "block choices undone" : h.WasMerged ? "hand merge undone" : "hand edit undone";
        int width = r.Restored.Take(maxList).Select(h => Undone(h).Length).Append(12).Max(); // one column for both kinds of row
        foreach (var d in r.Done.Take(maxList))
        {
            var what = new List<string>();
            if (d.SetAside is not null) what.Add("markers set aside");
            if (d.Copied is { } c) what.Add($"v2's {c} copied in");
            if (d.AddedDeletes.Count > 0) what.Add($"{string.Join(", ", d.AddedDeletes)} to delete");
            if (d.RemovedDeletes.Count > 0) what.Add($"{string.Join(", ", d.RemovedDeletes)} kept");
            o.Append("  ").Append((r.Choice == "undo" ? $"{d.Choice} undone" : d.Choice).PadRight(width)).Append($" {ResultStore.Escaped(d.Path)}")
             .Append(what.Count > 0 ? $"  ({string.Join(" · ", what)})" : "").Append('\n');
        }
        if (r.Done.Count > maxList) o.Append($"  ... {r.Done.Count - maxList:N0} more\n");
        foreach (var h in r.Restored.Take(maxList))
            o.Append("  ").Append(Undone(h).PadRight(width)).Append($" {ResultStore.Escaped(h.Path)}  (markers put back")
             .Append(h.Existed ? $" · {(h.ByBlocks ? "the file as settled" : "your edit")} kept in {OverlayConflicts.SetAsideDir}\\{ResultStore.Escaped(h.Keep).Replace('/', '\\')})\n" : ")\n");
        if (r.Restored.Count > maxList) o.Append($"  ... {r.Restored.Count - maxList:N0} more\n");
        if (r.Refused.Count > 0)
        {
            o.Append("refused:\n");
            foreach (var (p, why) in r.Refused.Take(maxList)) o.Append($"  {ResultStore.Escaped(p)} — {why}\n");
            if (r.Refused.Count > maxList) o.Append($"  ... {r.Refused.Count - maxList:N0} more\n");
        }
        Notes(o, r, maxList);
        o.Append($"{r.Undecided:N0} conflict(s) {(r.Written ? "" : "would be ")}left undecided\n");
        o.Append(!r.Written ? (r.Done.Count + r.Restored.Count > 0 ? "nothing was changed — run again with --write to record it\n" : "nothing to do\n")
                 : "apply-overlay now applies these choices (codediffer conflicts lists what is left)\n");
        return o.ToString();
    }

    private static void Notes(StringBuilder o, ResolveResult r, int maxList)
    {
        if (r.Notes is not { Count: > 0 } ns) return;
        o.Append("kept:\n");
        foreach (var n in ns.Take(maxList)) o.Append($"  {n}\n");
        if (ns.Count > maxList) o.Append($"  ... {ns.Count - maxList:N0} more\n");
    }

    private static string BlocksText(ResolveResult r, IReadOnlyList<BlocksSettled> bs, int maxList)
    {
        var o = new StringBuilder();
        string verb = r.Choice switch { "v1" => "take v1", "v2" => "take v2", _ => "take both (v1's, then v2's)" };
        o.Append($"{(r.Written ? "RESOLVED" : "dry run")} · blocks · {verb} · rule {r.Rule} · overlay {r.Overlay}\n");
        if (r.FinishedStopped) o.Append("finished a resolve that was stopped part way first\n");
        string w = r.Written ? "" : "would be ";
        o.Append($"{bs.Sum(b => b.Numbers.Count):N0} block(s) in {bs.Count:N0} file(s) {w}settled · {bs.Count(b => b.Left == 0):N0} file(s) {w}merged")
         .Append(r.AlreadyDecided > 0 ? $" · {r.AlreadyDecided:N0} already decided (left alone)" : "")
         .Append(r.NotDecided > 0 ? $" · {r.NotDecided:N0} with no markers file (settle those whole)" : "")
         .Append(r.Refused.Count > 0 ? $" · {r.Refused.Count:N0} refused" : "").Append('\n');
        foreach (var b in bs.Take(maxList))
            o.Append($"  block(s) {ConflictBlocks.Ranges(b.Numbers)} → {r.Choice}  {ResultStore.Escaped(b.Path)}  (")
             .Append(b.Left == 0 ? "merged: no block left" : $"{b.Left:N0} block(s) left").Append(")\n");
        if (bs.Count > maxList) o.Append($"  ... {bs.Count - maxList:N0} more\n");
        if (r.Refused.Count > 0)
        {
            o.Append("refused:\n");
            foreach (var (p, why) in r.Refused.Take(maxList)) o.Append($"  {ResultStore.Escaped(p)} — {why}\n");
            if (r.Refused.Count > maxList) o.Append($"  ... {r.Refused.Count - maxList:N0} more\n");
        }
        Notes(o, r, maxList);
        o.Append($"{r.Undecided:N0} conflict(s) {w}left undecided\n");
        o.Append(!r.Written ? (bs.Count > 0 ? "nothing was changed — run again with --write to record it\n" : "nothing to do\n")
                 : "the files are rewritten in files\\ (undo puts one back as written; codediffer conflicts --blocks shows what is left)\n");
        return o.ToString();
    }
}
