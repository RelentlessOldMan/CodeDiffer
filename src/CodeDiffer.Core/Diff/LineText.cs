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

    public static string Join(IEnumerable<string> lines) => string.Join('\n', lines);
}
