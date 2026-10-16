using System.Runtime.CompilerServices;
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

    /// <summary>Whether two files hold the same bytes, streamed (any size, a buffer's worth of memory); stops at
    /// the first difference, and a cancel is seen between chunks.</summary>
    public static bool SameBytes(string a, string b, CancellationToken ct = default)
    {
        using var fa = OpenShared(a, 1);
        using var fb = OpenShared(b, 1);
        return fa.Length == fb.Length && SameStream(fa, fb, fa.Length, ct);
    }

    /// <summary>Whether a file holds exactly these bytes, streamed.</summary>
    public static bool SameBytes(string path, byte[] bytes)
    {
        using var fs = OpenShared(path, 1);
        return fs.Length == bytes.Length && SameStream(fs, new MemoryStream(bytes, writable: false), bytes.Length);
    }

    private static bool SameStream(Stream a, Stream b, long length, CancellationToken ct = default)
    {
        int size = (int)Math.Clamp(length, 1, 1 << 20);
        var pool = System.Buffers.ArrayPool<byte>.Shared; // up to 2 × 1 MB a file: pooled, not LOH garbage per call
        var ba = pool.Rent(size);
        var bb = pool.Rent(size);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int na = a.ReadAtLeast(ba.AsSpan(0, size), size, throwOnEndOfStream: false);
                int nb = b.ReadAtLeast(bb.AsSpan(0, size), size, throwOnEndOfStream: false);
                if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
                if (na == 0) return true;
            }
        }
        finally
        {
            pool.Return(ba);
            pool.Return(bb);
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
        // Not UTF-8 (a legacy code page): said without throwing first, which cost more than the decode.
        if (skip is 0 or 3 && !System.Text.Unicode.Utf8.IsValid(bytes.AsSpan(skip))) return Encoding.Latin1.GetString(bytes, skip, bytes.Length - skip);
        try { return enc.GetString(bytes, skip, bytes.Length - skip); }
        // A UTF-8 BOM before legacy bytes is still a BOM (the streamed check skips it too): only the rest is Latin-1.
        catch (DecoderFallbackException) { int k = skip == 3 ? 3 : 0; return Encoding.Latin1.GetString(bytes, k, bytes.Length - k); }
    }

    /// <summary>
    /// Whether <see cref="Decode"/> reads these bytes as Latin-1 (not UTF-8, no UTF-16/32 BOM) and they hold a byte in
    /// 0x80–0x9F: in cp1252, the usual legacy code page, those are punctuation (“ ” € …), in Latin-1 control characters.
    /// The same text as a UTF-8 side is then no proof of a re-save — U+0093 is what a byte-for-byte "conversion" makes.
    /// </summary>
    public static bool LegacyC1(ReadOnlySpan<byte> bytes)
    {
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0x00, 0x00, 0xFE, 0xFF, ..]) return false;
        var rest = bytes is [0xEF, 0xBB, 0xBF, ..] ? bytes[3..] : bytes;
        return !System.Text.Unicode.Utf8.IsValid(rest) && rest.IndexOfAnyInRange((byte)0x80, (byte)0x9F) >= 0;
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
    {
        // The two keys a character at a time: never built whole (two 8 MB files' keys were 32 MB more).
        var ka = new WhitespaceKeyReader(a, rules);
        var kb = new WhitespaceKeyReader(b, rules);
        while (true)
        {
            int ca = ka.Next(), cb = kb.Next();
            if (ca != cb) return false;
            if (ca < 0) return true;
        }
    }

    /// <summary>Whether a text's last line has no line ending and is only whitespace (<c>"b.\n␠"</c>): a line of its own,
    /// which <see cref="WhitespaceKey"/>, dropping trailing whitespace, can't tell from no line (<c>"b.\n"</c>). One added
    /// or taken away is a line added or taken away: content.</summary>
    public static bool EndsInBlankLine(string s)
    {
        int i = s.Length;
        while (i > 0 && IsHorizontalSpace(s[i - 1])) i--;
        return i < s.Length && (i == 0 || s[i - 1] is '\n' or '\r');
    }

    /// <summary>How a file's language reads a backslash (or a backtick) before a quote, by the file's extension.</summary>
    public static StringRules RulesFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".csx" => StringRules.CSharp,
        ".ps1" or ".psm1" or ".psd1" => StringRules.PowerShell,
        ".bat" or ".cmd" => StringRules.Literal,
        ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".c++" or ".hpp" or ".hh" or ".hxx" or ".h++" or ".inl" or ".ipp"
            or ".mm" or ".java" or ".kt" or ".kts" or ".scala" or ".groovy" or ".gradle" or ".js" or ".jsx"
            or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".json" or ".py" or ".pyi" or ".rb" or ".go" or ".rs" or ".swift"
            or ".php" or ".pl" or ".pm" or ".lua" or ".dart" or ".hlsl" or ".hlsli" or ".fx" or ".fxh" or ".glsl"
            or ".vert" or ".frag" or ".usf" or ".ush" or ".shader" or ".cginc" or ".compute" => StringRules.Escape,
        _ => StringRules.Both, // .m too: Objective-C escapes, MATLAB doesn't
    };

    /// <summary>The rules for a file compared under two names (a rename): its language's if both name it, else
    /// <see cref="StringRules.Both"/>, so a verdict never depends on which side is the new one.</summary>
    public static StringRules RulesFor(string oldPath, string newPath)
        => RulesFor(oldPath) is var a && a == RulesFor(newPath) ? a : StringRules.Both;

    /// <summary>
    /// What a whitespace-only change leaves alone, of EOL-normalized text: the same lines, each with its leading and
    /// trailing whitespace dropped, and an inner run of it read as one space where it <see cref="Separates"/> two
    /// tokens (two word characters, or two operator characters) or is inside a quoted string on its line, and as nothing
    /// elsewhere. So reindenting, trailing spaces and spacing round punctuation ("x=1" → "x = 1") are whitespace; a
    /// space that joins or splits a token ("return x" → "returnx", "- -x" → "--x", "/ *" → "/*", "1 .5" → "1.5") or is
    /// taken out of a string (<c>split(" ")</c> → <c>split("")</c>, <c>"Total: "</c> → <c>"Total:"</c>) and a line
    /// joined, split or added are content. A quote not escaped by a backslash (one after an odd run of them: in <c>"\\"</c> the second quote closes) opens a string, the same quote closes it,
    /// and so does a line ending; an apostrophe is read both as a quote and as not one (prose in a comment, a Rust
    /// lifetime), and a string after a raw prefix both as raw and not. The width of a run is never kept,
    /// so a run widened or narrowed inside a string literal reads as whitespace too. Which quote is escaped is the
    /// language's (<paramref name="rules"/>, a <see cref="StringScan"/>): in C a backslash escapes, in a C# verbatim
    /// string (<c>@"C:\temp\"</c>) and in batch it is text, in PowerShell a backtick escapes.
    /// </summary>
    public static string WhitespaceKey(string s, StringRules rules = StringRules.Both)
    {
        var sb = new StringBuilder(s.Length);
        var key = new WhitespaceKeyReader(s, rules);
        for (int c; (c = key.Next()) >= 0;) sb.Append((char)c);
        return sb.ToString();
    }

    /// <summary>Whether a backslash before a line ending splices the two lines into one (C's preprocessor, Python): the
    /// spacing before the backslash and the next line's indent are then one run, between the characters either side.</summary>
    internal static bool Splices(StringRules rules) => rules is StringRules.Escape or StringRules.Both;

    /// <summary>
    /// Whether a run held back before a backslash (where <see cref="Splices"/>) is kept, as one space, decided at the
    /// backslash: <paramref name="last"/> the character before the run, <paramref name="next"/> the one after the
    /// backslash (-1: the end). Before a splice (<paramref name="splice"/>) it is part of the run across it, kept or not
    /// after it; in <see cref="StringRules.Both"/> also kept as a literal reading, where the backslash is text, keeps it:
    /// so the verdict is both readings'. One ending the text is read as before a splice too (a hunk's last line, checked
    /// alone, then reads as it does in its file): nothing follows it, so it counts only in a string.
    /// </summary>
    internal static bool KeepsRunBeforeBackslash(in StringScan scan, bool both, int last, int next, out bool splice)
    {
        splice = next is '\n' or '\r';
        if (splice) return both && (scan.KeepsRunUnspliced || Separates(last, '\\'));
        return scan.KeepsRun || (both || next != -1) && Separates(last, '\\');
    }

    /// <summary><see cref="WhitespaceKey"/> a character at a time: what it appends, in order (-1 at the end).</summary>
    private struct WhitespaceKeyReader(string s, StringRules rules)
    {
        private readonly string _s = s;
        private readonly bool _splices = Splices(rules), _both = rules == StringRules.Both;
        private StringScan _scan = new(rules);
        private int _i;
        private bool _lineStart = true;
        private int _last = -1; // the last character of the key so far
        private int _solid = -1, _solidPrev = -1; // the last two characters of it but a run's space
        // A splice: a run held back before a backslash; just past one; spacing either side of it; the character before
        // its backslash.
        private bool _runBeforeBackslash, _spliced, _spliceRun;
        private int _beforeSplice = -1;

        public int Next() => _last = Read();

        private int Solid(int c)
        {
            (_solidPrev, _solid) = (_solid, c);
            return c;
        }

        private int Read()
        {
            var s = _s;
            for (; _i < s.Length; _i++)
            {
                char c = s[_i];
                if (_spliced && !IsHorizontalSpace(c))
                {
                    // The first character past a splice: the spacing either side of it is one run, between the character
                    // before the backslash and this one (returned first; c is read again on the next call).
                    _spliced = false;
                    bool run = _spliceRun;
                    _spliceRun = false;
                    if (run && c != '\n' && (_scan.KeepsRun || Separates(_beforeSplice, c))) return ' ';
                }
                if (_runBeforeBackslash)
                {
                    // The backslash a run was held back for.
                    _runBeforeBackslash = false;
                    bool keep = KeepsRunBeforeBackslash(_scan, _both, _last, _i + 1 < s.Length ? s[_i + 1] : -1, out bool splice);
                    _spliceRun = splice;
                    if (keep) return ' ';
                }
                bool escaped = _scan.Step(c);
                if (IsHorizontalSpace(c))
                {
                    while (_i + 1 < s.Length && IsHorizontalSpace(s[_i + 1])) _scan.Step(s[++_i]);
                    bool lineEnd = _i + 1 >= s.Length || s[_i + 1] is '\n' or '\r';
                    if (escaped)
                    {
                        // An escaped space is a character (taken out, the backslash escapes what follows): kept as it is, the
                        // rest of its run not (a run's width never is), and trailing whitespace still, but where a backslash
                        // splices: there "\ " ending a line is no splice, and taken out it makes one.
                        if (!lineEnd || _splices) { _i++; return Solid(c); }
                        continue;
                    }
                    if (_spliced) { _spliceRun = true; continue; } // a continued line's indent
                    if (_splices && !_lineStart && !lineEnd && s[_i + 1] == '\\') { _runBeforeBackslash = true; continue; }
                    if (!_lineStart && !lineEnd && (_scan.KeepsRun || Separates((char)_last, s[_i + 1]))) { _i++; return ' '; }
                    continue;
                }
                if (c == '\n' && escaped && _splices && _solid == '\\') { _spliced = true; _beforeSplice = _solidPrev; }
                _lineStart = c == '\n';
                _i++;
                return Solid(c);
            }
            return -1;
        }
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
/// opens a string, the same quote closes it, and so does a line ending. Where a line can be read more than one way the
/// readings run side by side, in a string if any is: an apostrophe a quote (a char literal, a string) or not (prose in
/// a comment, a Rust lifetime: read as a quote, the next apostrophe, inside a real string, would close it); a string
/// after a raw prefix (<c>r"…"</c>, <c>R"(…)"</c>, <c>r#"…"#</c>, a backtick string, C#'s <c>"""…"""</c>) one whose
/// backslashes are text or not; and in <see cref="StringRules.Both"/> a backslash an escape or text. Where a backslash
/// escapes (and in C#) one reading more reads the line's structure: it skips <c>//</c> and <c>/* … */</c> comments (a
/// <c>3"</c> in one opens no string), closes a C++ raw string only at its <c>)delim"</c> and a <c>"""</c> or
/// <c>'''</c> string only at the same three quotes (the quotes inside them are text). A reading wrong for the file only
/// puts more of a line in a string, so a verdict is stricter, never one that calls a change in a string whitespace.
/// </summary>
internal struct StringScan
{
    private Readings _r;
    private readonly int _n;
    private int _escapeNext, _closedVerbatim, _inString; // a bit a reading
    private readonly int _unspliced;                      // the readings whose backslashes are text (Both's literal ones)
    private readonly bool _structured;                    // one reading reads comments, raw and triple-quoted strings
    private int _everyChar;                               // the readings that need every character, now (Structured)
    private int _p1 = -1, _p2 = -1;                       // the last two characters stepped over but spacing
    private ulong _tail;                                  // the last eight stepped over, spacing too, a byte each (Tail)
    private Pp _pp;                                       // where on a preprocessor line the scan is
    private ulong _directive;                             // its directive's name so far, a byte a character

    public StringScan(StringRules rules)
    {
        _n = 0;
        var e = rules == StringRules.Both ? StringRules.Escape : rules;
        if (e is StringRules.Escape or StringRules.CSharp)
        {
            _r[_n++] = new Reading(e, apostrophe: true, raw: false);
            _r[_n++] = new Reading(e, apostrophe: true, raw: true);
            _r[_n++] = new Reading(e, apostrophe: false, raw: false);
            _r[_n++] = new Reading(e, apostrophe: false, raw: true);
            _r[_n++] = new Reading(e, apostrophe: true, raw: true, structured: true);
            _structured = true;
        }
        if (rules is StringRules.Both or StringRules.Literal or StringRules.PowerShell)
        {
            var l = rules == StringRules.PowerShell ? rules : StringRules.Literal;
            _unspliced = 0b11 << _n;
            _r[_n++] = new Reading(l, apostrophe: true, raw: false);
            _r[_n++] = new Reading(l, apostrophe: false, raw: false);
        }
    }

    /// <summary>The scan is in a string, after the characters stepped over.</summary>
    public readonly bool InString => _inString != 0;

    /// <summary>A run of spacing here is kept (as one space): in a string, between a <c>#define</c>'s name and what
    /// follows it (<c>F(x)</c> is a function-like macro, <c>F (x)</c> one that expands to <c>(x)</c>), or inside an
    /// <c>#include</c>'s <c>&lt;…&gt;</c> (another file name).</summary>
    public readonly bool KeepsRun => _inString != 0 || _pp is Pp.AfterName or Pp.InAngle;

    /// <summary><see cref="KeepsRun"/> but in a string only by a reading whose backslashes are text (one a splice
    /// doesn't join to the next line).</summary>
    public readonly bool KeepsRunUnspliced => (_inString & _unspliced) != 0 || _pp is Pp.AfterName or Pp.InAngle;

    /// <summary>Where a line is, read as a preprocessor directive.</summary>
    private enum Pp : byte { LineStart, Hash, Directive, BeforeName, Name, AfterName, BeforeAngle, InAngle, None }

    private static readonly ulong Define = Packed("define"), Include = Packed("include"), Import = Packed("import");

    private static ulong Packed(string s)
    {
        ulong w = 0;
        foreach (var c in s) w = (w << 8) | c;
        return w;
    }

    /// <summary>The directive just ended: what comes after it.</summary>
    private readonly Pp AfterDirective(int c)
        => _directive == Define ? (c == -2 ? Pp.BeforeName : Pp.None)
         : _directive == Include || _directive == Import ? (c == '<' ? Pp.InAngle : c == -2 ? Pp.BeforeAngle : Pp.None)
         : Pp.None;

    /// <summary>One character's step through a preprocessor line (-2: a run of spacing).</summary>
    private void PpStep(int c)
    {
        if (c is '\n' or '\r') { _pp = Pp.LineStart; return; }
        switch (_pp)
        {
            case Pp.None: return;
            case Pp.LineStart: _pp = c == '#' ? Pp.Hash : c == -2 ? Pp.LineStart : Pp.None; return;
            case Pp.Hash:
                if (c == -2) return;
                _pp = c is (>= 'a' and <= 'z') ? Pp.Directive : Pp.None;
                _directive = (ulong)c;
                return;
            case Pp.Directive:
                if (c is (>= 'a' and <= 'z') or '_') { _directive = _directive >> 56 != 0 ? ulong.MaxValue : (_directive << 8) | (uint)c; return; }
                _pp = AfterDirective(c);
                return;
            case Pp.BeforeName: _pp = c == -2 ? Pp.BeforeName : TextInspector.IsWordChar(c) && c is not (>= '0' and <= '9') ? Pp.Name : Pp.None; return;
            case Pp.Name: _pp = c == -2 ? Pp.AfterName : TextInspector.IsWordChar(c) ? Pp.Name : Pp.None; return;
            case Pp.AfterName: _pp = c == -2 ? Pp.AfterName : Pp.None; return;
            case Pp.BeforeAngle: _pp = c == -2 ? Pp.BeforeAngle : c == '<' ? Pp.InAngle : Pp.None; return;
            case Pp.InAngle: if (c == '>') _pp = Pp.None; return;
        }
    }

    /// <summary>Steps over one character (-1: the end; a line ending is '\n' or '\r'): whether it is escaped (by any
    /// reading).</summary>
    public bool Step(int c)
    {
        bool escaped = _escapeNext != 0;
        ulong tail = _tail;
        _tail = (tail << 8) | Tail(c);
        if ((_pp != Pp.None || c is '\n' or '\r') && !(escaped && c is '\n' or '\r')) PpStep(TextInspector.IsHorizontalSpace(c) ? -2 : c);
        if (TextInspector.IsHorizontalSpace(c))
        {
            // Spacing never puts a scan in or out of a string, nor makes a quote verbatim: only what is kept does. An
            // escaped space is a character, after which a verbatim string's closing quote is no longer half of "".
            _closedVerbatim &= ~_escapeNext;
            _escapeNext = 0;
            if (_everyChar != 0) Spacing();
            return escaped;
        }
        if (_everyChar == 0 && c is not ('"' or '\'' or '`' or '\\' or '\n' or '\r') && !(_structured && c is '/' or '*'))
        {
            // Nothing any reading treats specially (most of a file): the same step for all of them.
            _escapeNext = _closedVerbatim = 0;
            _p2 = _p1;
            _p1 = c;
            return escaped;
        }
        int next = 0, closed = 0, inString = 0, every = 0;
        for (int i = 0; i < _n; i++)
        {
            ref var r = ref _r[i];
            int bit = 1 << i;
            bool e = (_escapeNext & bit) != 0;
            if (!e && r.Escapes(c)) next |= bit;
            if (r.Structured)
            {
                if (Structured(ref r, c, e, tail, (_closedVerbatim & bit) != 0)) closed |= bit;
                if (r.Quote != '\0') inString |= bit;
                if (r.Mode is Mode.RawHead or Mode.Triple) every |= bit;
                continue;
            }
            if (c is '\n' or '\r')
            {
                if (!e) // an escaped one (a splice) goes on with the line, and its string
                {
                    r.Quote = '\0';
                    r.Verbatim = false;
                }
            }
            else if (!e && r.IsQuote(c))
            {
                if (r.Quote == '\0')
                {
                    // C#: @"…", $@"…", @$"…", and the second quote of "" in a verbatim string, which reopens it.
                    r.Verbatim = (r.Rules == StringRules.CSharp && c == '"' && ((_closedVerbatim & bit) != 0 || _p1 == '@' || (_p1 == '$' && _p2 == '@')))
                                 || (r.Raw && Raw(r.Rules, c));
                    r.Quote = (char)c;
                }
                else if (r.Quote == c)
                {
                    r.Quote = '\0';
                    if (r.Verbatim) closed |= bit;
                    r.Verbatim = false;
                }
            }
            if (r.Quote != '\0') inString |= bit;
        }
        _escapeNext = next;
        _closedVerbatim = closed;
        _inString = inString;
        _everyChar = every;
        _p2 = _p1;
        _p1 = c;
        return escaped;
    }

    /// <summary>A character as <see cref="_tail"/> holds it: ASCII as itself, anything else as 0xFF, so a UTF-16 char
    /// (whole) and its UTF-8 bytes (streamed) never match a quote, a parenthesis or a raw string's delimiter.</summary>
    private static uint Tail(int c) => c is >= 0 and < 0x80 ? (uint)c : 0xFF;

    /// <summary>The low <paramref name="n"/> bytes of a <see cref="_tail"/>.</summary>
    private static ulong Mask(int n) => n >= 8 ? ulong.MaxValue : (1UL << (8 * n)) - 1;

    /// <summary>Spacing, for the readings that need every character: a raw string's head isn't one (a raw string to
    /// its quote), a triple's closing quotes aren't three in a row.</summary>
    private void Spacing()
    {
        for (int i = 0; i < _n; i++)
        {
            ref var r = ref _r[i];
            if (r.Mode == Mode.RawHead) r.Mode = Mode.None;
            else if (r.Mode == Mode.Triple) r.Run = 0;
        }
        _everyChar = 0;
        for (int i = 0; i < _n; i++)
            if (_r[i].Mode == Mode.Triple) _everyChar |= 1 << i;
    }

    /// <summary>
    /// The structured reading's step over <paramref name="c"/> (not spacing; <paramref name="e"/> escaped,
    /// <paramref name="tail"/> what came before it): a comment opens no string, a C++ raw string (<c>R"delim(…)delim"</c>)
    /// closes only at its delimiter, a <c>"""</c> or <c>'''</c> string only at the same three quotes. Whether a C#
    /// verbatim string closed (<paramref name="closedVerbatim"/>: the last step closed one, so this quote is half of "").
    /// </summary>
    private readonly bool Structured(ref Reading r, int c, bool e, ulong tail, bool closedVerbatim)
    {
        int prev = (int)(tail & 0xFF), prev2 = (int)(tail >> 8 & 0xFF);
        bool justOpened = r.JustOpened;
        r.JustOpened = false;
        if (c is '\n' or '\r')
        {
            if (!e) // an escaped one (a splice) goes on with the line: its string, its comment
            {
                r.Quote = '\0';
                r.Verbatim = false;
                r.Mode = Mode.None;
                r.Empty = false;
            }
            return false;
        }
        switch (r.Mode)
        {
            case Mode.LineComment:
                return false;
            case Mode.BlockComment:
                if (c == '/' && prev == '*' && !justOpened) r.Mode = Mode.None; // "/*/" is no comment's end
                return false;
            case Mode.RawHead:
                // R"delim( : the delimiter, up to the parenthesis; anything else (r"abc", Python's) is a raw string to its quote.
                if (c == '(') r.Mode = r.DelimLen < 8 ? Mode.RawDelim : Mode.None; // ')' and the delimiter fit a tail
                else if (c == r.Quote) Close(ref r, prev);
                else if (c is ')' or '\\' or < 0x21 or >= 0x7F || r.DelimLen >= 16) r.Mode = Mode.None;
                else
                {
                    r.Want = (r.Want << 8) | (uint)c;
                    r.DelimLen++;
                }
                return false;
            case Mode.RawDelim:
                if (c == '"' && ((tail ^ r.Want) & Mask(r.DelimLen + 1)) == 0) Close(ref r, -1);
                return false;
            case Mode.Triple:
                if (c == r.Quote && !e)
                {
                    if (++r.Run == 3) Close(ref r, -1);
                }
                else r.Run = 0;
                return false;
        }
        if (r.Quote == '\0')
        {
            if (c == '/' && prev == '/') { r.Mode = Mode.LineComment; return false; }
            if (c == '*' && prev == '/') { r.Mode = Mode.BlockComment; r.JustOpened = true; return false; }
            if (e || !r.IsQuote(c)) return false;
            if (r.Empty && prev == c && prev2 == c && c is '"' or '\'')
            {
                // The third quote of """ (or '''): the two before it were an empty string. Raw in C#, and after an r.
                r.Mode = Mode.Triple;
                r.Run = 0;
                r.Quote = (char)c;
                r.Verbatim = r.Rules == StringRules.CSharp || (tail >> 16 & 0xFF) is 'r' or 'R';
                r.Empty = false;
                return false;
            }
            r.Empty = false;
            r.Quote = (char)c;
            r.Verbatim = (r.Rules == StringRules.CSharp && c == '"' && (closedVerbatim || _p1 == '@' || (_p1 == '$' && _p2 == '@')))
                         || Raw(r.Rules, c);
            if (r.Verbatim && r.Rules != StringRules.CSharp && c == '"' && _p1 is 'r' or 'R')
            {
                r.Mode = Mode.RawHead;
                r.Want = ')';
                r.DelimLen = 0;
            }
            return false;
        }
        if (e || c != r.Quote) return false;
        bool verbatim = r.Verbatim;
        Close(ref r, prev);
        return verbatim;
    }

    /// <summary>A string's closing quote: an empty one (<paramref name="prev"/> the opening quote) may be the first two of three.</summary>
    private static void Close(ref Reading r, int prev)
    {
        r.Empty = prev == r.Quote;
        r.Quote = '\0';
        r.Verbatim = false;
        r.Mode = Mode.None;
    }

    /// <summary>What the structured reading is in, beyond a plain string.</summary>
    private enum Mode : byte { None, LineComment, BlockComment, RawHead, RawDelim, Triple }

    /// <summary>Whether a string opening at quote <paramref name="c"/> is raw (its backslashes text): C#'s third quote
    /// of <c>"""</c>; else a backtick (Go), or a quote after an <c>r</c>/<c>R</c> prefix (<c>r"…"</c>, <c>R"(…)"</c>,
    /// <c>br"…"</c>, <c>u8R"…"</c>) or after <c>r#</c> (Rust).</summary>
    private readonly bool Raw(StringRules rules, int c) => rules == StringRules.CSharp
        ? c == '"' && _p1 == '"' && _p2 == '"'
        : c == '`'
          || (_p1 is 'r' or 'R' && !(TextInspector.IsWordChar(_p2) && _p2 is not ('b' or 'B' or 'u' or 'U' or 'L' or '8' or 'f' or 'F')))
          || (_p1 == '#' && _p2 is 'r' or '#');

    [InlineArray(8)]
    private struct Readings
    {
        private Reading _first;
    }

    private struct Reading(StringRules rules, bool apostrophe, bool raw, bool structured = false)
    {
        public readonly StringRules Rules = rules;
        public readonly bool Raw = raw;
        public readonly bool Structured = structured; // reads comments, raw strings' delimiters and triple quotes
        public char Quote;    // the quote of the string the reading is in, or '\0'
        public bool Verbatim; // that string's backslashes are text
        public Mode Mode;     // structured: a comment, a raw string's head or delimiter, a triple-quoted string
        public byte Run, DelimLen; // a triple's closing quotes so far; a raw string's delimiter's length
        public bool Empty, JustOpened; // the last string closed was ""; a block comment opened at the last character
        public ulong Want;    // a raw string's ')' and delimiter, a byte each (StringScan.Tail)

        /// <summary>Whether <paramref name="c"/>, not itself escaped, escapes the next character.</summary>
        public readonly bool Escapes(int c) => Rules switch
        {
            StringRules.Escape or StringRules.CSharp => c == '\\' && !Verbatim,
            StringRules.PowerShell => c == '`' && Quote != '\'',
            _ => false,
        };

        /// <summary>Whether <paramref name="c"/>, not escaped, opens or closes a string.</summary>
        public readonly bool IsQuote(int c) => c == '"' || (c == '\'' && apostrophe) || (c == '`' && Rules != StringRules.PowerShell);
    }
}
