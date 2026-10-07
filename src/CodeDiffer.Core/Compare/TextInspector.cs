using System.Text;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// Byte/text helpers for reason classification: BOM sniffing, binary detection, decode, and the
/// EOL / whitespace normalizations the reason precedence relies on. Binary detection is BOM-aware so
/// a BOM'd UTF-16 file (full of NULs) is NOT mistaken for binary — a BOM-less UTF-16 file is, which
/// matches CodeCompass's documented limitation.
/// </summary>
internal static class TextInspector
{
    public const int HeadBytes = 8192;

    /// <summary>Read up to <paramref name="max"/> leading bytes (the whole file if smaller).</summary>
    public static byte[] ReadHead(string path, int max = HeadBytes)
    {
        using var fs = OpenShared(path);
        var buffer = new byte[max];
        int total = 0, read;
        while (total < max && (read = fs.Read(buffer, total, max - total)) > 0)
            total += read;
        if (total == max) return buffer;
        Array.Resize(ref buffer, total);
        return buffer;
    }

    public static bool HasBom(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 3 && h[0] == 0xEF && h[1] == 0xBB && h[2] == 0xBF) return true;               // UTF-8
        if (h.Length >= 4 && h[0] == 0xFF && h[1] == 0xFE && h[2] == 0x00 && h[3] == 0x00) return true; // UTF-32 LE
        if (h.Length >= 4 && h[0] == 0x00 && h[1] == 0x00 && h[2] == 0xFE && h[3] == 0xFF) return true; // UTF-32 BE
        if (h.Length >= 2 && h[0] == 0xFF && h[1] == 0xFE) return true;                                 // UTF-16 LE
        if (h.Length >= 2 && h[0] == 0xFE && h[1] == 0xFF) return true;                                 // UTF-16 BE
        return false;
    }

    /// <summary>A NUL in the head with no BOM ⇒ treat as binary. (Checks the LEFT or RIGHT caller-side.)</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
        => !HasBom(head) && head.IndexOf((byte)0) >= 0;

    /// <summary>Open for reading without getting in a writer's way: a log being appended to, or a file being deleted,
    /// is still readable (asking for FileShare.Read alone fails on any file open for writing).</summary>
    public static FileStream OpenShared(string path, int bufferSize = 4096)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.SequentialScan);

    /// <summary>The whole file, opened as <see cref="OpenShared"/>.</summary>
    public static byte[] ReadAll(string path)
    {
        using var fs = OpenShared(path, 1);
        long length = fs.Length;
        if (length > Array.MaxLength) throw new IOException($"{path} is too large to read whole ({length:N0} bytes)");
        var ms = new MemoryStream((int)length);
        fs.CopyTo(ms);
        return ms.Length == ms.Capacity ? ms.GetBuffer() : ms.ToArray();
    }

    /// <summary>
    /// Decode bytes to text, honoring a BOM (UTF-8/16/32) and defaulting to UTF-8 without one — strictly: bytes that are
    /// not valid in that encoding (a cp1252 / Latin-1 file) are read as Latin-1 instead, one char per byte, so two
    /// different files never decode to the same text (a lenient decode turns every bad byte into U+FFFD, and
    /// <c>25°C</c> → <c>25±C</c> would read as "the same text").
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        var (enc, skip) = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => ((Encoding)new UTF8Encoding(false, true), 3),
            [0xFF, 0xFE, 0x00, 0x00, ..] => (new UTF32Encoding(false, false, true), 4),
            [0x00, 0x00, 0xFE, 0xFF, ..] => (new UTF32Encoding(true, false, true), 4),
            [0xFF, 0xFE, ..] => (new UnicodeEncoding(false, false, true), 2),
            [0xFE, 0xFF, ..] => (new UnicodeEncoding(true, false, true), 2),
            _ => (new UTF8Encoding(false, true), 0),
        };
        try { return enc.GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    public static string NormalizeEol(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string StripWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsWhiteSpace(c))
                sb.Append(c);
        return sb.ToString();
    }
}
