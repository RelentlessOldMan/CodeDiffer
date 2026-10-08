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

    /// <summary>A NUL, unless a UTF-16/32 BOM says NULs are half its characters ⇒ treat as binary. UTF-8 never needs a
    /// NUL, so a UTF-8 BOM excuses none. (Checks the LEFT or RIGHT caller-side.) Given the whole file when it was read
    /// whole — a NUL past the first 8 KB makes it binary too — else its head.</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
        => !IsWideBom(head) && head.IndexOf((byte)0) >= 0;

    private static bool IsWideBom(ReadOnlySpan<byte> h)
        => h.Length >= 2 && ((h[0] == 0xFF && h[1] == 0xFE) || (h[0] == 0xFE && h[1] == 0xFF))       // UTF-16 (and UTF-32 LE)
           || (h.Length >= 4 && h[0] == 0x00 && h[1] == 0x00 && h[2] == 0xFE && h[3] == 0xFF);       // UTF-32 BE

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

    /// <summary>Whether two files hold the same bytes, streamed (any size, a buffer's worth of memory).</summary>
    public static bool SameBytes(string a, string b)
    {
        using var fa = OpenShared(a, 1);
        using var fb = OpenShared(b, 1);
        return fa.Length == fb.Length && SameStream(fa, fb, fa.Length);
    }

    /// <summary>Whether a file holds exactly these bytes, streamed.</summary>
    public static bool SameBytes(string path, byte[] bytes)
    {
        using var fs = OpenShared(path, 1);
        return fs.Length == bytes.Length && SameStream(fs, new MemoryStream(bytes, writable: false), bytes.Length);
    }

    private static bool SameStream(Stream a, Stream b, long length)
    {
        int size = (int)Math.Clamp(length, 1, 1 << 20);
        var ba = new byte[size];
        var bb = new byte[size];
        while (true)
        {
            int na = a.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
            int nb = b.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            if (na == 0) return true;
        }
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
        // A UTF-8 BOM before legacy bytes is still a BOM (the streamed check skips it too): only the rest is Latin-1.
        catch (DecoderFallbackException) { int k = skip == 3 ? 3 : 0; return Encoding.Latin1.GetString(bytes, k, bytes.Length - k); }
    }

    public static string NormalizeEol(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Horizontal whitespace for the whitespace reason: ASCII space, tab, VT and FF only — never NBSP, NEL or
    /// another Unicode space (in a single-byte code page those are other characters), and the same set the streamed
    /// byte comparison uses.</summary>
    public static bool IsHorizontalSpace(int c) => c is ' ' or '\t' or 0x0B or 0x0C;

    /// <summary>A character a run of whitespace can't vanish next to without joining two tokens: an ASCII letter, digit or
    /// '_', or anything non-ASCII (a decoded char, or a byte of one in the streamed comparison — the same verdict).</summary>
    public static bool IsWordChar(int c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or >= 0x80;

    /// <summary>
    /// What a whitespace-only change leaves alone, of EOL-normalized text: the same lines, each with its leading and
    /// trailing whitespace dropped, and an inner run of it read as one space between two word characters and as
    /// nothing elsewhere. So reindenting, trailing spaces and spacing round punctuation ("x=1" → "x = 1") are
    /// whitespace; a space that joins or splits a token ("return x" → "returnx") and a line joined, split or added
    /// are content.
    /// </summary>
    public static string WhitespaceKey(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lineStart = true;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (IsHorizontalSpace(c))
            {
                while (i + 1 < s.Length && IsHorizontalSpace(s[i + 1])) i++;
                if (!lineStart && i + 1 < s.Length && IsWordChar(sb[^1]) && IsWordChar(s[i + 1])) sb.Append(' ');
                continue;
            }
            lineStart = c == '\n';
            sb.Append(c);
        }
        return sb.ToString();
    }
}
