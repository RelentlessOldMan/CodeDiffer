using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// Why a file could not be read, as a cause a tally counts without naming the file: "denied", "in use", "missing",
/// "path too long", "offline" (a cloud placeholder), "network", "disk error", else "Windows error N" or the error's type. A
/// <see cref="FileChange.Unreadable"/> note starts with it in brackets, with the side when known —
/// "[right, in use] The process cannot access the file…" — so the warnings can say what kept the files out.
/// </summary>
public static class ReadFailure
{
    /// <summary>The cause of <paramref name="ex"/>, from its Windows error code (the message is translated; the code isn't).</summary>
    public static string Cause(Exception ex)
    {
        int code = ShareGuard.WindowsCode(ex);
        if (ex is UnauthorizedAccessException || code == 5) return "denied";
        if (ex is PathTooLongException || code == 206) return "path too long";
        if (ex is FileNotFoundException or DirectoryNotFoundException) return "missing";
        return code switch
        {
            32 or 33 => "in use",                                    // sharing / lock violation
            2 or 3 => "missing",
            4350 or (>= 358 and <= 406) => "offline",                 // ERROR_FILE_OFFLINE, ERROR_CLOUD_FILE_*
            53 or 59 or 64 or 67 or 121 or 1231 or 1236 => "network", // path gone, net error, timeout, unreachable
            23 or 1117 or 1392 => "disk error",                       // CRC, device I/O, corrupt file
            > 0 => $"Windows error {code}",
            _ => ex.GetType().Name,
        };
    }

    /// <summary>The note for a file that couldn't be read: its cause (and side, when known), then the error.</summary>
    public static string Note(Exception ex, string? side = null)
        => $"[{(side is null ? "" : side + ", ")}{Cause(ex)}] {ex.Message}";

    /// <summary>A pair that couldn't be read: which side fails when opened again, as the read opens it, and why; when
    /// both open now, the first error's cause with no side.</summary>
    public static string PairNote(string leftPath, string rightPath, Exception ex)
        => Fails(leftPath) is { } l ? Note(l, "left") : Fails(rightPath) is { } r ? Note(r, "right") : Note(ex);

    private static Exception? Fails(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            f.ReadByte();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex; }
    }

    /// <summary>The unreadable paths of <paramref name="r"/> by side and cause — "right in use 2 · left denied 1" —
    /// the sides named <paramref name="left"/> and <paramref name="right"/>; empty when there are none.</summary>
    public static IEnumerable<(string Key, int Count)> Tally(CompareReport r, string left = "left", string right = "right")
        => r.Changes.Where(c => c.Unreadable is not null)
            .Select(c => Key(c, left, right))
            .GroupBy(k => k).Select(g => (g.Key, g.Count()))
            .OrderByDescending(t => t.Item2).ThenBy(t => t.Key, StringComparer.Ordinal);

    /// <summary>The tally as one line: "right in use 2 · left denied 1".</summary>
    public static string Line(IEnumerable<(string Key, int Count)> tally)
        => string.Join(" · ", tally.GroupBy(t => t.Key).Select(g => $"{g.Key} {g.Sum(t => t.Count):N0}"));

    private static string Key(FileChange c, string left, string right)
    {
        if (c.BehindLink) return "not followed (link, or a name Windows can't open)";
        var note = c.Unreadable!;
        int end = note.StartsWith('[') ? note.IndexOf(']') : -1;
        if (end < 0) return "cause not recorded"; // saved before 1.0.126
        var parts = note[1..end].Split(", ", 2);
        // An add or remove is one side's file; a pair's note says which side failed.
        var side = parts.Length == 2 ? parts[0] : c.Status == ChangeStatus.Removed ? "left" : c.Status == ChangeStatus.Added ? "right" : null;
        var name = side switch { "left" => left, "right" => right, _ => "a side" };
        return $"{name} {parts[^1]}";
    }
}
