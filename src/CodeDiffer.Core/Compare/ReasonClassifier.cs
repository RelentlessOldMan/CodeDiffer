using System.Text;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// Classifies WHY two files (already known to differ in bytes) are modified — the honesty-contract
/// reason. Precedence (first match wins):
///   1. <b>binary</b> — a NUL (no BOM) on EITHER side. Fixes the prototype's one-sided binary bug.
///   2. <b>encoding</b> — decoded text identical, bytes differ (UTF-8↔UTF-16 / BOM).
///   3. <b>eol</b> — equal after EOL normalization (LF↔CRLF). Never reported as "content with no diff".
///   4. <b>whitespace</b> — the same lines but for indentation, trailing whitespace and the width of inner runs of it
///      (<see cref="TextInspector.WhitespaceKey"/>).
///   5. <b>content</b> — a real textual change.
/// A file over <c>maxClassifyBytes</c> is not decoded whole: it is compared streamed, as bytes (BOM, whitespace and
/// line endings normalized byte by byte, by the same rule, stopping at the first real difference — so a content change costs only
/// the read up to it). Past <c>maxStreamBytes</c>, or in UTF-16/32, a difference is called content unchecked.
/// </summary>
public sealed class ReasonClassifier
{
    private readonly long _maxClassifyBytes, _maxStreamBytes;

    public ReasonClassifier(long maxClassifyBytes = 8L * 1024 * 1024, long maxStreamBytes = 64L * 1024 * 1024)
    {
        _maxClassifyBytes = maxClassifyBytes;
        _maxStreamBytes = maxStreamBytes;
    }

    /// <param name="ct">Checked per 64 KB read when streamed (up to three passes over 64 MB), and before reading.</param>
    public ChangeReason Classify(string leftPath, long leftLength, string rightPath, long rightLength, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // One open per side: a small file is read whole (its head is a prefix of it); a large one only its head.
        bool large = leftLength > _maxClassifyBytes || rightLength > _maxClassifyBytes;
        var leftBytes = large ? TextInspector.ReadHead(leftPath) : TextInspector.ReadAll(leftPath);
        var rightBytes = large ? TextInspector.ReadHead(rightPath) : TextInspector.ReadAll(rightPath);

        // Read whole, the whole file is sniffed: text that turns binary past 8 KB is binary, never a text diff with NULs.
        if (TextInspector.LooksBinary(leftBytes) || TextInspector.LooksBinary(rightBytes))
            return ChangeReason.Binary;

        if (large)
            return Math.Max(leftLength, rightLength) <= _maxStreamBytes ? Streamed(leftPath, leftBytes, rightPath, rightBytes, ct) : ChangeReason.Content;

        var leftText = TextInspector.Decode(leftBytes);
        var rightText = TextInspector.Decode(rightBytes);

        if (string.Equals(leftText, rightText, StringComparison.Ordinal))
            return ChangeReason.Encoding; // same text, different bytes

        var leftEol = TextInspector.NormalizeEol(leftText);
        var rightEol = TextInspector.NormalizeEol(rightText);
        if (string.Equals(leftEol, rightEol, StringComparison.Ordinal))
            return ChangeReason.Eol;

        if (string.Equals(TextInspector.WhitespaceKey(leftEol), TextInspector.WhitespaceKey(rightEol), StringComparison.Ordinal))
            return ChangeReason.Whitespace;

        return ChangeReason.Content;
    }

    private static ReadOnlySpan<byte> Head(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, TextInspector.HeadBytes));

    private enum Norm { None, Whitespace, Eol }

    /// <summary>The same precedence on a large file, by streamed comparison (UTF-8 or a single-byte code page). Read
    /// byte for byte (Latin-1, one char a byte) — the same verdict as decoding, while both sides decode alike; when a
    /// difference is found and one side is UTF-8 with the other not, again decoding each as a whole read does (a
    /// cp1252 file re-saved as UTF-8 is the same text: <c>encoding</c>, at any size).</summary>
    private static ChangeReason Streamed(string leftPath, byte[] leftHead, string rightPath, byte[] rightHead, CancellationToken ct)
    {
        if (Wide(leftHead) || Wide(rightHead)) return ChangeReason.Content;
        int l = Utf8Bom(leftHead) ? 3 : 0, r = Utf8Bom(rightHead) ? 3 : 0;
        var reason = Verdict(leftPath, l, Encoding.Latin1, rightPath, r, Encoding.Latin1, l != r, ct, out bool decodingMatters);
        // ASCII up to the first difference, and an ASCII character on one side of it: any decoding of either side reads
        // the same there, so content it is — without reading both files whole to learn whether each is UTF-8.
        if (reason != ChangeReason.Content || !decodingMatters) return reason;
        bool lu = IsUtf8(leftPath, l, ct), ru = IsUtf8(rightPath, r, ct);
        if (lu == ru) return reason;
        return Verdict(leftPath, l, lu ? Utf8Strict : Encoding.Latin1, rightPath, r, ru ? Utf8Strict : Encoding.Latin1, true, ct, out _);
    }

    private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);

    /// <param name="decodingMatters">On <c>content</c>: whether another decoding of either side could read differently
    /// up to the first difference (a non-ASCII byte before it, or on both sides of it).</param>
    private static ChangeReason Verdict(string a, int skipA, Encoding encA, string b, int skipB, Encoding encB, bool encodingMayDiffer, CancellationToken ct,
        out bool decodingMatters)
    {
        decodingMatters = true;
        if (encodingMayDiffer && Same(a, skipA, encA, b, skipB, encB, Norm.None, ct, out _)) return ChangeReason.Encoding;
        // Whitespace-normalized first: it is implied by eol-equality, and a content change ends it at the first difference.
        if (!Same(a, skipA, encA, b, skipB, encB, Norm.Whitespace, ct, out decodingMatters)) return ChangeReason.Content;
        return Same(a, skipA, encA, b, skipB, encB, Norm.Eol, ct, out _) ? ChangeReason.Eol : ChangeReason.Whitespace;
    }

    /// <summary>Whether a file past <paramref name="skip"/> bytes is valid UTF-8, streamed.</summary>
    private static bool IsUtf8(string path, int skip, CancellationToken ct)
    {
        using var fs = TextInspector.OpenShared(path, 1);
        fs.Position = skip;
        var d = Utf8Strict.GetDecoder();
        var buf = new byte[1 << 16];
        var chars = new char[(1 << 16) + 4];
        try
        {
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                d.GetChars(buf, 0, n, chars, 0, flush: false);
            }
            d.GetChars(buf, 0, 0, chars, 0, flush: true); // a sequence cut off at the end is invalid too
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    private static bool Utf8Bom(byte[] h) => h is [0xEF, 0xBB, 0xBF, ..];
    private static bool Wide(byte[] h) => h is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0x00, 0x00, 0xFE, 0xFF, ..];

    private static bool Same(string a, int skipA, Encoding encA, string b, int skipB, Encoding encB, Norm norm, CancellationToken ct,
        out bool decodingMatters)
    {
        using var fa = new Feed(a, skipA, encA, ct);
        using var fb = new Feed(b, skipB, encB, ct);
        while (true)
        {
            int x = Next(fa, norm), y = Next(fb, norm);
            if (x != y)
            {
                decodingMatters = fa.HighBefore || fb.HighBefore || (x >= 0x80 && y >= 0x80);
                return false;
            }
            if (x < 0) { decodingMatters = false; return true; }
        }
    }

    private static int Next(Feed f, Norm norm)
    {
        while (true)
        {
            int c = f.Next();
            switch (norm)
            {
                // TextInspector.WhitespaceKey, byte by byte (line endings normalized too).
                case Norm.Whitespace when TextInspector.IsHorizontalSpace(c):
                    while (TextInspector.IsHorizontalSpace(f.Peek())) f.Next();
                    int after = f.Peek();
                    if (f.LineStart || after is '\n' or '\r' or -1 || (f.Quote == '\0' && !TextInspector.Separates(f.Last, after))) continue;
                    return ' ';
                case Norm.Whitespace or Norm.Eol when c == '\r':
                    if (f.Peek() == '\n') f.Next();
                    f.LineStart = true;
                    f.Quote = '\0';
                    return '\n';
                default:
                    f.LineStart = c == '\n';
                    if (f.LineStart) f.Quote = '\0';
                    else if (TextInspector.IsQuote(c) && f.PrevRaw != '\\') f.Quote = TextInspector.Quote(f.Quote, c);
                    f.Last = c;
                    return c;
            }
        }
    }

    /// <summary>A file's characters one at a time (-1 at the end), buffered: Latin-1 is its bytes.</summary>
    private sealed class Feed : IDisposable
    {
        private readonly StreamReader _r;
        private readonly CancellationToken _ct;
        private readonly char[] _buf = new char[1 << 16];
        private int _n, _i;

        /// <summary>Nothing but whitespace since the last line ending (or the start).</summary>
        public bool LineStart = true;

        /// <summary>The last byte returned that wasn't whitespace (whitespace normalization).</summary>
        public int Last = -1;

        /// <summary>The quote of the string on this line the comparison is in, or '\0' (whitespace normalization).</summary>
        public char Quote;

        /// <summary>The character before the last one <see cref="Next()"/> returned (-1 at the start).</summary>
        public int PrevRaw { get; private set; } = -1;
        private int _lastRaw = -1;

        public Feed(string path, int skip, Encoding encoding, CancellationToken ct)
        {
            _ct = ct;
            var fs = TextInspector.OpenShared(path, 1);
            fs.Position = skip;
            _r = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
        }

        public int Peek() => _i < _n || Fill() ? _buf[_i] : -1;

        /// <summary>A non-ASCII character was read before the last one <see cref="Next()"/> returned.</summary>
        public bool HighBefore { get; private set; }
        private bool _high;

        public int Next()
        {
            int c = _i < _n || Fill() ? _buf[_i++] : -1;
            HighBefore = _high;
            _high |= c >= 0x80;
            PrevRaw = _lastRaw;
            _lastRaw = c;
            return c;
        }

        private bool Fill()
        {
            _ct.ThrowIfCancellationRequested();
            _n = _r.Read(_buf, 0, _buf.Length);
            _i = 0;
            return _n > 0;
        }

        public void Dispose() => _r.Dispose();
    }
}
