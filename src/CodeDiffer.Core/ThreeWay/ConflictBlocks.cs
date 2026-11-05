using System.Text;
using CodeDiffer.Core.Sessions;

namespace CodeDiffer.Core.ThreeWay;

/// <summary>One conflict block of a markers file, as the merger wrote it: how many lines each section holds (v1's,
/// base's, v2's), and <see cref="Open"/>: bits 1 (v1), 2 (base), 4 (v2) for a section whose last line had no line
/// ending, so the marker after it was put on a line of its own with one added.</summary>
public readonly record struct ConflictBlock(int V1, int Base, int V2, int Open);

/// <summary>
/// The conflict blocks of a markers file, settled one at a time: a block is found by the line counts the overlay
/// recorded for it (<see cref="OverlayConflict.Blocks"/>) and its exact marker lines, so a bare <c>=======</c> in the
/// text itself is never taken for a marker, and a block with any line other than in the overlay's copy as written
/// (edited by hand) is never touched. Choosing a side replaces the block (its four marker lines and all three sections)
/// with that side's lines; <c>both</c> puts v1's then v2's. Blocks are numbered from 1 in the file as the overlay wrote
/// it, and keep their numbers as others are settled. Bytes are kept exactly: a file with a UTF-16 or UTF-32 byte order
/// mark is decoded as such, any other is cut byte for byte, a UTF-8 byte order mark kept apart from its first line.
/// </summary>
public static class ConflictBlocks
{
    public const string Open1 = "<<<<<<< v1 (", BaseHeader = "||||||| base (", Mid = "=======", Close2 = ">>>>>>> v2 (";

    /// <summary>A file as lines, each with its ending ('\n', after any '\r'), and how to write it back.</summary>
    internal sealed record Doc(Encoding Encoding, byte[] Preamble, List<string> Lines)
    {
        public byte[] Bytes()
        {
            var body = Encoding.GetBytes(string.Concat(Lines));
            return Preamble.Length == 0 ? body : [.. Preamble, .. body];
        }
    }

    /// <summary>Where a block is in a file: the line of its <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; v1</c> marker.</summary>
    internal readonly record struct Found(int Number, int At, ConflictBlock Block);

    /// <summary>The marker lines of each block, as the overlay wrote them, and the line it starts on there.</summary>
    internal readonly record struct Headers(string Open, string Base, string Close, int At);

    /// <summary>Block numbers: "all" (null), or numbers and ranges like <c>1,3,5-7</c>.</summary>
    public static SortedSet<int>? Parse(string spec)
    {
        if (spec.Trim() == "all") return null;
        var set = new SortedSet<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split('-', 2, StringSplitOptions.TrimEntries);
            bool ok = int.TryParse(ends[0], out int from) && from >= 1;
            int to = from;
            if (ok && ends.Length == 2) ok = int.TryParse(ends[1], out to) && to >= from;
            if (!ok)
                throw new ArgumentException($"--blocks takes all, or block numbers and ranges like 1,3,5-7 — not \"{spec}\"");
            if (to - from > 100_000) throw new ArgumentException($"--blocks range {part} is too long");
            for (int n = from; n <= to; n++) set.Add(n);
        }
        if (set.Count == 0) throw new ArgumentException("--blocks needs all, or one or more block numbers");
        return set;
    }

    internal static Doc? Read(byte[] bytes)
    {
        ReadOnlySpan<byte> b = bytes;
        Encoding? enc = b.StartsWith((byte[])[0xFF, 0xFE, 0, 0]) ? new UTF32Encoding(false, false, true)
            : b.StartsWith((byte[])[0, 0, 0xFE, 0xFF]) ? new UTF32Encoding(true, false, true)
            : b.StartsWith((byte[])[0xFF, 0xFE]) ? new UnicodeEncoding(false, false, true)
            : b.StartsWith((byte[])[0xFE, 0xFF]) ? new UnicodeEncoding(true, false, true)
            : null;
        if (enc is null) // byte for byte, a UTF-8 byte order mark kept apart so a marker on the first line starts it
        {
            int bom = b.StartsWith((byte[])[0xEF, 0xBB, 0xBF]) ? 3 : 0;
            return new Doc(Encoding.Latin1, bytes[..bom], ChangePorterLines(Encoding.Latin1.GetString(bytes, bom, bytes.Length - bom)));
        }
        int pre = enc is UTF32Encoding ? 4 : 2;
        try { return new Doc(enc, bytes[..pre], ChangePorterLines(enc.GetString(bytes, pre, bytes.Length - pre))); }
        catch (DecoderFallbackException) { return null; }
    }

    private static List<string> ChangePorterLines(string text) => CodeDiffer.Core.Port.ChangePorter.Lines(text, false);

    /// <summary>A line without its ending.</summary>
    internal static string Bare(string line)
    {
        int n = line.Length;
        if (n > 0 && line[n - 1] == '\n') n--;
        if (n > 0 && line[n - 1] == '\r') n--;
        return line[..n];
    }

    /// <summary>Whether block <paramref name="g"/> starts at line <paramref name="i"/>: marker lines where its line
    /// counts put them (exactly <paramref name="h"/>'s, if given).</summary>
    private static bool At(List<string> lines, int i, ConflictBlock g, Headers? h)
    {
        int b = i + 1 + g.V1, m = b + 1 + g.Base, c = m + 1 + g.V2;
        if (c >= lines.Count) return false;
        string o = Bare(lines[i]), bh = Bare(lines[b]), ch = Bare(lines[c]);
        return Bare(lines[m]) == Mid && (h is { } x
            ? o == x.Open && bh == x.Base && ch == x.Close
            : o.StartsWith(Open1, StringComparison.Ordinal) && bh.StartsWith(BaseHeader, StringComparison.Ordinal) && ch.StartsWith(Close2, StringComparison.Ordinal));
    }

    /// <summary>The marker lines of each block in the file as the overlay wrote it, found in order; null if a block
    /// isn't where its line counts say (the records don't fit the file).</summary>
    internal static List<Headers>? HeadersOf(List<string> original, IReadOnlyList<ConflictBlock> blocks)
    {
        var hs = new List<Headers>(blocks.Count);
        int i = 0;
        foreach (var g in blocks)
        {
            while (i < original.Count && !At(original, i, g, null)) i++;
            if (i >= original.Count) return null;
            hs.Add(new Headers(Bare(original[i]), Bare(original[i + 1 + g.V1]), Bare(original[i + 3 + g.V1 + g.Base + g.V2]), i));
            i += 4 + g.V1 + g.Base + g.V2;
        }
        return hs;
    }

    /// <summary>The blocks still in <paramref name="lines"/> as written (each found once, every line as in
    /// <paramref name="original"/>, the file as the overlay wrote it), by number.</summary>
    internal static SortedDictionary<int, Found> Find(List<string> lines, List<string> original, IReadOnlyList<ConflictBlock> blocks, List<Headers> hs)
    {
        var at = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].StartsWith(Open1, StringComparison.Ordinal))
            {
                var k = Bare(lines[i]);
                if (!at.TryGetValue(k, out var l)) at[k] = l = [];
                l.Add(i);
            }
        var found = new SortedDictionary<int, Found>();
        for (int n = 0; n < blocks.Count; n++)
            if (at.TryGetValue(hs[n].Open, out var l) && l.Where(i => At(lines, i, blocks[n], hs[n]) && Same(lines, i, original, hs[n].At, blocks[n])).ToList() is [var only])
                found[n + 1] = new Found(n + 1, only, blocks[n]);
        // Two found blocks that overlap (only an edit could make that): neither is touched.
        var all = found.Values.OrderBy(f => f.At).ToList();
        for (int k = 1; k < all.Count; k++)
            if (all[k].At <= End(all[k - 1])) { found.Remove(all[k].Number); found.Remove(all[k - 1].Number); }
        return found;
    }

    /// <summary>Whether the block at line <paramref name="i"/> holds just the lines it held as written.</summary>
    private static bool Same(List<string> lines, int i, List<string> original, int at, ConflictBlock g)
    {
        for (int k = 0, n = 4 + g.V1 + g.Base + g.V2; k < n; k++)
            if (!string.Equals(lines[i + k], original[at + k], StringComparison.Ordinal)) return false;
        return true;
    }

    private static int End(Found f) => f.At + 3 + f.Block.V1 + f.Block.Base + f.Block.V2;

    /// <summary>The section's lines (v1 1, base 2, v2 4), its last line's added ending taken off when
    /// <paramref name="strip"/> and the block says it had none.</summary>
    private static IEnumerable<string> Section(List<string> lines, Found f, int which, bool strip)
    {
        var g = f.Block;
        var (start, count) = which switch
        {
            1 => (f.At + 1, g.V1),
            2 => (f.At + 2 + g.V1, g.Base),
            _ => (f.At + 3 + g.V1 + g.Base, g.V2),
        };
        for (int k = 0; k < count; k++)
        {
            var line = lines[start + k];
            if (k == count - 1 && strip && (g.Open & which) != 0)
            {
                // The ending added is the one the marker after it has: "\r\n" or "\n".
                var nl = lines[start + count].EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                if (line.EndsWith(nl, StringComparison.Ordinal)) line = line[..^nl.Length];
            }
            yield return line;
        }
    }

    /// <summary><paramref name="lines"/> with the <paramref name="chosen"/> blocks replaced by <paramref name="choice"/>
    /// (v1, v2 or both).</summary>
    internal static List<string> Settle(List<string> lines, IEnumerable<Found> chosen, string choice)
    {
        var o = new List<string>(lines.Count);
        int cursor = 0;
        foreach (var f in chosen.OrderBy(f => f.At))
        {
            o.AddRange(lines.Skip(cursor).Take(f.At - cursor));
            switch (choice)
            {
                case "v1": o.AddRange(Section(lines, f, 1, true)); break;
                case "v2": o.AddRange(Section(lines, f, 4, true)); break;
                default: // both: v1's lines, then v2's; only the last of them can lose an ending it didn't have
                    o.AddRange(Section(lines, f, 1, f.Block.V2 == 0));
                    o.AddRange(Section(lines, f, 4, true));
                    break;
            }
            cursor = End(f) + 1;
        }
        o.AddRange(lines.Skip(cursor));
        return o;
    }

    /// <summary>How many conflict blocks lines hold, counted as <see cref="OverlayConflicts.MarkerBlocks"/> counts them.</summary>
    internal static int Left(List<string> lines)
    {
        int open = 0, mid = 0, close = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith(Open1, StringComparison.Ordinal)) open++;
            else if (line.StartsWith(BaseHeader, StringComparison.Ordinal)) mid++;
            else if (line.StartsWith(Close2, StringComparison.Ordinal)) close++;
        }
        return Math.Max(open, Math.Max(mid, close));
    }

    /// <summary>The blocks of each undecided markers file <paramref name="patterns"/> and <paramref name="kinds"/> pick
    /// (at most <paramref name="max"/> files): numbered, with each section's line count and first
    /// <paramref name="show"/> lines, and the resolve that settles one.</summary>
    public static string View(OverlayConflicts s, IReadOnlyList<string> patterns, IReadOnlyList<string> kinds, int max = 10, int show = 4)
    {
        var o = new StringBuilder();
        var matcher = new ConflictMatcher(patterns, kinds);
        var cs = s.All.Where(c => matcher.Matches(c) && c.Markers is not null && !s.IsDecided(c)).ToList();
        o.Append($"{cs.Count:N0} undecided file(s) with markers{(matcher.Any ? $" matching {matcher}" : "")} in {s.Dir}\n");
        var files = Path.Combine(s.Dir, "files");
        foreach (var c in cs.Take(max))
        {
            var m = c.Markers!;
            var native = m.Replace('/', Path.DirectorySeparatorChar);
            o.Append($"\n{ResultStore.Escaped(c.Path)}  (files\\{ResultStore.Escaped(native)})\n");
            var full = Path.Combine(files, native);
            var original = Path.Combine(s.Dir, OverlayConflicts.OriginalsDir, native);
            if (c.Blocks is not { } g || !File.Exists(original)) { o.Append("  its blocks aren't recorded (written by an older CodeDiffer): write the overlay again\n"); continue; }
            if (!File.Exists(full)) { o.Append("  the file is gone: undo it to put it back\n"); continue; }
            if (Read(File.ReadAllBytes(original)) is not { } was || Read(File.ReadAllBytes(full)) is not { } now || HeadersOf(was.Lines, g) is not { } hs)
            {
                o.Append("  can't be read as the text it was written in\n");
                continue;
            }
            var found = Find(now.Lines, was.Lines, g, hs);
            o.Append($"  {g.Count:N0} block(s) · {found.Count:N0} as written · {Left(now.Lines):N0} with markers in the file now\n");
            for (int n = 1; n <= g.Count; n++)
            {
                if (!found.TryGetValue(n, out var f)) { o.Append($"  block {n}: settled or edited by hand\n"); continue; }
                o.Append($"  block {n} (line {f.At + 1:N0}):\n");
                foreach (var (which, name, count) in new[] { (1, "v1", f.Block.V1), (2, "base", f.Block.Base), (4, "v2", f.Block.V2) })
                {
                    o.Append($"    {name} · {count:N0} line(s)\n");
                    foreach (var line in Section(now.Lines, f, which, true).Take(show))
                        o.Append("      | ").Append(Printable(Shown(now, Bare(line)))).Append('\n');
                    if (count > show) o.Append($"      | ... {count - show:N0} more\n");
                }
            }
            if (found.Count > 0)
                o.Append($"  settle: codediffer resolve \"{s.Dir}\" v1|v2|both \"/{ResultStore.Escaped(c.Path)}\" --blocks N[,N...]|all   (dry run; --write records it)\n");
        }
        if (cs.Count > max) o.Append($"\n... {cs.Count - max:N0} more file(s) (--max N, or a pattern)\n");
        return o.ToString();
    }

    /// <summary>A line of a file cut byte for byte, read as UTF-8 to show it.</summary>
    private static string Shown(Doc d, string line)
        => d.Encoding.CodePage == Encoding.Latin1.CodePage ? Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(line)) : line;

    /// <summary>A line as shown: at most 160 characters, control characters as '?'.</summary>
    private static string Printable(string line)
    {
        var t = line.Length > 160 ? line[..160] + "…" : line;
        return string.Create(t.Length, t, (span, x) => { for (int i = 0; i < x.Length; i++) span[i] = char.IsControl(x[i]) && x[i] != '\t' ? '?' : x[i]; });
    }

    /// <summary>Numbers as short ranges: 1-3,5.</summary>
    public static string Ranges(IEnumerable<int> numbers)
    {
        var parts = new List<string>();
        int? from = null, last = null;
        foreach (var n in numbers.Order())
        {
            if (last is { } l && n == l + 1) { last = n; continue; }
            if (from is { } f) parts.Add(f == last ? $"{f}" : $"{f}-{last}");
            from = last = n;
        }
        if (from is { } f2) parts.Add(f2 == last ? $"{f2}" : $"{f2}-{last}");
        return string.Join(',', parts);
    }
}
