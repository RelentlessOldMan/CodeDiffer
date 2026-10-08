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

        if (TextInspector.LooksBinary(Head(leftBytes)) || TextInspector.LooksBinary(Head(rightBytes)))
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

    /// <summary>The same precedence on a large file, by streamed byte comparison (UTF-8 or a single-byte code page).</summary>
    private static ChangeReason Streamed(string leftPath, byte[] leftHead, string rightPath, byte[] rightHead, CancellationToken ct)
    {
        if (Wide(leftHead) || Wide(rightHead)) return ChangeReason.Content;
        int l = Utf8Bom(leftHead) ? 3 : 0, r = Utf8Bom(rightHead) ? 3 : 0;
        if (l != r && Same(leftPath, l, rightPath, r, Norm.None, ct)) return ChangeReason.Encoding;
        // Whitespace-normalized first: it is implied by eol-equality, and a content change ends it at the first difference.
        if (!Same(leftPath, l, rightPath, r, Norm.Whitespace, ct)) return ChangeReason.Content;
        return Same(leftPath, l, rightPath, r, Norm.Eol, ct) ? ChangeReason.Eol : ChangeReason.Whitespace;
    }

    private static bool Utf8Bom(byte[] h) => h is [0xEF, 0xBB, 0xBF, ..];
    private static bool Wide(byte[] h) => h is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0x00, 0x00, 0xFE, 0xFF, ..];

    private static bool Same(string a, int skipA, string b, int skipB, Norm norm, CancellationToken ct)
    {
        using var fa = new Feed(a, skipA, ct);
        using var fb = new Feed(b, skipB, ct);
        while (true)
        {
            int x = Next(fa, norm), y = Next(fb, norm);
            if (x != y) return false;
            if (x < 0) return true;
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
                    if (f.LineStart || !TextInspector.IsWordChar(f.Last) || !TextInspector.IsWordChar(f.Peek())) continue;
                    return ' ';
                case Norm.Whitespace or Norm.Eol when c == '\r':
                    if (f.Peek() == '\n') f.Next();
                    f.LineStart = true;
                    return '\n';
                default:
                    f.LineStart = c == '\n';
                    f.Last = c;
                    return c;
            }
        }
    }

    /// <summary>A file's bytes one at a time (-1 at the end), buffered.</summary>
    private sealed class Feed : IDisposable
    {
        private readonly FileStream _fs;
        private readonly CancellationToken _ct;
        private readonly byte[] _buf = new byte[1 << 16];
        private int _n, _i;

        /// <summary>Nothing but whitespace since the last line ending (or the start).</summary>
        public bool LineStart = true;

        /// <summary>The last byte returned that wasn't whitespace (whitespace normalization).</summary>
        public int Last = -1;

        public Feed(string path, int skip, CancellationToken ct)
        {
            _ct = ct;
            _fs = TextInspector.OpenShared(path, 1);
            _fs.Position = skip;
        }

        public int Peek() => _i < _n || Fill() ? _buf[_i] : -1;

        public int Next() => _i < _n || Fill() ? _buf[_i++] : -1;

        private bool Fill()
        {
            _ct.ThrowIfCancellationRequested();
            _n = _fs.Read(_buf, 0, _buf.Length);
            _i = 0;
            return _n > 0;
        }

        public void Dispose() => _fs.Dispose();
    }
}
