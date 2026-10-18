using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.ThreeWay;
using CodeDiffer.Core.Walk;

namespace CodeDiffer.Core.Port;

/// <summary>What happened to one ported change region in the target.</summary>
public enum HunkOutcome
{
    /// <summary>Applied where the base had it.</summary>
    Applied,
    /// <summary>Applied, but the target has the spot at a different line (its own edits shifted it).</summary>
    Fuzzy,
    /// <summary>The target already has exactly this change.</summary>
    Already,
    /// <summary>The target changed the same (or an adjoining) region differently — not applied.</summary>
    Conflict,
}

/// <summary>One ported region. Lines are 1-based; Offset = target line − base line.</summary>
public readonly record struct PortHunk(HunkOutcome Outcome, int BaseLine, int BaseLines, int TargetLine, int TargetLines, int ChangeLines, int Offset);

/// <summary>Per-file verdict.</summary>
public enum PortStatus
{
    /// <summary>Every change applies (some maybe already present); the file is (or would be) written.</summary>
    Clean,
    /// <summary>Nothing to do: the target already matches the change.</summary>
    Already,
    /// <summary>At least one change conflicts; the file is left untouched.</summary>
    Conflict,
    /// <summary>The run was cancelled before this file: nothing was looked at or written.</summary>
    NotReached,
}

/// <summary>How far a port has got (files done of Total), for a progress line.</summary>
public sealed class PortProgress
{
    private int _done;
    public int Total { get; internal set; }
    public int Done => Volatile.Read(ref _done);
    internal void Step() => Interlocked.Increment(ref _done);
    /// <summary>Test hook: called after each file with the number done so far.</summary>
    internal Action<int>? AfterFile { get; init; }
}

/// <summary>One changed file's outcome. Action is what applying does: modify, create, delete, rename, none.</summary>
public sealed record PortFile(string Path, string? From, PortStatus Status, string Action, IReadOnlyList<PortHunk> Hunks, string? Note);

public sealed class PortResult
{
    public required string Target { get; init; }
    public required bool Written { get; init; }
    public required IReadOnlyList<PortFile> Files { get; init; }
    /// <summary>Cancelled part way: the files before the cancel are done (each whole), the rest are NotReached.</summary>
    public bool Cancelled { get; init; }

    public int Count(PortStatus s) => Files.Count(f => f.Status == s);
    public int Count(HunkOutcome o) => Files.Sum(f => f.Hunks.Count(h => h.Outcome == o));
}

/// <summary>
/// Ports a compare's left→right change set onto a third tree C (apply_changeset). Per text file this is a
/// diff3 merge with base = left, the change side = right and the target = C, on the same
/// <see cref="ThreeWayMerger"/> the 3-way contract is verified against: a region only the change side
/// touched is applied (fuzzy when C has it at a different line), one C already has is "already", and one
/// both touched differently is a conflict. A file with any conflict is never written; everything else is
/// all-or-nothing per file (atomic replace). Byte-level files (binary, EOL/encoding-only, very large, or a
/// text that would not round-trip through its encoding) apply only when C still equals the base exactly.
/// <para>
/// Interrupting it is safe: a cancel stops between files (each file is written whole, via a temp file and a
/// rename), and running the same port again finishes the job — what was already written comes out "already",
/// and a rename stopped between writing the new path and deleting the old one is completed. A temp file a killed run
/// left behind stays: a file of that name is never written over or deleted, since it could be a real one.
/// </para>
/// </summary>
public static class ChangePorter
{
    /// <summary>The suffix of the temp file a write goes through (<c>&lt;file&gt;.&lt;id&gt;.codediffer.tmp</c>).</summary>
    public const string TempSuffix = ".codediffer.tmp";

    public static PortResult Run(CompareReport report, string leftRoot, string rightRoot, string target,
        bool write, long maxTextBytes = 16L * 1024 * 1024, int parallelism = 8,
        CancellationToken ct = default, PortProgress? progress = null, bool resolveStamps = false)
    {
        target = Path.GetFullPath(target);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException($"target directory not found: {target}");
        // A directory that couldn't be listed makes every file under it look removed (or added): porting that would
        // delete real files. Refused whole, for every caller (the CLI, the MCP server).
        if (report.LeftDroppedDirectories + report.RightDroppedDirectories > 0)
            throw new ArgumentException($"the compare is incomplete ({report.LeftDroppedDirectories} left / {report.RightDroppedDirectories} right " +
                                        "director(ies) could not be listed): its adds and removes there may be listing failures — refusing to port it; compare again");
        // A held rename (too weak or tied to act on) is ported as the remove and the add it may be: a target that edited
        // the source keeps it (a conflict), never has it deleted for an unrelated file.
        var changed = report.Changes.Where(c => c.Status != ChangeStatus.Identical).SelectMany(c => c.ActedOn()).ToList();
        var files = new PortFile[changed.Count];
        if (progress is not null) progress.Total = changed.Count;
        var links = new LinkGuard(target);
        // Two changes whose target paths differ only in case (Foo.c removed, foo.c added: a case-only rename too
        // different to pair) are one file on Windows: run in parallel, the add would see the old file and the delete
        // would then remove it — leaving neither. Both are left to do by hand.
        var caseClash = changed.SelectMany((c, i) => (c.RenamedFrom is { } f ? new[] { c.RelativePath, f } : [c.RelativePath]).Select(p => (p, i)))
            .GroupBy(t => t.p, TreeMerger.PathComparer)
            .Where(g => g.Select(t => t.i).Distinct().Count() > 1 && g.Select(t => t.p).Distinct(StringComparer.Ordinal).Count() > 1)
            .SelectMany(g => g.Select(t => t.p)).ToHashSet(StringComparer.Ordinal);
        // In phases, each in parallel: what frees a path before what takes one. A directory that became a file (x/y
        // removed, x added) or a file that became one (the reverse) is a remove and an add on one path prefix; run
        // together, the add found the old file or directory still there. So removes first, then renames (each deletes
        // its old path once its new one is written), then the directories those emptied, then adds and edits.
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) };
        var deleted = new System.Collections.Concurrent.ConcurrentBag<string>();
        int Phase(FileChange c) => c.Status switch { ChangeStatus.Removed => 0, ChangeStatus.Renamed => 1, _ => 2 };
        foreach (var phase in Enumerable.Range(0, changed.Count).GroupBy(i => Phase(changed[i])).OrderBy(g => g.Key))
        {
            if (phase.Key == 2 && write) RemoveEmptied(deleted, target, links);
            Parallel.ForEach(phase, options, i => Port(i));
        }
        return new PortResult { Target = target, Written = write, Files = files, Cancelled = files.Any(f => f.Status == PortStatus.NotReached) };

        void Port(int i)
        {
            var c = changed[i];
            if (ct.IsCancellationRequested) // checked per file, never inside one: a file is done whole or not at all
            {
                files[i] = new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.NotReached, "none", [], null);
                return;
            }
            try
            {
                files[i] = c.BehindLink
                    ? new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.Conflict, "none", [], $"not ported: {c.Unreadable}")
                    : caseClash.Contains(c.RelativePath) || (c.RenamedFrom is { } rf && caseClash.Contains(rf))
                    ? new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.Conflict, "none", [],
                        $"another change's path differs from {c.RelativePath} only in case (one file on Windows) — do these by hand")
                    : LinkIn(c, target, links) is { } link
                    ? new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.Conflict, "none", [],
                        $"the target has a symlink/junction at {link} — never written or deleted through")
                    : One(c, leftRoot, rightRoot, target, write, maxTextBytes, resolveStamps, ct);
                if (c.RenameHeld is { } held) files[i] = files[i] with { Note = files[i].Note is { } n ? $"{n}; {held}" : held };
                if (write && files[i].Status == PortStatus.Clean && (c.Status == ChangeStatus.Removed || c.Status == ChangeStatus.Renamed))
                    deleted.Add(c.RenamedFrom ?? c.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files[i] = new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.Conflict, "none", [], $"I/O error: {ex.Message}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Stopped in its merge, before anything of it was written: not reached (thrown out of the loop, it would
                // reach the caller wrapped, with the files done before it unreported).
                files[i] = new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.NotReached, "none", [], null);
            }
            if (progress is not null)
            {
                progress.Step();
                progress.AfterFile?.Invoke(progress.Done);
            }
        }
    }

    /// <summary>
    /// The directories the deletes left empty, deepest first, up to (never) the target root: the change set's left side
    /// had files there and its right side has none, so the path is free for an add (a file where a directory was). Only
    /// an empty real directory goes, never one behind a link; one that still holds anything, the target's own files
    /// included, stays.
    /// </summary>
    private static void RemoveEmptied(IEnumerable<string> deleted, string target, LinkGuard links)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in deleted)
            for (var d = Path.GetDirectoryName(Full(target, rel)); d is not null && d.Length > target.Length && dirs.Add(d); d = Path.GetDirectoryName(d)) { }
        foreach (var d in dirs.OrderByDescending(d => d.Length))
            try
            {
                if (links.LinkOnTheWay(d) is null && Directory.Exists(d) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0
                    && !Directory.EnumerateFileSystemEntries(d).Any())
                    Directory.Delete(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // left as it was: an add there says why
    }

    /// <summary>A link on the way to either of the change's target paths, or null.</summary>
    private static string? LinkIn(FileChange c, string target, LinkGuard links)
        => links.LinkOnTheWay(Full(target, c.RelativePath)) ?? (c.RenamedFrom is { } f ? links.LinkOnTheWay(Full(target, f)) : null);

    private static PortFile One(FileChange c, string leftRoot, string rightRoot, string target, bool write, long maxText, bool stamps,
        CancellationToken ct)
    {
        string path = c.RelativePath;
        string fromRel = c.RenamedFrom ?? path;                 // where the base content lives (and lives in C)
        string tFrom = Full(target, fromRel), tTo = Full(target, path);

        // Whole-file checks stream (a file can be bigger than memory); only a text merge reads files whole.
        switch (c.Status)
        {
            case ChangeStatus.Added:
            {
                var src = Full(rightRoot, path);
                if (!File.Exists(tTo))
                {
                    if (write) Output.Copy(src).WriteTo(tTo);
                    return File1(c, PortStatus.Clean, "create", HunkOutcome.Applied, null);
                }
                return TextInspector.SameBytes(tTo, src)
                    ? File1(c, PortStatus.Already, "none", HunkOutcome.Already, null)
                    : File1(c, PortStatus.Conflict, "none", HunkOutcome.Conflict, "added on the right, but the target already has a different file here");
            }
            case ChangeStatus.Removed:
            {
                if (!File.Exists(tFrom)) return File1(c, PortStatus.Already, "none", HunkOutcome.Already, null);
                if (!TextInspector.SameBytes(tFrom, Full(leftRoot, path)))
                    return File1(c, PortStatus.Conflict, "none", HunkOutcome.Conflict, "removed on the right, but the target changed it");
                if (write) File.Delete(tFrom);
                return File1(c, PortStatus.Clean, "delete", HunkOutcome.Applied, null);
            }
        }

        // Modified or renamed: needs the target's copy of the base file.
        bool renamed = c.Status == ChangeStatus.Renamed;
        string aPath = Full(leftRoot, fromRel), bPath = Full(rightRoot, path);
        if (!File.Exists(tFrom))
        {
            if (renamed && File.Exists(tTo))
                return RenameDone(c, aPath, bPath, tTo, maxText, stamps, ct) is var (done, how) && done
                    ? File1(c, PortStatus.Already, "none", HunkOutcome.Already, how)
                    : File1(c, PortStatus.Conflict, "none", HunkOutcome.Conflict,
                        $"target has no {fromRel}, and its {path} is not the renamed file — check by hand");
            return File1(c, PortStatus.Conflict, "none", HunkOutcome.Conflict, $"target has no {fromRel}");
        }
        // A rename that only changes case is the same file on Windows: writing the new name and deleting the old
        // one would delete it. Left to do by hand.
        if (renamed && TreeMerger.PathComparer.Equals(tFrom, tTo))
            return File1(c, PortStatus.Conflict, "none", HunkOutcome.Conflict, $"a case-only rename ({fromRel} -> {path}) — rename it by hand");

        var (f, output) = Plan(c, aPath, bPath, tFrom, maxText, stamps, ct);
        if (f.Status != PortStatus.Clean) return f;

        if (renamed && File.Exists(tTo))
        {
            // The new path is already there. If it holds exactly what this rename writes, an earlier run was stopped
            // between writing it and deleting the old path: finish that. Anything else is someone else's file.
            if (!output!.Holds(tTo))
                return File1(c, PortStatus.Conflict, "none", HunkOutcome.Conflict, $"rename target {path} already exists in the target");
            if (write) File.Delete(tFrom);
            return f with { Note = Join(f.Note, $"finishes an interrupted rename ({path} was already written; deletes {fromRel})") };
        }
        if (write)
        {
            output!.WriteTo(tTo);
            if (renamed) File.Delete(tFrom);
        }
        return f;
    }

    /// <summary>The port of one modified/renamed file onto the target's copy <paramref name="tPath"/>: its verdict, and
    /// for a clean one what to write.</summary>
    private static (PortFile, Output?) Plan(FileChange c, string aPath, string bPath, string tPath, long maxText, bool stamps, CancellationToken ct)
    {
        long big = Math.Max(Math.Max(new FileInfo(aPath).Length, new FileInfo(bPath).Length), new FileInfo(tPath).Length);
        if (big > maxText || c.Reason is ChangeReason.Binary or ChangeReason.Eol or ChangeReason.Encoding)
            return ByteLevel(c, aPath, bPath, tPath, big > maxText ? "large file — applied only if the target equals the base" : null);
        var a = TextInspector.ReadAll(aPath);
        var b = TextInspector.ReadAll(bPath);
        var t = TextInspector.ReadAll(tPath);
        if (Text.TryRead(a, out var at) && Text.TryRead(b, out var bt) && Text.TryRead(t, out var tt))
        {
            var (f, bytes) = Merge(c, at, bt, tt, stamps, ct);
            return (f, bytes is null ? null : Output.Of(bytes));
        }
        return ByteLevel(c, aPath, bPath, tPath, "not round-trippable text (legacy encoding?) — compared as bytes");
    }

    /// <summary>
    /// A rename whose old path is gone from the target and whose new path is there: did an earlier run do it? Yes when
    /// the new path holds the right side exactly, or already has the whole change (merging it again changes nothing)
    /// including at least one of its edited regions. A rename without edits (of a file the target had edited) has no
    /// region to recognize it by: it is taken as done when the new path is still mostly the base — a judgement, so the
    /// verdict says so (an unrelated file much like the base would pass it too).
    /// </summary>
    /// <returns>Done, and the note saying how it was judged when that was by likeness.</returns>
    private static (bool Done, string? How) RenameDone(FileChange c, string aPath, string bPath, string tTo, long maxText, bool stamps,
        CancellationToken ct)
    {
        if (TextInspector.SameBytes(tTo, bPath)) return (true, null);
        var (f, _) = Plan(c with { Status = ChangeStatus.Modified, RenamedFrom = null }, aPath, bPath, tTo, maxText, stamps, ct);
        if (f.Status != PortStatus.Already) return (false, null);
        if (f.Hunks.Any(h => h.Outcome == HunkOutcome.Already && h.BaseLine > 0)) return (true, null);
        if (Math.Max(new FileInfo(aPath).Length, new FileInfo(tTo).Length) > maxText) return (false, null);
        int milli = Similarity.Milli(TextInspector.Decode(TextInspector.ReadAll(aPath)), TextInspector.Decode(TextInspector.ReadAll(tTo)));
        return milli >= 500
            ? (true, $"taken as renamed by an earlier run: {c.RenamedFrom} is gone and {c.RelativePath} is {milli / 10.0:0.#}% like it " +
                     "(not its exact bytes) — check it is that file")
            : (false, null);
    }

    private static string Join(string? a, string b) => a is null ? b : a + "; " + b;

    /// <summary>The note of a file merged by resolveStamps.</summary>
    public const string StampsResolved = "only keyword stamps collided: the target's kept where it changed them, else the change's";

    private static (PortFile, byte[]?) Merge(FileChange c, Text a, Text b, Text t, bool resolveStamps, CancellationToken ct)
    {
        // Merge on raw lines (terminators kept, so a gained/lost final newline is a real line change). If the
        // target's line-ending style differs from the base's, merge EOL-normalized and write the target's style.
        // A side with no line break has no style of its own: the target takes the change's (else the base's), the base the target's.
        string tEol = t.Eol != "none" ? t.Eol : b.Eol != "none" ? b.Eol : a.Eol, aEol = a.Eol != "none" ? a.Eol : tEol;
        // A mixed base still normalizes when the target has one style (a CRLF target took the change's LF lines raw). A
        // target still in the base's style takes the change's conversion to one style, as compare3 does: the merge is
        // written in the change's style, the target's own new lines too.
        string bEol = b.Eol != "none" ? b.Eol : aEol, outEol = tEol;
        bool normalize = aEol != tEol && tEol is not ("mixed" or "none") && aEol != "none";
        bool converts = !normalize && aEol == tEol && bEol != aEol && bEol is "LF" or "CRLF";
        if (converts) (normalize, outEol) = (true, bEol);
        var al = Lines(a.Content, normalize);
        var bl = Lines(b.Content, normalize);
        var tl = Lines(t.Content, normalize);

        var hunks = new List<PortHunk>();
        var merged = new StringBuilder(t.Content.Length + 256);
        int cursor = 0; // base position; lines between regions are identical in all three
        var regions = ThreeWayMerger.Regions(al, bl, tl, ct);
        // --resolve-stamps: lines that collide only in keyword stamps ($Id: … $) are merged on the bare stamps. Between
        // regions a side may then have changed a stamp: the target's own is kept, else the change's is applied.
        bool stamps = false;
        if (resolveStamps && regions.Exists(r => r.Kind == RegionKind.Conflict) && TreeMerger.StampsMerge(al, bl, tl, ct) is { } bare)
            (regions, stamps) = (bare, true);
        void Gap(int to, int s1, int s2)
        {
            for (; cursor < to; cursor++)
            {
                if (!stamps) merged.Append(al[cursor]);
                else if (tl[cursor + s2] != al[cursor] || bl[cursor + s1] == al[cursor]) merged.Append(tl[cursor + s2]);
                else
                {
                    merged.Append(bl[cursor + s1]);
                    hunks.Add(new PortHunk(s2 == 0 ? HunkOutcome.Applied : HunkOutcome.Fuzzy, cursor + 1, 1, cursor + s2 + 1, 1, 1, s2));
                }
            }
        }
        foreach (var r in regions)
        {
            Gap(r.BaseStart0, r.V1Start0 - r.BaseStart0, r.V2Start0 - r.BaseStart0);
            cursor = r.BaseStart0 + r.BaseLines;
            int offset = r.V2Start0 - r.BaseStart0;
            switch (r.Kind)
            {
                case RegionKind.V1Only: // the change side alone: apply it
                    for (int k = 0; k < r.V1Lines; k++) merged.Append(bl[r.V1Start0 + k]);
                    hunks.Add(new PortHunk(offset == 0 ? HunkOutcome.Applied : HunkOutcome.Fuzzy,
                        r.BaseStart0 + 1, r.BaseLines, r.V2Start0 + 1, r.V1Lines, r.V1Lines, offset));
                    break;
                case RegionKind.V2Only: // the target's own edit: keep it, not part of the change set
                    for (int k = 0; k < r.V2Lines; k++) merged.Append(tl[r.V2Start0 + k]);
                    break;
                case RegionKind.Agreed:
                    for (int k = 0; k < r.V2Lines; k++) merged.Append(tl[r.V2Start0 + k]);
                    hunks.Add(new PortHunk(HunkOutcome.Already, r.BaseStart0 + 1, r.BaseLines, r.V2Start0 + 1, r.V2Lines, r.V1Lines, offset));
                    break;
                default:
                    for (int k = 0; k < r.V2Lines; k++) merged.Append(tl[r.V2Start0 + k]);
                    hunks.Add(new PortHunk(HunkOutcome.Conflict, r.BaseStart0 + 1, r.BaseLines, r.V2Start0 + 1, r.V2Lines, r.V1Lines, offset));
                    break;
            }
        }
        Gap(al.Count, bl.Count - al.Count, tl.Count - al.Count);

        string action = c.Status == ChangeStatus.Renamed ? "rename" : "modify";
        if (hunks.Any(h => h.Outcome == HunkOutcome.Conflict))
            return (new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.Conflict, "none", hunks, null), null);
        bool reencode = a.Kind != b.Kind && t.Kind == a.Kind; // the change's re-encoding, not yet in the target
        bool nothing = hunks.All(h => h.Outcome == HunkOutcome.Already) && c.Status != ChangeStatus.Renamed && !reencode && !converts;
        if (nothing)
            return (new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.Already, "none", hunks, stamps ? StampsResolved : null), null);

        var text = merged.ToString();
        if (normalize && outEol == "CRLF") text = text.Replace("\n", "\r\n");
        // The change may re-encode the file as well as edit it (a BOM dropped, UTF-16 → UTF-8): that is part of the
        // change, carried when the target still has the base's encoding. A target that re-encoded it its own way keeps
        // its own, and says so.
        var write = t;
        string? encNote = null;
        if (a.Kind != b.Kind)
        {
            if (reencode) { write = b; encNote = $"re-encoded {a.Kind} → {b.Kind}, as the change does"; }
            else if (t.Kind != b.Kind) encNote = $"the change re-encodes it {a.Kind} → {b.Kind}, but the target is {t.Kind} — kept the target's {t.Kind}";
        }
        string? note = converts ? $"merged ignoring line endings; written with {outEol}, as the change converts them ({aEol} → {outEol})"
            : normalize ? $"merged ignoring line endings; written with the target's {tEol}" : null;
        if (encNote is not null) note = note is null ? encNote : note + "; " + encNote;
        if (stamps) note = note is null ? StampsResolved : StampsResolved + "; " + note;
        return (new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.Clean, action, hunks, note), write.Encode(text));
    }

    private static (PortFile, Output?) ByteLevel(FileChange c, string aPath, string bPath, string tPath, string? note)
    {
        if (TextInspector.SameBytes(tPath, bPath) && c.Status != ChangeStatus.Renamed)
            return (File1(c, PortStatus.Already, "none", HunkOutcome.Already, note), null);
        if (!TextInspector.SameBytes(tPath, aPath))
            return (File1(c, PortStatus.Conflict, "none", HunkOutcome.Conflict,
                (note is null ? "" : note + "; ") + "the target differs from the base, and this file can't be merged line by line"), null);
        return (File1(c, PortStatus.Clean, c.Status == ChangeStatus.Renamed ? "rename" : "replace", HunkOutcome.Applied, note), Output.Copy(bPath));
    }

    private static PortFile File1(FileChange c, PortStatus s, string action, HunkOutcome o, string? note) =>
        new(c.RelativePath, c.RenamedFrom, s, action, [new PortHunk(o, 0, 0, 0, 0, 0, 0)], note);

    /// <summary>Lines with their terminators kept (the last may have none).</summary>
    internal static List<string> Lines(string text, bool normalizeEol)
    {
        if (normalizeEol) text = text.Replace("\r\n", "\n"); // CRLF only: a lone CR is content, it stays in its line
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') { lines.Add(text[start..(i + 1)]); start = i + 1; }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>What a clean file becomes: merged bytes, or (byte-level) a copy of the right side's file.</summary>
    private sealed class Output
    {
        private byte[]? _bytes;
        private string? _from;
        public static Output Of(byte[] bytes) => new() { _bytes = bytes };
        public static Output Copy(string from) => new() { _from = from };

        public bool Holds(string path) => _bytes is not null ? TextInspector.SameBytes(path, _bytes) : TextInspector.SameBytes(path, _from!);

        /// <summary>
        /// Write via a temp file in the same directory, then replace: a reader never sees a half file. The temp file is
        /// one this write created (<see cref="Walk.TempNames.Claim"/>), never a file already there.
        /// </summary>
        public void WriteTo(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = Walk.TempNames.Claim(path, TempSuffix);
            try
            {
                if (_bytes is not null) File.WriteAllBytes(tmp, _bytes);
                else File.Copy(_from!, tmp, overwrite: true);
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Decoded text that re-encodes to its exact original bytes (so writing it back is lossless).</summary>
    internal sealed class Text
    {
        public required string Content { get; init; }
        public required Encoding Encoding { get; init; }
        public required string Eol { get; init; }

        /// <summary>The encoding as a reader would name it: "UTF-8", "UTF-8 with BOM", "UTF-16LE"…</summary>
        public string Kind => Encoding switch
        {
            UTF8Encoding => Encoding.GetPreamble().Length > 0 ? "UTF-8 with BOM" : "UTF-8",
            UnicodeEncoding => Encoding.WebName == "utf-16BE" ? "UTF-16BE" : "UTF-16LE",
            _ => Encoding.WebName == "utf-32BE" ? "UTF-32BE" : "UTF-32LE",
        };

        public byte[] Encode(string s)
        {
            var pre = Encoding.GetPreamble();
            var body = Encoding.GetBytes(s);
            if (pre.Length == 0) return body;
            var all = new byte[pre.Length + body.Length];
            pre.CopyTo(all, 0);
            body.CopyTo(all, pre.Length);
            return all;
        }

        public static bool TryRead(byte[] bytes, out Text text)
        {
            var enc = bytes switch
            {
                [0xEF, 0xBB, 0xBF, ..] => new UTF8Encoding(true, true),
                [0xFF, 0xFE, 0x00, 0x00, ..] => new UTF32Encoding(false, true, true),
                [0x00, 0x00, 0xFE, 0xFF, ..] => new UTF32Encoding(true, true, true),
                [0xFF, 0xFE, ..] => new UnicodeEncoding(false, true, true),
                [0xFE, 0xFF, ..] => new UnicodeEncoding(true, true, true),
                _ => (Encoding)new UTF8Encoding(false, true),
            };
            text = null!;
            string s;
            try { s = enc.GetString(bytes, enc.GetPreamble().Length, bytes.Length - enc.GetPreamble().Length); }
            catch (DecoderFallbackException) { return false; }
            text = new Text { Content = s, Encoding = enc, Eol = EolOf(s) };
            return text.Encode(s).AsSpan().SequenceEqual(bytes);
        }

        private static string EolOf(string s)
        {
            int crlf = 0, lf = 0;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\n') { if (i > 0 && s[i - 1] == '\r') crlf++; else lf++; }
            return crlf > 0 && lf > 0 ? "mixed" : crlf > 0 ? "CRLF" : lf > 0 ? "LF" : "none"; // none: no line break at all
        }
    }
}
