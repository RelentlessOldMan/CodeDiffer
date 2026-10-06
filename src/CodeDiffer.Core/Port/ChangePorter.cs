using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Model;

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
}

/// <summary>One changed file's outcome. Action is what applying does: modify, create, delete, rename, none.</summary>
public sealed record PortFile(string Path, string? From, PortStatus Status, string Action, IReadOnlyList<PortHunk> Hunks, string? Note);

public sealed class PortResult
{
    public required string Target { get; init; }
    public required bool Written { get; init; }
    public required IReadOnlyList<PortFile> Files { get; init; }

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
/// </summary>
public static class ChangePorter
{
    public static PortResult Run(CompareReport report, string leftRoot, string rightRoot, string target,
        bool write, long maxTextBytes = 16L * 1024 * 1024, int parallelism = 8)
    {
        target = Path.GetFullPath(target);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException($"target directory not found: {target}");
        var changed = report.Changes.Where(c => c.Status != ChangeStatus.Identical).ToList();
        var files = new PortFile[changed.Count];
        Parallel.For(0, changed.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) }, i =>
        {
            var c = changed[i];
            try { files[i] = One(c, leftRoot, rightRoot, target, write, maxTextBytes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files[i] = new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.Conflict, "none", [], $"I/O error: {ex.Message}");
            }
        });
        return new PortResult { Target = target, Written = write, Files = files };
    }

    private static PortFile One(FileChange c, string leftRoot, string rightRoot, string target, bool write, long maxText)
    {
        string path = c.RelativePath;
        string fromRel = c.RenamedFrom ?? path;                 // where the base content lives (and lives in C)
        string tFrom = Full(target, fromRel), tTo = Full(target, path);

        switch (c.Status)
        {
            case ChangeStatus.Added:
            {
                var b = File.ReadAllBytes(Full(rightRoot, path));
                if (!File.Exists(tTo))
                {
                    if (write) WriteAtomic(tTo, b);
                    return File1(c, PortStatus.Clean, "create", HunkOutcome.Applied, null);
                }
                return SameBytes(tTo, b)
                    ? File1(c, PortStatus.Already, "none", HunkOutcome.Already, null)
                    : File1(c, PortStatus.Conflict, "none", HunkOutcome.Conflict, "added on the right, but the target already has a different file here");
            }
            case ChangeStatus.Removed:
            {
                if (!File.Exists(tFrom)) return File1(c, PortStatus.Already, "none", HunkOutcome.Already, null);
                if (!SameBytes(tFrom, File.ReadAllBytes(Full(leftRoot, path))))
                    return File1(c, PortStatus.Conflict, "none", HunkOutcome.Conflict, "removed on the right, but the target changed it");
                if (write) File.Delete(tFrom);
                return File1(c, PortStatus.Clean, "delete", HunkOutcome.Applied, null);
            }
        }

        // Modified or renamed: needs the target's copy of the base file.
        bool renamed = c.Status == ChangeStatus.Renamed;
        if (!File.Exists(tFrom))
        {
            if (renamed && File.Exists(tTo) && SameBytes(tTo, File.ReadAllBytes(Full(rightRoot, path))))
                return File1(c, PortStatus.Already, "none", HunkOutcome.Already, null);
            return File1(c, PortStatus.Conflict, "none", HunkOutcome.Conflict, $"target has no {fromRel}");
        }
        if (renamed && File.Exists(tTo))
            return File1(c, PortStatus.Conflict, "none", HunkOutcome.Conflict, $"rename target {path} already exists in the target");

        string aPath = Full(leftRoot, fromRel), bPath = Full(rightRoot, path);
        long big = Math.Max(Math.Max(new FileInfo(aPath).Length, new FileInfo(bPath).Length), new FileInfo(tFrom).Length);
        bool byteLevel = big > maxText || c.Reason is ChangeReason.Binary or ChangeReason.Eol or ChangeReason.Encoding;

        if (!byteLevel)
        {
            var a = File.ReadAllBytes(aPath);
            var b = File.ReadAllBytes(bPath);
            var t = File.ReadAllBytes(tFrom);
            if (Text.TryRead(a, out var at) && Text.TryRead(b, out var bt) && Text.TryRead(t, out var tt))
                return Merge(c, at, bt, tt, tFrom, tTo, write);
            return ByteLevel(c, a, b, t, tFrom, tTo, write, "not round-trippable text (legacy encoding?) — compared as bytes");
        }
        return ByteLevel(c, File.ReadAllBytes(aPath), File.ReadAllBytes(bPath), File.ReadAllBytes(tFrom), tFrom, tTo, write,
            big > maxText ? "large file — applied only if the target equals the base" : null);
    }

    private static PortFile Merge(FileChange c, Text a, Text b, Text t, string tFrom, string tTo, bool write)
    {
        // Merge on raw lines (terminators kept, so a gained/lost final newline is a real line change). If the
        // target's line-ending style differs from the base's, merge EOL-normalized and write the target's style.
        bool normalize = a.Eol != t.Eol && t.Eol != "mixed" && a.Eol != "mixed";
        var al = Lines(a.Content, normalize);
        var bl = Lines(b.Content, normalize);
        var tl = Lines(t.Content, normalize);

        var hunks = new List<PortHunk>();
        var merged = new StringBuilder(t.Content.Length + 256);
        int cursor = 0; // base position; lines between regions are identical in all three
        foreach (var r in ThreeWayMerger.Regions(al, bl, tl))
        {
            for (; cursor < r.BaseStart0; cursor++) merged.Append(al[cursor]);
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
        for (; cursor < al.Count; cursor++) merged.Append(al[cursor]);

        string action = c.Status == ChangeStatus.Renamed ? "rename" : "modify";
        if (hunks.Any(h => h.Outcome == HunkOutcome.Conflict))
            return new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.Conflict, "none", hunks, null);
        bool nothing = hunks.All(h => h.Outcome == HunkOutcome.Already) && c.Status != ChangeStatus.Renamed;
        if (nothing)
            return new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.Already, "none", hunks, null);

        if (write)
        {
            var text = merged.ToString();
            if (normalize && t.Eol == "CRLF") text = text.Replace("\n", "\r\n");
            WriteAtomic(tTo, t.Encode(text));
            if (c.Status == ChangeStatus.Renamed) File.Delete(tFrom);
        }
        return new PortFile(c.RelativePath, c.RenamedFrom, PortStatus.Clean, action, hunks,
            normalize ? $"merged ignoring line endings; written with the target's {t.Eol}" : null);
    }

    private static PortFile ByteLevel(FileChange c, byte[] a, byte[] b, byte[] t, string tFrom, string tTo, bool write, string? note)
    {
        if (t.AsSpan().SequenceEqual(b) && c.Status != ChangeStatus.Renamed)
            return File1(c, PortStatus.Already, "none", HunkOutcome.Already, note);
        if (!t.AsSpan().SequenceEqual(a))
            return File1(c, PortStatus.Conflict, "none", HunkOutcome.Conflict,
                (note is null ? "" : note + "; ") + "the target differs from the base, and this file can't be merged line by line");
        if (write)
        {
            WriteAtomic(tTo, b);
            if (c.Status == ChangeStatus.Renamed) File.Delete(tFrom);
        }
        return File1(c, PortStatus.Clean, c.Status == ChangeStatus.Renamed ? "rename" : "replace", HunkOutcome.Applied, note);
    }

    private static PortFile File1(FileChange c, PortStatus s, string action, HunkOutcome o, string? note) =>
        new(c.RelativePath, c.RenamedFrom, s, action, [new PortHunk(o, 0, 0, 0, 0, 0, 0)], note);

    /// <summary>Lines with their terminators kept (the last may have none).</summary>
    internal static List<string> Lines(string text, bool normalizeEol)
    {
        if (normalizeEol) text = TextInspector.NormalizeEol(text);
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') { lines.Add(text[start..(i + 1)]); start = i + 1; }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    private static bool SameBytes(string path, byte[] bytes)
        => new FileInfo(path).Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);

    /// <summary>Write via a temp file in the same directory, then replace: a reader never sees a half file.</summary>
    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".codediffer-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Decoded text that re-encodes to its exact original bytes (so writing it back is lossless).</summary>
    internal sealed class Text
    {
        public required string Content { get; init; }
        public required Encoding Encoding { get; init; }
        public required string Eol { get; init; }

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
            return crlf > 0 && lf > 0 ? "mixed" : crlf > 0 ? "CRLF" : "LF";
        }
    }
}
