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
    private readonly long _maxClassifyBytes, _maxStreamBytes, _wholeBudget;
    private readonly object _gate = new();
    private long _held;

    /// <param name="wholeBudgetBytes">How many bytes of files read whole may be classified at once, both sides counted
    /// (a pair larger than it alone). A pair read whole costs some twenty times its size while classified (its text
    /// decoded, normalized and stripped of each kind of noise in turn): 64 workers on 7 MB pairs held 2.9 GB.</param>
    public ReasonClassifier(long maxClassifyBytes = 8L * 1024 * 1024, long maxStreamBytes = 64L * 1024 * 1024,
        long wholeBudgetBytes = 64L * 1024 * 1024)
    {
        _maxClassifyBytes = maxClassifyBytes;
        _maxStreamBytes = maxStreamBytes;
        _wholeBudget = Math.Max(1, wholeBudgetBytes);
    }

    /// <summary>The most bytes of files read whole that were being classified at once.</summary>
    internal long PeakHeld { get; private set; }

    /// <summary>A share of <see cref="_wholeBudget"/> for a pair of <paramref name="bytes"/>, waited for while others hold it.</summary>
    private Hold Take(long bytes, CancellationToken ct)
    {
        bytes = Math.Clamp(bytes, 0, _wholeBudget);
        lock (_gate)
        {
            if (_held > 0 && _held + bytes > _wholeBudget) Sessions.ProcessTally.Stat("classify: waited for memory");
            while (_held > 0 && _held + bytes > _wholeBudget)
            {
                ct.ThrowIfCancellationRequested();
                Monitor.Wait(_gate, 200);
            }
            _held += bytes;
            if (_held > PeakHeld) PeakHeld = _held;
        }
        return new Hold(this, bytes);
    }

    private readonly struct Hold(ReasonClassifier owner, long bytes) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._held -= bytes;
                Monitor.PulseAll(owner._gate);
            }
        }
    }

    /// <param name="ct">Checked per 64 KB read when streamed (up to three passes over 64 MB), and before reading.</param>
    public ChangeReason Classify(string leftPath, long leftLength, string rightPath, long rightLength, CancellationToken ct = default)
        => Classify(leftPath, leftLength, rightPath, rightLength, out _, ct);

    /// <param name="noise">What a <c>content</c> change is only, when it is noise (<see cref="NoiseFinder"/>): found for
    /// a pair read whole, else none.</param>
    public ChangeReason Classify(string leftPath, long leftLength, string rightPath, long rightLength, out ChangeNoise noise, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        noise = ChangeNoise.None;
        // One open per side: a small file is read whole (its head is a prefix of it); a large one only its head.
        bool large = leftLength > _maxClassifyBytes || rightLength > _maxClassifyBytes;
        if (large)
            return Large(leftPath, leftLength, TextInspector.ReadHead(leftPath), rightPath, rightLength, TextInspector.ReadHead(rightPath), ct);
        using var hold = Take(leftLength + rightLength, ct);
        return Whole(leftPath, TextInspector.ReadAll(leftPath), rightPath, TextInspector.ReadAll(rightPath), out noise);
    }

    /// <summary>
    /// The reason for a pair the listing says changed size — or null when the two are byte for byte the same after all
    /// (a directory entry can keep a stale size: a file rewritten through a hard link elsewhere), never "same text,
    /// different bytes". A pair read whole costs nothing more; a large one an open per side for its length, and a read
    /// only when those agree. <paramref name="sameLength"/>: their length, when null.
    /// </summary>
    public ChangeReason? ClassifyResized(string leftPath, long leftLength, string rightPath, long rightLength, out long sameLength, CancellationToken ct = default)
        => ClassifyResized(leftPath, leftLength, rightPath, rightLength, out sameLength, out _, ct);

    public ChangeReason? ClassifyResized(string leftPath, long leftLength, string rightPath, long rightLength, out long sameLength, out ChangeNoise noise,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        noise = ChangeNoise.None;
        if (leftLength > _maxClassifyBytes || rightLength > _maxClassifyBytes)
            return SameBytes(leftPath, rightPath, out sameLength, ct) ? null : Classify(leftPath, leftLength, rightPath, rightLength, out noise, ct);
        using var hold = Take(leftLength + rightLength, ct);
        var leftBytes = TextInspector.ReadAll(leftPath);
        var rightBytes = TextInspector.ReadAll(rightPath);
        sameLength = leftBytes.Length;
        return leftBytes.AsSpan().SequenceEqual(rightBytes) ? null : Held(leftPath, leftBytes, rightPath, rightBytes, out noise, ct);
    }

    /// <summary>Whether two files hold the same bytes, by their handles' lengths and then their bytes.</summary>
    private static bool SameBytes(string leftPath, string rightPath, out long length, CancellationToken ct)
    {
        using var a = TextInspector.OpenShared(leftPath, 1);
        using var b = TextInspector.OpenShared(rightPath, 1);
        length = a.Length;
        if (a.Length != b.Length) return false;
        var x = new byte[64 * 1024];
        var y = new byte[64 * 1024];
        for (int n; (n = a.ReadAtLeast(x, x.Length, throwOnEndOfStream: false)) > 0;)
        {
            ct.ThrowIfCancellationRequested();
            if (b.ReadAtLeast(y, n, throwOnEndOfStream: false) != n || !x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n))) return false;
        }
        return b.ReadByte() < 0;
    }

    /// <summary>The reason, from both files' whole bytes as just read (one open fewer per side); bytes past the
    /// whole-read size are read again by path.</summary>
    public ChangeReason Classify(string leftPath, byte[] leftBytes, string rightPath, byte[] rightBytes, CancellationToken ct = default)
        => Classify(leftPath, leftBytes, rightPath, rightBytes, out _, ct);

    public ChangeReason Classify(string leftPath, byte[] leftBytes, string rightPath, byte[] rightBytes, out ChangeNoise noise, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        noise = ChangeNoise.None;
        if (leftBytes.Length > _maxClassifyBytes || rightBytes.Length > _maxClassifyBytes)
            return Classify(leftPath, leftBytes.Length, rightPath, rightBytes.Length, out noise, ct);
        using var hold = Take((long)leftBytes.Length + rightBytes.Length, ct);
        return Whole(leftPath, leftBytes, rightPath, rightBytes, out noise);
    }

    /// <summary><see cref="Classify(string, byte[], string, byte[], out ChangeNoise, CancellationToken)"/> for bytes
    /// read under a hold already taken.</summary>
    private ChangeReason Held(string leftPath, byte[] leftBytes, string rightPath, byte[] rightBytes, out ChangeNoise noise, CancellationToken ct)
    {
        noise = ChangeNoise.None;
        return leftBytes.Length > _maxClassifyBytes || rightBytes.Length > _maxClassifyBytes
            ? Classify(leftPath, leftBytes.Length, rightPath, rightBytes.Length, out noise, ct)
            : Whole(leftPath, leftBytes, rightPath, rightBytes, out noise);
    }

    /// <summary>A side past the whole-read size: only the heads are at hand.</summary>
    private ChangeReason Large(string leftPath, long leftLength, byte[] leftBytes, string rightPath, long rightLength, byte[] rightBytes, CancellationToken ct)
    {
        if (TextInspector.LooksBinary(leftBytes) || TextInspector.LooksBinary(rightBytes))
            return ChangeReason.Binary;
        // Read by its head but small enough for a patch to line-diff it whole: a NUL further in is binary there, so here.
        if (Math.Max(leftLength, rightLength) <= TextInspector.WholeSniffBytes
                  && (TextInspector.NulPast(leftPath, leftBytes, ct) || TextInspector.NulPast(rightPath, rightBytes, ct)))
            return ChangeReason.Binary;

        // Strings are read as the files' language reads them: a backslash an escape or text (both ways if the two differ).
        var rules = TextInspector.RulesFor(leftPath, rightPath);
        return Math.Max(leftLength, rightLength) <= _maxStreamBytes ? Streamed(leftPath, leftBytes, rightPath, rightBytes, rules, ct) : ChangeReason.Content;
    }

    /// <summary>Both files read whole.</summary>
    private static ChangeReason Whole(string leftPath, byte[] leftBytes, string rightPath, byte[] rightBytes, out ChangeNoise noise)
    {
        noise = ChangeNoise.None;
        // Read whole, the whole file is sniffed: text that turns binary past 8 KB is binary, never a text diff with NULs.
        if (TextInspector.LooksBinary(leftBytes) || TextInspector.LooksBinary(rightBytes))
            return ChangeReason.Binary;
        // Strings are read as the files' language reads them: a backslash an escape or text (both ways if the two differ).
        var rules = TextInspector.RulesFor(leftPath, rightPath);
        var leftText = TextInspector.Decode(leftBytes);
        var rightText = TextInspector.Decode(rightBytes);

        if (string.Equals(leftText, rightText, StringComparison.Ordinal))
            // Same text, different bytes — unless one side is a legacy file whose 0x80–0x9F bytes were read as Latin-1
            // controls: as cp1252 they are other characters, and the "same text" a byte-for-byte conversion.
            return TextInspector.LegacyC1(leftBytes) != TextInspector.LegacyC1(rightBytes) ? ChangeReason.Content : ChangeReason.Encoding;

        var leftEol = TextInspector.NormalizeEol(leftText);
        var rightEol = TextInspector.NormalizeEol(rightText);
        if (string.Equals(leftEol, rightEol, StringComparison.Ordinal))
            return ChangeReason.Eol;

        if (TextInspector.SameButWhitespace(leftEol, rightEol, rules) && TextInspector.EndsInBlankLine(leftEol) == TextInspector.EndsInBlankLine(rightEol))
            return ChangeReason.Whitespace;

        noise = NoiseFinder.Find(leftPath, rightPath, leftEol, rightEol, rules);
        return ChangeReason.Content;
    }

    private static ReadOnlySpan<byte> Head(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, TextInspector.HeadBytes));

    /// <summary>How a comparison reads both sides: as they are, whitespace-normalized (<see cref="TextInspector.SameButWhitespace"/>),
    /// or EOL-normalized.</summary>
    private enum Norm { None, Whitespace, Eol }

    /// <summary>The same precedence on a large file, by streamed comparison (UTF-8 or a single-byte code page). Read
    /// byte for byte (Latin-1, one char a byte) — the same verdict as decoding, while both sides decode alike; when a
    /// difference is found and one side is UTF-8 with the other not, again decoding each as a whole read does (a
    /// cp1252 file re-saved as UTF-8 is the same text: <c>encoding</c>, at any size).</summary>
    private static ChangeReason Streamed(string leftPath, byte[] leftHead, string rightPath, byte[] rightHead, StringRules rules, CancellationToken ct)
    {
        if (Wide(leftHead) || Wide(rightHead)) return ChangeReason.Content;
        int l = Utf8Bom(leftHead) ? 3 : 0, r = Utf8Bom(rightHead) ? 3 : 0;
        var reason = Verdict(leftPath, l, Encoding.Latin1, rightPath, r, Encoding.Latin1, l != r, rules, ct, out bool decodingMatters);
        // ASCII up to the first difference, and an ASCII character on one side of it: any decoding of either side reads
        // the same there, so content it is — without reading both files whole to learn whether each is UTF-8.
        if (reason != ChangeReason.Content || !decodingMatters) return reason;
        bool lu = IsUtf8(leftPath, l, ct), ru = IsUtf8(rightPath, r, ct);
        if (lu == ru) return reason;
        reason = Verdict(leftPath, l, lu ? Utf8Strict : Encoding.Latin1, rightPath, r, ru ? Utf8Strict : Encoding.Latin1, true, rules, ct, out _);
        // The legacy side's 0x80–0x9F bytes are cp1252 punctuation, not the Latin-1 controls that read as the same text.
        return reason == ChangeReason.Encoding && HasC1(lu ? rightPath : leftPath, lu ? r : l, ct) ? ChangeReason.Content : reason;
    }

    /// <summary>Whether a file past <paramref name="skip"/> bytes holds a byte in 0x80–0x9F, streamed.</summary>
    private static bool HasC1(string path, int skip, CancellationToken ct)
    {
        using var fs = TextInspector.OpenShared(path, 1);
        fs.Position = skip;
        var buf = new byte[1 << 16];
        for (int n; (n = fs.Read(buf, 0, buf.Length)) > 0;)
        {
            ct.ThrowIfCancellationRequested();
            if (buf.AsSpan(0, n).IndexOfAnyInRange((byte)0x80, (byte)0x9F) >= 0) return true;
        }
        return false;
    }

    private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);

    /// <param name="decodingMatters">On <c>content</c>: whether another decoding of either side could read differently
    /// up to the first difference (a non-ASCII byte before it, or on both sides of it).</param>
    private static ChangeReason Verdict(string a, int skipA, Encoding encA, string b, int skipB, Encoding encB, bool encodingMayDiffer,
        StringRules rules, CancellationToken ct, out bool decodingMatters)
    {
        decodingMatters = true;
        if (encodingMayDiffer && Same(a, skipA, encA, b, skipB, encB, Norm.None, rules, ct, out _)) return ChangeReason.Encoding;
        // Whitespace-normalized first: it is implied by eol-equality, and a content change ends it at the first difference.
        if (!Same(a, skipA, encA, b, skipB, encB, Norm.Whitespace, rules, ct, out decodingMatters)) return ChangeReason.Content;
        return Same(a, skipA, encA, b, skipB, encB, Norm.Eol, rules, ct, out _) ? ChangeReason.Eol : ChangeReason.Whitespace;
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

    private static bool Same(string a, int skipA, Encoding encA, string b, int skipB, Encoding encB, Norm norm, StringRules rules,
        CancellationToken ct, out bool decodingMatters)
    {
        using var fa = new Feed(a, skipA, encA, rules, ct);
        using var fb = new Feed(b, skipB, encB, rules, ct);
        while (true)
        {
            int x = Next(fa, norm), y = Next(fb, norm);
            if (x != y)
            {
                decodingMatters = fa.HighBefore || fb.HighBefore || (x >= 0x80 && y >= 0x80);
                return false;
            }
            if (x < 0)
            {
                decodingMatters = false;
                return fa.BlankTail == fb.BlankTail; // a whitespace-only last line without a line ending is a line
            }
        }
    }

    private static int Next(Feed f, Norm norm)
    {
        int c = ReadNormalized(f, norm);
        if (norm == Norm.Whitespace) (f.Number, f.Key) = (TextInspector.InNumber(f.Number, f.Key, c), c);
        return c;
    }

    private static int ReadNormalized(Feed f, Norm norm)
    {
        while (true)
        {
            int c = f.Next();
            if (norm == Norm.Whitespace)
            {
                // A splice, as TextInspector.WhitespaceKey reads one: the spacing either side of it is one run.
                if (f.Spliced && !TextInspector.IsHorizontalSpace(c))
                {
                    f.Spliced = false;
                    bool run = f.SpliceRun;
                    f.SpliceRun = false;
                    if (run && c is not ('\n' or '\r' or -1) && (f.Scan.KeepsRun || TextInspector.Separates(f.BeforeSplice, c))) { f.Unread(); return ' '; }
                }
                if (f.RunBeforeBackslash)
                {
                    f.RunBeforeBackslash = false;
                    bool keep = TextInspector.KeepsRunBeforeBackslash(f.Scan, f.Both, f.Last, f.Peek(), out bool splice);
                    f.SpliceRun = splice;
                    if (keep) { f.Unread(); return ' '; }
                }
            }
            bool escaped = norm == Norm.Whitespace && f.Scan.Step(c);
            switch (norm)
            {
                // TextInspector.WhitespaceKey, byte by byte (line endings normalized too).
                case Norm.Whitespace when TextInspector.IsHorizontalSpace(c):
                    while (TextInspector.IsHorizontalSpace(f.Peek())) f.Scan.Step(f.Next());
                    int after = f.Peek();
                    bool lineEnd = after is '\n' or '\r' or -1;
                    if (escaped)
                    {
                        if (lineEnd && !f.Splices) continue; // an escaped space is a character, but trailing whitespace still (TextInspector)
                        f.LineStart = false;
                        (f.Prev, f.Last) = (f.Last, c);
                        return c;
                    }
                    if (after == -1 && f.LineStart) f.BlankTail = true; // TextInspector.EndsInBlankLine
                    if (f.Spliced) { f.SpliceRun = true; continue; } // a continued line's indent
                    if (f.Splices && !f.LineStart && !lineEnd && after == '\\') { f.RunBeforeBackslash = true; continue; }
                    if (f.LineStart || lineEnd || (!f.Scan.KeepsRun && !TextInspector.Separates(f.Last, after, f.Number))) continue;
                    return ' ';
                case Norm.Whitespace or Norm.Eol when c == '\r':
                    if (f.Peek() == '\n') f.Next();
                    if (escaped && f.Splices && f.Last == '\\') (f.Spliced, f.BeforeSplice) = (true, f.Prev);
                    f.LineStart = true;
                    (f.Prev, f.Last) = (f.Last, '\n');
                    return '\n';
                default:
                    if (escaped && c == '\n' && f.Splices && f.Last == '\\') (f.Spliced, f.BeforeSplice) = (true, f.Prev);
                    f.LineStart = c == '\n';
                    (f.Prev, f.Last) = (f.Last, c);
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

        /// <summary>The last byte returned that wasn't whitespace (whitespace normalization), and the one before it.</summary>
        public int Last = -1, Prev = -1;

        /// <summary>The last byte returned, spaces too, and whether it is in a number (<see cref="TextInspector.InNumber"/>).</summary>
        public int Key = -1;
        public bool Number;

        /// <summary>A splice (whitespace normalization, <see cref="TextInspector.Splices"/>): a run held back before a
        /// backslash; just past one; spacing either side of it; the byte before its backslash.</summary>
        public readonly bool Splices, Both;
        public bool RunBeforeBackslash, Spliced, SpliceRun;
        public int BeforeSplice = -1;

        /// <summary>Whether the comparison is in a string on this line (whitespace normalization).</summary>
        public StringScan Scan;

        /// <summary>The file ended in a line of whitespace only, without a line ending (whitespace normalization).</summary>
        public bool BlankTail;

        public Feed(string path, int skip, Encoding encoding, StringRules rules, CancellationToken ct)
        {
            _ct = ct;
            Scan = new StringScan(rules);
            Splices = TextInspector.Splices(rules);
            Both = rules == StringRules.Both;
            var fs = TextInspector.OpenShared(path, 1);
            fs.Position = skip;
            _r = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
        }

        public int Peek() => _held ? _last : _i < _n || Fill() ? _buf[_i] : -1;

        /// <summary>The character <see cref="Next()"/> last returned is returned again by the next call.</summary>
        public void Unread() => _held = true;
        private bool _held, _heldHighBefore;
        private int _last;

        /// <summary>A non-ASCII character was read before the last one <see cref="Next()"/> returned.</summary>
        public bool HighBefore { get; private set; }
        private bool _high;

        public int Next()
        {
            if (_held)
            {
                _held = false;
                HighBefore = _heldHighBefore;
                return _last;
            }
            int c = _i < _n || Fill() ? _buf[_i++] : -1;
            HighBefore = _heldHighBefore = _high;
            _high |= c >= 0x80;
            return _last = c;
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
