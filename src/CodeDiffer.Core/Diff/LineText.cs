namespace CodeDiffer.Core.Diff;

/// <summary>
/// Splits text into lines the way the hunk model counts them: on '\n', with a single trailing newline
/// NOT producing a phantom empty final line (so "a\nb\n" and "a\nb" are both two lines). '\r' is kept
/// verbatim — EOL-only differences are a <c>reason</c> concern handled before a file is ever line-diffed,
/// not something the differ should silently normalize.
/// </summary>
public static class LineText
{
    public static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var parts = text.Split('\n');
        // A trailing '\n' yields a final "" element that isn't a real line — drop exactly one.
        if (text[^1] == '\n')
            return parts[..^1];
        return parts;
    }

    /// <summary>The lines of <c>SplitLines(NormalizeEol(text))</c>, a line at a time: neither the normalized copy nor
    /// every line at once is held. A line ends at CRLF, CR or LF (CRLF first, left to right, as the normalizing does),
    /// and a line ending at the very end starts no line.</summary>
    public static IEnumerable<string> NormalizedLines(string text)
    {
        for (int start = 0; start < text.Length;)
        {
            int end = text.AsSpan(start).IndexOfAny('\r', '\n');
            end = end < 0 ? text.Length : start + end;
            yield return text.Substring(start, end - start);
            start = end + (end + 1 < text.Length && text[end] == '\r' && text[end + 1] == '\n' ? 2 : 1);
        }
    }

    public static string Join(IEnumerable<string> lines) => string.Join('\n', lines);
}
