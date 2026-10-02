using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// Classifies WHY two files (already known to differ in bytes) are modified — the honesty-contract
/// reason. Precedence (first match wins):
///   1. <b>binary</b> — a NUL (no BOM) on EITHER side. Fixes the prototype's one-sided binary bug.
///   2. (too large to decode cheaply ⇒ <b>content</b> — the giant-file path; block-level detail is lazy.)
///   3. <b>encoding</b> — decoded text identical, bytes differ (UTF-8↔UTF-16 / BOM).
///   4. <b>eol</b> — equal after EOL normalization (LF↔CRLF). Never reported as "content with no diff".
///   5. <b>whitespace</b> — equal after stripping whitespace.
///   6. <b>content</b> — a real textual change.
/// </summary>
public sealed class ReasonClassifier
{
    private readonly long _maxClassifyBytes;

    public ReasonClassifier(long maxClassifyBytes = 8L * 1024 * 1024) => _maxClassifyBytes = maxClassifyBytes;

    public ChangeReason Classify(string leftPath, long leftLength, string rightPath, long rightLength)
    {
        var leftHead = TextInspector.ReadHead(leftPath);
        var rightHead = TextInspector.ReadHead(rightPath);

        if (TextInspector.LooksBinary(leftHead) || TextInspector.LooksBinary(rightHead))
            return ChangeReason.Binary;

        // Too large to fully decode on the eager Tier-2 pass; bytes differ, so it's a content change.
        // The precise block-level picture is produced lazily on demand (see docs/OUTPUT.md §3).
        if (leftLength > _maxClassifyBytes || rightLength > _maxClassifyBytes)
            return ChangeReason.Content;

        var leftText = TextInspector.Decode(File.ReadAllBytes(leftPath));
        var rightText = TextInspector.Decode(File.ReadAllBytes(rightPath));

        if (string.Equals(leftText, rightText, StringComparison.Ordinal))
            return ChangeReason.Encoding; // same text, different bytes

        var leftEol = TextInspector.NormalizeEol(leftText);
        var rightEol = TextInspector.NormalizeEol(rightText);
        if (string.Equals(leftEol, rightEol, StringComparison.Ordinal))
            return ChangeReason.Eol;

        if (string.Equals(TextInspector.StripWhitespace(leftEol), TextInspector.StripWhitespace(rightEol), StringComparison.Ordinal))
            return ChangeReason.Whitespace;

        return ChangeReason.Content;
    }
}
