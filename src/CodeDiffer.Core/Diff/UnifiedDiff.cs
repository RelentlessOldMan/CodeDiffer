using System.Text;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Diff;

/// <summary>
/// Renders a text diff as a git-style unified diff that <c>git apply</c> / <c>patch</c> accept: a
/// <c>diff --git</c> header, <c>---</c>/<c>+++</c> lines (/dev/null for an added or removed file), and
/// <c>@@ -a,b +c,d @@</c> hunks with N lines of context, adjacent edits merged when their contexts touch.
/// A file without a trailing newline gets the standard <c>\ No newline at end of file</c> marker, and a
/// newline-at-EOF-only change is a real (last-line) hunk, not silently identical.
///
/// Memory: one file's two line arrays at a time; the output is streamed to a <see cref="TextWriter"/>.
/// </summary>
public static class UnifiedDiff
{
    public const int DefaultContext = 3;
    private const string NoEolMarker = "\0␀noeol"; // can't occur in decoded text; tags a last line lacking '\n'

    /// <summary>The lines of a text, last line tagged when the text lacks a trailing newline.</summary>
    public static string[] DiffLines(string text)
    {
        var lines = LineText.SplitLines(text);
        if (lines.Length > 0 && text[^1] != '\n') lines[^1] += NoEolMarker;
        return lines;
    }

    /// <summary>
    /// Write one file's unified diff. <paramref name="oldText"/> null = added file, <paramref name="newText"/>
    /// null = removed file. Writes nothing when the texts are identical. Returns whether the diff was coarse
    /// (edit-distance budget exceeded — still correct, the changed middle is shown as one block).
    /// </summary>
    public static bool Write(TextWriter w, string oldPath, string newPath, string? oldText, string? newText,
        int context = DefaultContext, string? extraHeader = null)
    {
        var a = oldText is null ? [] : DiffLines(oldText);
        var b = newText is null ? [] : DiffLines(newText);
        // A whitespace-only change line for line is shown line for line (never a line paired with an identical one elsewhere).
        bool coarse = false;
        var hunks = (oldText is null || newText is null ? null : LineDiffer.WhitespaceAligned(a, b))
                    ?? LineDiffer.Diff(a, b, LineDiffer.DefaultMaxEditDistance, out coarse);
        if (hunks.Count == 0 && oldText is not null && newText is not null && extraHeader is null) return false;

        w.Write($"diff --git a/{oldPath} b/{newPath}\n");
        if (oldText is null) w.Write("new file mode 100644\n");
        if (newText is null) w.Write("deleted file mode 100644\n");
        if (extraHeader is not null) w.Write(extraHeader);
        if (hunks.Count == 0) return coarse; // e.g. a pure rename: header only
        w.Write(oldText is null ? "--- /dev/null\n" : $"--- a/{oldPath}\n");
        w.Write(newText is null ? "+++ /dev/null\n" : $"+++ b/{newPath}\n");
        WriteHunks(w, a, b, hunks, context);
        return coarse;
    }

    /// <summary>Render the coordinate hunks as unified-diff hunks with context.</summary>
    public static void WriteHunks(TextWriter w, IReadOnlyList<string> a, IReadOnlyList<string> b, IReadOnlyList<Hunk> hunks, int context)
    {
        // More context than the longer file is the whole file; a negative one is none. Unclamped, -1 or int.MaxValue
        // (overflowing the arithmetic below) wrote hunk headers git can't read.
        context = Math.Clamp(context, 0, Math.Max(a.Count, b.Count));
        // Normalize each hunk to 0-based spans: old [o0, o0+ol), new [n0, n0+nl).
        var edits = hunks
            .Select(h => (O0: h.OldLines == 0 ? h.OldStart : h.OldStart - 1, Ol: h.OldLines, N0: h.NewStart - 1, Nl: h.NewLines))
            .OrderBy(e => e.O0).ThenBy(e => e.N0)
            .ToList();

        int i = 0;
        while (i < edits.Count)
        {
            // Group edits whose surrounding context would overlap or touch.
            int j = i;
            while (j + 1 < edits.Count && edits[j + 1].O0 - (edits[j].O0 + edits[j].Ol) <= 2 * context) j++;

            var first = edits[i];
            var last = edits[j];
            int oStart = Math.Max(0, first.O0 - context);
            int nStart = first.N0 - (first.O0 - oStart);
            int oEnd = Math.Min(a.Count, last.O0 + last.Ol + context);
            int nEnd = last.N0 + last.Nl + (oEnd - (last.O0 + last.Ol));

            var body = new StringBuilder();
            int pos = oStart;
            for (int k = i; k <= j; k++)
            {
                var e = edits[k];
                for (; pos < e.O0; pos++) Line(body, ' ', a[pos]);
                for (int x = 0; x < e.Ol; x++) Line(body, '-', a[e.O0 + x]);
                for (int x = 0; x < e.Nl; x++) Line(body, '+', b[e.N0 + x]);
                pos = e.O0 + e.Ol;
            }
            for (; pos < oEnd; pos++) Line(body, ' ', a[pos]);

            w.Write($"@@ -{Range(oStart, oEnd - oStart)} +{Range(nStart, nEnd - nStart)} @@\n");
            w.Write(body);
            i = j + 1;
        }
    }

    /// <summary>Unified-diff range: "start,count" with start 1-based, or the line before for an empty range;
    /// ",1" omitted as git does.</summary>
    private static string Range(int start0, int count)
        => count == 0 ? $"{start0},0" : count == 1 ? $"{start0 + 1}" : $"{start0 + 1},{count}";

    private static void Line(StringBuilder sb, char tag, string line)
    {
        bool noEol = line.EndsWith(NoEolMarker, StringComparison.Ordinal);
        sb.Append(tag).Append(noEol ? line[..^NoEolMarker.Length] : line).Append('\n');
        if (noEol) sb.Append("\\ No newline at end of file\n");
    }
}
