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

    /// <summary>Up to this size (either side) a file is sniffed for a NUL whole, and a patch line-diffs it (reading it
    /// whole); past it, its first <see cref="HeadBytes"/> decide, and a patch gives its changed byte ranges.</summary>
    public const long WholeSniffBytes = 16L * 1024 * 1024;

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

    /// <summary>A NUL past <paramref name="head"/> (the file's first bytes, already sniffed), streamed: a file read
    /// only by its head is binary by the same rule as one read whole.</summary>
    public static bool NulPast(string path, byte[] head, CancellationToken ct)
    {
        if (IsWideBom(head)) return false;
        using var fs = OpenShared(path, 1);
        fs.Position = head.Length;
        var buf = new byte[1 << 16];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (buf.AsSpan(0, n).IndexOf((byte)0) >= 0) return true;
        }
        return false;
    }

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

    /// <summary>An ASCII operator character: two of them side by side can read as another token (<c>- -x</c> → <c>--x</c>,
    /// <c>/ *</c> → <c>/*</c>, <c>&gt; =</c> → <c>&gt;=</c>). Brackets, commas and semicolons never fuse.</summary>
    public static bool IsOperatorChar(int c) => c is '!' or '#' or '$' or '%' or '&' or '*' or '+' or '-' or '.' or '/'
        or ':' or '<' or '=' or '>' or '?' or '@' or '\\' or '^' or '|' or '~';

    /// <summary>A quote character: it opens or closes a string literal (a char, a template), and a word character before it
    /// can be the literal's prefix (<c>L"x"</c>, <c>u8"x"</c>, <c>f"x"</c>).</summary>
    public static bool IsQuote(int c) => c is '"' or '\'' or '`';

    /// <summary>The string a scan is in after an unescaped quote <paramref name="c"/>: none opens one, its own quote closes
    /// it, another quote inside it is text ('\0' = none).</summary>
    public static char Quote(char open, int c) => open == '\0' ? (char)c : open == c ? '\0' : open;

    /// <summary>Whether a run of whitespace between these two characters keeps them apart: two word characters or quotes
    /// (<c>L "x"</c> is not <c>L"x"</c>), two operator characters, or a digit and a '.' (<c>1 .5</c> is not <c>1.5</c>).
    /// Between anything else it can vanish without changing a token.</summary>
    public static bool Separates(int before, int after)
        => ((IsWordChar(before) || IsQuote(before)) && (IsWordChar(after) || IsQuote(after)))
           || (IsOperatorChar(before) && IsOperatorChar(after))
           || (before is >= '0' and <= '9' && after == '.') || (before == '.' && after is >= '0' and <= '9');

    /// <summary>
    /// Whether two EOL-normalized texts differ only in whitespace: their <see cref="WhitespaceKey"/>s match, strings read
    /// by <paramref name="rules"/> (<see cref="RulesFor"/> the file's name). A closing quote read as escaped where the
    /// language reads it as text (or the other way round) puts the rest of its line in or out of a string the wrong way
    /// round: a change inside a string read as spacing between tokens, or a reformat of code read as a change in a string.
    /// </summary>
    public static bool SameButWhitespace(string a, string b, StringRules rules = StringRules.Both)
        => string.Equals(WhitespaceKey(a, rules), WhitespaceKey(b, rules), StringComparison.Ordinal);

    /// <summary>How a file's language reads a backslash (or a backtick) before a quote, by the file's extension.</summary>
    public static StringRules RulesFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".csx" => StringRules.CSharp,
        ".ps1" or ".psm1" or ".psd1" => StringRules.PowerShell,
        ".bat" or ".cmd" => StringRules.Literal,
        ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".c++" or ".hpp" or ".hh" or ".hxx" or ".h++" or ".inl" or ".ipp"
            or ".m" or ".mm" or ".java" or ".kt" or ".kts" or ".scala" or ".groovy" or ".gradle" or ".js" or ".jsx"
            or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".json" or ".py" or ".pyi" or ".rb" or ".go" or ".rs" or ".swift"
            or ".php" or ".pl" or ".pm" or ".lua" or ".dart" or ".hlsl" or ".hlsli" or ".fx" or ".fxh" or ".glsl"
            or ".vert" or ".frag" or ".usf" or ".ush" or ".shader" or ".cginc" or ".compute" => StringRules.Escape,
        _ => StringRules.Both,
    };

    /// <summary>
    /// What a whitespace-only change leaves alone, of EOL-normalized text: the same lines, each with its leading and
    /// trailing whitespace dropped, and an inner run of it read as one space where it <see cref="Separates"/> two
    /// tokens (two word characters, or two operator characters) or is inside a quoted string on its line, and as nothing
    /// elsewhere. So reindenting, trailing spaces and spacing round punctuation ("x=1" → "x = 1") are whitespace; a
    /// space that joins or splits a token ("return x" → "returnx", "- -x" → "--x", "/ *" → "/*", "1 .5" → "1.5") or is
    /// taken out of a string (<c>split(" ")</c> → <c>split("")</c>, <c>"Total: "</c> → <c>"Total:"</c>) and a line
    /// joined, split or added are content. A quote not escaped by a backslash (one after an odd run of them: in <c>"\\"</c> the second quote closes) opens a string, the same quote closes it,
    /// and so does a line ending (an apostrophe in prose only makes the rest of its line stricter). The width of a run is never kept,
    /// so a run widened or narrowed inside a string literal reads as whitespace too. Which quote is escaped is the
    /// language's (<paramref name="rules"/>, a <see cref="StringScan"/>): in C a backslash escapes, in a C# verbatim
    /// string (<c>@"C:\temp\"</c>) and in batch it is text, in PowerShell a backtick escapes.
    /// </summary>
    public static string WhitespaceKey(string s, StringRules rules = StringRules.Both)
    {
        var sb = new StringBuilder(s.Length);
        bool lineStart = true;
        var scan = new StringScan(rules);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool escaped = scan.Step(c);
            if (IsHorizontalSpace(c))
            {
                while (i + 1 < s.Length && IsHorizontalSpace(s[i + 1])) scan.Step(s[++i]);
                bool lineEnd = i + 1 >= s.Length || s[i + 1] is '\n' or '\r';
                if (escaped)
                {
                    // An escaped space is a character (taken out, the backslash escapes what follows): kept as it is, the
                    // rest of its run not (a run's width never is), and trailing whitespace still.
                    if (!lineEnd) sb.Append(c);
                    continue;
                }
                if (!lineStart && !lineEnd && (scan.InString || Separates(sb[^1], s[i + 1]))) sb.Append(' ');
                continue;
            }
            lineStart = c == '\n';
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>How a file's strings are read for whitespace (<see cref="TextInspector.RulesFor"/>).</summary>
public enum StringRules
{
    /// <summary>A language not known: a space is in a string if it is so with a backslash read as an escape, or as text.
    /// A whitespace verdict then holds whichever the file means (and a reformat beside an odd escaped quote is content).</summary>
    Both,
    /// <summary>A backslash escapes the next character (C, C++, Java, JS, JSON, Python, HLSL…).</summary>
    Escape,
    /// <summary>A backslash is text (batch).</summary>
    Literal,
    /// <summary>A backtick escapes, in a double-quoted string (a single-quoted one has none); a backtick is no quote.</summary>
    PowerShell,
    /// <summary>A backslash escapes, but in a verbatim string (<c>@"…"</c>, <c>$@"…"</c>, <c>@$"…"</c>) it is text and
    /// <c>""</c> is a quote in it.</summary>
    CSharp,
}

/// <summary>
/// Whether a scan, a character at a time, is in a string on its line: the one reading of quotes the whole-text
/// <see cref="TextInspector.WhitespaceKey"/> and the streamed comparison share, so they always agree. A quote not escaped
/// opens a string, the same quote closes it, and so does a line ending. <see cref="StringRules.Both"/> runs the escape and
/// the literal reading side by side, in a string if either is.
/// </summary>
internal struct StringScan
{
    private Reading _a, _b;
    private readonly bool _both;

    public StringScan(StringRules rules)
    {
        _both = rules == StringRules.Both;
        _a = new Reading(_both ? StringRules.Escape : rules);
        _b = new Reading(StringRules.Literal);
    }

    /// <summary>The scan is in a string, after the characters stepped over.</summary>
    public readonly bool InString => _a.Quote != '\0' || (_both && _b.Quote != '\0');

    /// <summary>Steps over one character (-1: the end; a line ending is '\n' or '\r'): whether it is escaped (by either
    /// reading).</summary>
    public bool Step(int c)
    {
        bool escaped = _a.Step(c);
        if (_both) escaped |= _b.Step(c);
        return escaped;
    }

    private struct Reading(StringRules rules)
    {
        public char Quote; // the quote of the string the scan is in, or '\0'
        private bool _verbatim, _closedVerbatim, _escapeNext;
        private int _p1 = -1, _p2 = -1;

        public bool Step(int c)
        {
            bool escaped = _escapeNext;
            if (!escaped && TextInspector.IsHorizontalSpace(c))
            {
                // Spacing never puts a scan in or out of a string, nor makes a quote verbatim: only what is kept does.
                _escapeNext = false;
                return false;
            }
            _escapeNext = !escaped && rules switch
            {
                StringRules.Escape => c == '\\',
                StringRules.CSharp => c == '\\' && !_verbatim,
                StringRules.PowerShell => c == '`' && Quote != '\'',
                _ => false,
            };
            if (c is '\n' or '\r')
            {
                Quote = '\0';
                _verbatim = _closedVerbatim = false;
            }
            else if (!escaped && (c is '"' or '\'' || (c == '`' && rules != StringRules.PowerShell)))
            {
                if (Quote == '\0')
                {
                    // C#: @"…", $@"…", @$"…", and the second quote of "" in a verbatim string, which reopens it.
                    _verbatim = rules == StringRules.CSharp && c == '"' && (_closedVerbatim || _p1 == '@' || (_p1 == '$' && _p2 == '@'));
                    Quote = (char)c;
                }
                else if (Quote == c)
                {
                    Quote = '\0';
                    _closedVerbatim = _verbatim;
                    _verbatim = false;
                    _p2 = _p1;
                    _p1 = c;
                    return false;
                }
                _closedVerbatim = false;
            }
            else _closedVerbatim = false;
            _p2 = _p1;
            _p1 = c;
            return escaped;
        }
    }
}
