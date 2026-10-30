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
/// to write — not a choice to make here; write the overlay again.
/// </summary>
public sealed record OverlayConflict(string Path, string Kind, string? Note, string? Markers, string? AlsoDelete,
    string? V1At, string? V2At, long V2Size, long V2Ticks, bool Failed);

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
            all.Add(new OverlayConflict(P(c, "path")!, c.GetProperty("kind").GetString()!, c.GetProperty("note").GetString(),
                P(c, "markers"), P(c, "alsoDelete"), P(c, "v1"), P(c, "v2"), c.GetProperty("v2Size").GetInt64(),
                c.GetProperty("v2Ticks").GetInt64(), c.TryGetProperty("failed", out var f) && f.GetBoolean()));
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

    internal static Dictionary<string, ConflictDecision> ReadDecisions(string file)
    {
        var d = new Dictionary<string, ConflictDecision>(StringComparer.Ordinal);
        if (!File.Exists(file)) return d;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        foreach (var e in doc.RootElement.GetProperty("decisions").EnumerateArray()) d[Decision(e).Path] = Decision(e);
        return d;
    }

    internal static ConflictDecision Decision(JsonElement e)
        => new(P(e, "path")!, e.GetProperty("choice").GetString()!, e.GetProperty("rule").GetString()!,
            e.GetProperty("at").GetDateTime(), P(e, "setAside"), P(e, "copied"),
            [.. e.GetProperty("addedDeletes").EnumerateArray().Select(x => ResultStore.Unescaped(x.GetString()!))],
            [.. e.GetProperty("removedDeletes").EnumerateArray().Select(x => ResultStore.Unescaped(x.GetString()!))]);

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
/// it matches a file or folder name at any depth (<c>*.h</c>, <c>generated</c>); with one it is anchored at the tree's
/// top and matches that path or anything under it (<c>src/gen</c>, <c>src/**/regs_*.h</c>). A conflict matches by any
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
            var p = raw.Replace('\\', '/').Trim('/');
            if (p.Length == 0) continue;
            bool name = !p.Contains('/');
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
/// Dry run unless <c>write</c>. A conflict already decided is left alone (undo it first). A path collision or a
/// file/directory clash can't be taken from v2 here (another change lands on the same path): decide it by hand. While it
/// writes, the overlay holds <c>RESOLVING.json</c> (what it is doing), and apply-overlay refuses it; a resolve that was
/// stopped is finished by the next one. An overlay already applied (or being applied) is refused: a choice made now
/// would not reach that tree.
/// </summary>
public static class ConflictResolver
{
    public static readonly string[] Choices = ["v1", "v2", "regenerate", "undo"];

    public static ResolveResult Run(string overlay, string choice, IReadOnlyList<string> patterns, IReadOnlyList<string> kinds, bool write)
    {
        if (!Choices.Contains(choice)) throw new ArgumentException($"the choice is v1, v2, regenerate or undo, not \"{choice}\"");
        var s = OverlayConflicts.Load(overlay);
        foreach (var f in new[] { OverlayApplier.AppliedName, OverlayApplier.ApplyingName })
            if (File.Exists(Path.Combine(s.Dir, f)))
                throw new ArgumentException($"this overlay has been applied (see {f}): a choice made now would not reach that tree — " +
                                            "write the overlay again and decide before applying");
        var journal = Path.Combine(s.Dir, OverlayConflicts.ResolvingName);
        bool finished = false;
        if (File.Exists(journal))
        {
            if (!write) throw new ArgumentException($"a resolve was stopped part way ({OverlayConflicts.ResolvingName}): run a resolve with --write to finish it first");
            Finish(s, journal);
            finished = true;
            s = OverlayConflicts.Load(s.Dir);
        }

        var matcher = new ConflictMatcher(patterns, kinds);
        string rule = matcher.ToString() is { Length: > 0 } r ? r : "--all";
        var deletes = ReadDeletes(s.Dir);
        var files = Path.Combine(s.Dir, "files");
        var done = new List<ConflictDecision>();
        var refused = new List<(string, string)>();
        int already = 0, notDecided = 0;
        var now = DateTime.UtcNow;
        var claimed = new HashSet<string>(TreeMerger.PathComparer); // files\ paths this run copies into
        foreach (var c in s.All.Where(matcher.Matches))
        {
            bool isDecided = s.Decisions.TryGetValue(c.Path, out var was);
            if (choice == "undo")
            {
                if (isDecided) done.Add(was!);
                else notDecided++;
                continue;
            }
            if (isDecided) { already++; continue; }
            if (c.Failed) { refused.Add((c.Path, "the overlay failed to write it: write the overlay again")); continue; }
            if (choice != "v2")
            {
                var removed = c.AlsoDelete is { } ad && deletes.Contains(ad) ? new[] { ad } : [];
                done.Add(new ConflictDecision(c.Path, choice, rule, now, c.Markers, null, [], removed));
                continue;
            }
            if (c.Kind is "path collision" or "file/directory clash")
            {
                refused.Add((c.Path, $"{c.Kind}: another change lands on the same path — decide it by hand"));
                continue;
            }
            if (c.V2At is not null)
            {
                if (c.V2Size < 0) { refused.Add((c.Path, "v2's file couldn't be read when the overlay was written")); continue; }
                if (V2Changed(s, c) is { } why) { refused.Add((c.Path, why)); continue; }
                var dest = Path.Combine(files, Native(c.V2At));
                bool own = c.Markers is not null && TreeMerger.PathComparer.Equals(c.Markers, c.V2At);
                if (!own && (File.Exists(dest) || Directory.Exists(dest)) || !claimed.Add(c.V2At))
                {
                    refused.Add((c.Path, $"another change also writes {c.V2At} in files\\ — decide it by hand"));
                    continue;
                }
            }
            var added = new List<string>();
            if (c.V1At is { } at && (c.V2At is null || !string.Equals(at, c.V2At, StringComparison.Ordinal)) && !deletes.Contains(at)) added.Add(at);
            var removedDeletes = c.V2At is { } v2At && deletes.Contains(v2At) ? new[] { v2At } : [];
            done.Add(new ConflictDecision(c.Path, choice, rule, now, c.Markers, c.V2At, added, removedDeletes));
        }

        int undecided = s.All.Count - s.Decisions.Count + (choice == "undo" ? done.Count : -done.Count);
        if (write && done.Count > 0)
        {
            WriteJournal(journal, choice == "undo", done);
            Finish(s, journal);
        }
        return new ResolveResult(s.Dir, choice, rule, write, done, refused, already, notDecided, undecided, finished);
    }

    /// <summary>Carry out a journal: its file moves and copies (each safe to do twice), then deletes.txt and
    /// decisions.json written whole, then the journal deleted.</summary>
    private static void Finish(OverlayConflicts s, string journal)
    {
        bool undo;
        List<ConflictDecision> ds;
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(journal)))
        {
            undo = doc.RootElement.GetProperty("undo").GetBoolean();
            ds = [.. doc.RootElement.GetProperty("decisions").EnumerateArray().Select(OverlayConflicts.Decision)];
        }
        var files = Path.Combine(s.Dir, "files");
        var aside = Path.Combine(s.Dir, OverlayConflicts.SetAsideDir);
        var byPath = s.All.ToDictionary(c => c.Path, StringComparer.Ordinal);
        foreach (var d in ds)
        {
            if (!undo)
            {
                if (d.SetAside is { } m) SetAside(Path.Combine(files, Native(m)), Path.Combine(aside, Native(m)));
                if (d.Copied is { } at)
                {
                    var c = byPath[d.Path];
                    var src = Path.Combine(s.V2, Native(at));
                    var dest = Path.Combine(files, Native(at));
                    if (File.Exists(dest) && TextInspector.SameBytes(dest, src)) continue;
                    if (V2Changed(s, c) is { } why) throw new IOException($"{d.Path}: {why}");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    var tmp = Walk.TempNames.Claim(dest, ChangePorter.TempSuffix);
                    try
                    {
                        File.Copy(src, tmp, overwrite: true);
                        File.Move(tmp, dest, overwrite: true);
                    }
                    catch { try { File.Delete(tmp); } catch (Exception x) when (x is IOException or UnauthorizedAccessException) { } throw; }
                }
            }
            else
            {
                var setAside = d.SetAside is { } m ? Path.Combine(aside, Native(m)) : null;
                bool same = d.SetAside is not null && d.Copied is not null && TreeMerger.PathComparer.Equals(d.SetAside, d.Copied);
                // v2's copy goes, unless it is where the markers file comes back (a run already put it back if none is aside).
                if (d.Copied is { } at && !same) File.Delete(Path.Combine(files, Native(at)));
                if (setAside is not null && File.Exists(setAside))
                {
                    var back = Path.Combine(files, Native(d.SetAside!));
                    Directory.CreateDirectory(Path.GetDirectoryName(back)!);
                    File.Move(setAside, back, overwrite: same);
                }
            }
        }

        var deletes = ReadDeletes(s.Dir);
        foreach (var d in ds)
        {
            foreach (var x in d.AddedDeletes) { if (undo) deletes.Remove(x); else deletes.Add(x); }
            foreach (var x in d.RemovedDeletes) { if (undo) deletes.Add(x); else deletes.Remove(x); }
        }
        Replace(Path.Combine(s.Dir, "deletes.txt"), Encoding.UTF8.GetBytes(string.Concat(deletes.Select(d => ResultStore.Escaped(d) + "\n"))));

        var decisions = OverlayConflicts.ReadDecisions(Path.Combine(s.Dir, OverlayConflicts.DecisionsName));
        foreach (var d in ds) { if (undo) decisions.Remove(d.Path); else decisions[d.Path] = d; }
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
    }

    /// <summary>Move the markers file aside, once: a second run finds it aside already (and whatever is in files\ now is
    /// v2's copy, not the markers).</summary>
    private static void SetAside(string from, string to)
    {
        if (File.Exists(to) || !File.Exists(from)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
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

    private static void WriteJournal(string journal, bool undo, IReadOnlyList<ConflictDecision> ds)
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
        Replace(journal, ms.ToArray());
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
