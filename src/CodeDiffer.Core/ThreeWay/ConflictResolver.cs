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
/// </summary>
public sealed record OverlayConflict(string Path, string Kind, string? Note, string? Markers, string? AlsoDelete,
    string? V1At, string? V2At, long V2Size, long V2Ticks, bool Failed, long MarkersSize = -1, long MarkersTicks = 0);

/// <summary>A choice recorded for one conflict, and what it did to the overlay (so it can be undone): the markers file
/// moved to <c>set-aside\</c>, v2's file copied into <c>files\</c>, and the lines it added to and removed from
/// <c>deletes.txt</c>.</summary>
public sealed record ConflictDecision(string Path, string Choice, string Rule, DateTime AtUtc, string? SetAside, string? Copied,
    IReadOnlyList<string> AddedDeletes, IReadOnlyList<string> RemovedDeletes);

/// <summary>A merge overlay's conflicts and the choices recorded for them.</summary>
public sealed class OverlayConflicts
{
    public const string FileName = "conflicts.json", DecisionsName = "decisions.json", ResolvingName = "RESOLVING.json";
    public const string SetAsideDir = "set-aside";

    public required string Dir { get; init; }
    public required string Base { get; init; }
    public required string V1 { get; init; }
    public required string V2 { get; init; }
    /// <summary>Sorted by path (ordinal).</summary>
    public required IReadOnlyList<OverlayConflict> All { get; init; }
    /// <summary>By conflict path.</summary>
    public required IReadOnlyDictionary<string, ConflictDecision> Decisions { get; init; }

    public IEnumerable<OverlayConflict> Undecided => All.Where(c => !Decisions.ContainsKey(c.Path));

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
                c.TryGetProperty("markersSize", out var ms) ? ms.GetInt64() : -1, c.TryGetProperty("markersTicks", out var mt) ? mt.GetInt64() : 0));
        return new OverlayConflicts
        {
            Dir = overlay,
            Base = root.GetProperty("base").GetString()!,
            V1 = root.GetProperty("v1").GetString()!,
            V2 = root.GetProperty("v2").GetString()!,
            All = all,
            Decisions = ReadDecisions(System.IO.Path.Combine(overlay, DecisionsName)),
        };
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
        var open = picked.Where(c => !Decisions.ContainsKey(c.Path)).ToList();
        var decided = picked.Where(c => Decisions.ContainsKey(c.Path)).ToList();
        string scope = matcher.Any ? $" matching {matcher}" : "";
        o.Append($"{picked.Count:N0} conflict(s){scope} in {Dir} · {open.Count:N0} undecided · {decided.Count:N0} decided");
        if (decided.Count > 0)
            o.Append(" (" + string.Join(" · ", decided.GroupBy(c => Decisions[c.Path].Choice).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Count():N0} {g.Key}")) + ")");
        o.Append('\n');
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
                o.Append($"  {c.Kind,-14} {ResultStore.Escaped(c.Path)}").Append(c.Note is { } n ? $"  — {n}" : "").Append('\n');
            if (open.Count > max) o.Append($"  ... {open.Count - max:N0} more (--max N)\n");
            if (decided.Count > 0) o.Append("decided:\n");
            foreach (var c in decided.Take(max))
                o.Append($"  {Decisions[c.Path].Choice,-10} {ResultStore.Escaped(c.Path)}  (rule: {Decisions[c.Path].Rule})\n");
            if (decided.Count > max) o.Append($"  ... {decided.Count - max:N0} more (--max N)\n");
        }
        if (open.Any(c => c.Failed))
            o.Append($"{open.Count(c => c.Failed):N0} \"not written\" are files the overlay failed to write: write the overlay again for them\n");
        o.Append(open.Count == 0
            ? "nothing left to decide here: apply it with codediffer apply-overlay (text conflicts left in files\\ keep their markers)\n"
            : $"settle a group: codediffer resolve \"{Dir}\" v1|v2|regenerate <pattern>... [--kind K]   (dry run; --write records it)\n");
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
    IReadOnlyList<(string Path, string Why)> Refused, int AlreadyDecided, int NotDecided, int Undecided, bool FinishedStopped);

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
/// Dry run unless <c>write</c>. A conflict already decided is left alone (undo it first), and so is one whose markers
/// file was edited since the overlay was written (merged by hand). v2 is never taken where its file would land on
/// another file: one v1 has of its own, another conflict's, or another write in <c>files\</c>; nor for a kind whose
/// sides aren't known well enough (<see cref="NotFromV2"/>): decide those by hand. While it writes, the overlay holds
/// <c>RESOLVING.json</c> (what it is doing; created only if absent, so two resolves never write at once), and
/// apply-overlay refuses it; a resolve that was stopped is finished by the next one, which drops (and reports) a choice
/// of v2 whose file changed in between. An overlay already applied (or being applied) is refused: a choice made now
/// would not reach that tree.
/// </summary>
public static class ConflictResolver
{
    public static readonly string[] Choices = ["v1", "v2", "regenerate", "undo"];

    /// <summary>Kinds v2 is never taken for: v2's path is where another change lands or a directory, or a side is a link
    /// not followed, a directory not listed, a name Windows can't open, or a file that couldn't be read.</summary>
    public static readonly IReadOnlySet<string> NotFromV2 = new HashSet<string>(StringComparer.Ordinal)
        { "path collision", "file/directory clash", "unreadable", "behind a link", "unlisted directory", "unopenable name" };

    public static ResolveResult Run(string overlay, string choice, IReadOnlyList<string> patterns, IReadOnlyList<string> kinds, bool write)
    {
        if (!Choices.Contains(choice)) throw new ArgumentException($"the choice is v1, v2, regenerate or undo, not \"{choice}\"");
        var matcher = new ConflictMatcher(patterns, kinds);
        var s = OverlayConflicts.Load(overlay);
        RefuseApplied(s.Dir);
        var journal = Path.Combine(s.Dir, OverlayConflicts.ResolvingName);
        bool finished = false;
        var refused = new List<(string, string)>();
        if (File.Exists(journal))
        {
            if (!write) throw new ArgumentException($"a resolve was stopped part way ({OverlayConflicts.ResolvingName}): run a resolve with --write to finish it first");
            using (var held = Hold(journal)) refused.AddRange(Finish(s, journal, held));
            finished = true;
            s = OverlayConflicts.Load(s.Dir);
        }
        var stamp = Stamp(s.Dir);

        string rule = matcher.ToString() is { Length: > 0 } r ? r : "--all";
        var deletes = ReadDeletes(s.Dir);
        var files = Path.Combine(s.Dir, "files");
        var aside = Path.Combine(s.Dir, OverlayConflicts.SetAsideDir);
        var done = new List<ConflictDecision>();
        int already = 0, notDecided = 0;
        var now = DateTime.UtcNow;
        var claimed = new HashSet<string>(TreeMerger.PathComparer); // files\ paths this run copies into
        // How many conflicts have each path (on a side, or as their markers): v2's file never lands on another's.
        var holders = new Dictionary<string, int>(TreeMerger.PathComparer);
        foreach (var c in s.All)
            foreach (var p in new[] { c.V1At, c.V2At, c.Markers }.OfType<string>().Distinct(TreeMerger.PathComparer))
                holders[p] = holders.GetValueOrDefault(p) + 1;
        foreach (var c in s.All.Where(matcher.Matches))
        {
            bool isDecided = s.Decisions.TryGetValue(c.Path, out var was);
            if (choice == "undo")
            {
                if (!isDecided) { notDecided++; continue; }
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
            if (isDecided) { already++; continue; }
            if (c.Failed) { refused.Add((c.Path, "the overlay failed to write it: write the overlay again")); continue; }
            if (MarkersEdited(files, c) is { } edited) { refused.Add((c.Path, edited)); continue; }
            if (choice != "v2")
            {
                var removed = c.AlsoDelete is { } ad && deletes.Contains(ad) ? new[] { ad } : [];
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
            if (c.V1At is { } at && (c.V2At is null || !string.Equals(at, c.V2At, StringComparison.Ordinal)) && !deletes.Contains(at)) added.Add(at);
            var removedDeletes = c.V2At is { } v2d && deletes.Contains(v2d) ? new[] { v2d } : [];
            done.Add(new ConflictDecision(c.Path, choice, rule, now, c.Markers, c.V2At, added, removedDeletes));
        }

        if (write && done.Count > 0)
        {
            using var held = Claim(journal, choice == "undo", done);
            try
            {
                // Taken: now nothing else changes it. Check nothing did between reading it and taking it.
                RefuseApplied(s.Dir);
                if (Stamp(s.Dir) != stamp) throw new ArgumentException("another resolve changed this overlay meanwhile: run it again");
            }
            catch { held.Dispose(); File.Delete(journal); throw; }
            var dropped = Finish(s, journal, held);
            refused.AddRange(dropped);
            done.RemoveAll(d => dropped.Any(x => x.Path == d.Path));
        }
        int undecided = s.All.Count - s.Decisions.Count + (choice == "undo" ? done.Count : -done.Count);
        return new ResolveResult(s.Dir, choice, rule, write, done, refused, already, notDecided, undecided, finished);
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

    /// <summary>Why a markers file mustn't be set aside: it was edited since the overlay wrote it (merged by hand).</summary>
    private static string? MarkersEdited(string files, OverlayConflict c)
    {
        if (c.Markers is null || c.MarkersSize < 0) return null;
        var fi = new FileInfo(Path.Combine(files, Native(c.Markers)));
        if (!fi.Exists || fi.Length == c.MarkersSize && fi.LastWriteTimeUtc.Ticks == c.MarkersTicks) return null;
        return $"files\\{c.Markers} was edited since the overlay was written (merged by hand?): it stays — delete it first to choose a side anyway";
    }

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
    /// stopped resolve finished later) is dropped, its markers put back, and returned with why.</summary>
    private static List<(string Path, string Why)> Finish(OverlayConflicts s, string journal, FileStream held)
    {
        bool undo;
        List<ConflictDecision> ds;
        var bytes = new byte[held.Length];
        held.ReadExactly(bytes);
        using (var doc = JsonDocument.Parse(bytes))
        {
            undo = doc.RootElement.GetProperty("undo").GetBoolean();
            ds = [.. doc.RootElement.GetProperty("decisions").EnumerateArray().Select(OverlayConflicts.Decision)];
        }
        var files = Path.Combine(s.Dir, "files");
        var aside = Path.Combine(s.Dir, OverlayConflicts.SetAsideDir);
        // Copies are made here, outside files\, so one stopped part way is never applied.
        var temp = Path.Combine(aside, ".copying");
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        var byPath = s.All.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var dropped = new List<(string, string)>();
        var kept = new List<ConflictDecision>();
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
                            if (d.SetAside is { } m0 && File.Exists(Path.Combine(aside, Native(m0))) && !File.Exists(Path.Combine(files, Native(m0))))
                                MoveBack(aside, files, m0);
                            dropped.Add((d.Path, why + " (not taken)"));
                            continue;
                        }
                        if (d.SetAside is { } m1) SetAside(files, aside, m1);
                        Directory.CreateDirectory(temp);
                        var tmp = Path.Combine(temp, Guid.NewGuid().ToString("N"));
                        File.Copy(src, tmp, overwrite: true);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Move(tmp, dest, overwrite: true);
                    }
                    else if (d.SetAside is { } m2 && !TreeMerger.PathComparer.Equals(m2, at)) SetAside(files, aside, m2);
                }
                else if (d.SetAside is { } m) SetAside(files, aside, m);
            }
            else
            {
                bool same = d.SetAside is not null && d.Copied is not null && TreeMerger.PathComparer.Equals(d.SetAside, d.Copied);
                // v2's copy goes, unless it is where the markers file comes back (a run already put it back if none is aside).
                if (d.Copied is { } at && !same)
                {
                    File.Delete(Path.Combine(files, Native(at)));
                    Prune(Path.GetDirectoryName(Path.Combine(files, Native(at)))!, files);
                }
                if (d.SetAside is { } m && File.Exists(Path.Combine(aside, Native(m))))
                {
                    if (!same && File.Exists(Path.Combine(files, Native(m))))
                        throw new IOException($"files\\{m} holds another file now — undo the choice that put it there, then run a resolve with --write again");
                    MoveBack(aside, files, m, overwrite: same);
                }
            }
            kept.Add(d);
        }
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);

        var deletes = ReadDeletes(s.Dir);
        foreach (var d in kept)
        {
            foreach (var x in d.AddedDeletes) { if (undo) deletes.Remove(x); else deletes.Add(x); }
            foreach (var x in d.RemovedDeletes) { if (undo) deletes.Add(x); else deletes.Remove(x); }
        }
        Replace(Path.Combine(s.Dir, "deletes.txt"), Encoding.UTF8.GetBytes(string.Concat(deletes.Select(d => ResultStore.Escaped(d) + "\n"))));

        var decisions = OverlayConflicts.ReadDecisions(Path.Combine(s.Dir, OverlayConflicts.DecisionsName));
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
        File.Move(from, to, overwrite);
        Prune(Path.GetDirectoryName(from)!, aside);
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
    private static FileStream Claim(string journal, bool undo, IReadOnlyList<ConflictDecision> ds)
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

    private static string Native(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>The bounded report (the CLI prints it).</summary>
    public static string Text(ResolveResult r, int maxList = 20)
    {
        var o = new StringBuilder();
        string verb = r.Choice switch { "v1" => "take v1", "v2" => "take v2", "regenerate" => "regenerate (v1 kept until then)", _ => "undo" };
        o.Append($"{(r.Written ? "RESOLVED" : "dry run")} · {verb} · rule {r.Rule} · overlay {r.Overlay}\n");
        if (r.FinishedStopped) o.Append("finished a resolve that was stopped part way first\n");
        string w = r.Written ? "" : "would be ";
        o.Append(r.Choice == "undo"
            ? $"{r.Done.Count:N0} choice(s) {w}undone" + (r.NotDecided > 0 ? $" · {r.NotDecided:N0} matching not decided" : "") + "\n"
            : $"{r.Done.Count:N0} conflict(s) {w}decided" + (r.AlreadyDecided > 0 ? $" · {r.AlreadyDecided:N0} already decided (left alone; undo them first)" : "")
              + (r.Refused.Count > 0 ? $" · {r.Refused.Count:N0} refused" : "") + "\n");
        foreach (var d in r.Done.Take(maxList))
        {
            var what = new List<string>();
            if (d.SetAside is not null) what.Add("markers set aside");
            if (d.Copied is { } c) what.Add($"v2's {c} copied in");
            if (d.AddedDeletes.Count > 0) what.Add($"{string.Join(", ", d.AddedDeletes)} to delete");
            if (d.RemovedDeletes.Count > 0) what.Add($"{string.Join(", ", d.RemovedDeletes)} kept");
            o.Append($"  {(r.Choice == "undo" ? $"{d.Choice} undone" : d.Choice),-12} {ResultStore.Escaped(d.Path)}")
             .Append(what.Count > 0 ? $"  ({string.Join(" · ", what)})" : "").Append('\n');
        }
        if (r.Done.Count > maxList) o.Append($"  ... {r.Done.Count - maxList:N0} more\n");
        if (r.Refused.Count > 0)
        {
            o.Append("refused:\n");
            foreach (var (p, why) in r.Refused.Take(maxList)) o.Append($"  {ResultStore.Escaped(p)} — {why}\n");
            if (r.Refused.Count > maxList) o.Append($"  ... {r.Refused.Count - maxList:N0} more\n");
        }
        o.Append($"{r.Undecided:N0} conflict(s) {(r.Written ? "" : "would be ")}left undecided\n");
        o.Append(!r.Written ? (r.Done.Count > 0 ? "nothing was changed — run again with --write to record it\n" : "nothing to do\n")
                 : "apply-overlay now applies these choices (codediffer conflicts lists what is left)\n");
        return o.ToString();
    }
}
