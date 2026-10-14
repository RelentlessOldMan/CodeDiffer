using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;

namespace CodeDiffer.Core.Verify;

/// <summary>
/// A tree file's lines as the checks read them: decoded as the engine decodes (<see cref="TextInspector.Decode"/>: a BOM
/// honoured, else UTF-8, a file that is not valid UTF-8 read as Latin-1, so two different files never read as the same
/// text), split on '\n' with a '\r' kept, and a last line without a '\n' not the same line as it is with one (a change of
/// only the final newline is a change of the last line, as git and the unified diff write it).
/// </summary>
internal static class TreeLines
{
    public static string[] Read(string path) => UnifiedDiff.DiffLines(TextInspector.Decode(File.ReadAllBytes(path)));

    /// <summary>The same lines streamed, for a file too big to hold. Past a BOM, one char per byte: lines are equal exactly
    /// when their bytes are, as decoded whole, and the whitespace the checks know is ASCII.</summary>
    public static IEnumerable<string> Stream(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var head = new byte[4];
        int n = fs.ReadAtLeast(head, 4, throwOnEndOfStream: false);
        bool utf8Bom = n >= 3 && head is [0xEF, 0xBB, 0xBF, _];
        bool otherBom = (n >= 2 && head is [0xFF, 0xFE, _, _] or [0xFE, 0xFF, _, _]) || (n == 4 && head is [0x00, 0x00, 0xFE, 0xFF]);
        fs.Position = utf8Bom ? 3 : 0;
        using var r = otherBom ? new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true)
            : new StreamReader(fs, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
        var sb = new StringBuilder();
        var buf = new char[1 << 15];
        int got;
        while ((got = r.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < got; i++)
            {
                if (buf[i] != '\n') { sb.Append(buf[i]); continue; }
                yield return sb.ToString();
                sb.Clear();
            }
        if (sb.Length > 0) yield return sb.Append(UnifiedDiff.NoEolMarker).ToString();
    }
}
