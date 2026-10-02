namespace CodeDiffer.Core.Model;

/// <summary>
/// Per-path status in a 2-way compare. The canonical tokens are the lowercase enum names.
/// </summary>
public enum ChangeStatus
{
    Identical,
    Added,
    Removed,
    Modified,
    Renamed,
}

/// <summary>
/// Why a file is <see cref="ChangeStatus.Modified"/>. This is ground truth the CodeSpawner delta
/// contract labels and CodeDiffer's classifier must reproduce — the honesty contract forbids a
/// "modified" with no reason. Canonical tokens (used in the digest) are the lowercase names, except
/// <see cref="Eol"/> which serializes as "eol".
/// </summary>
public enum ChangeReason
{
    /// <summary>Textual content change (incl. line add/remove, which carry insert/delete hunks).</summary>
    Content,
    /// <summary>Line-ending-only change (LF↔CRLF); bytes differ, zero textual hunks.</summary>
    Eol,
    /// <summary>Whitespace-only change; hunks present but every change is whitespace.</summary>
    Whitespace,
    /// <summary>Encoding-only change (UTF-8↔UTF-16 / BOM); bytes differ, decoded text identical.</summary>
    Encoding,
    /// <summary>Change inside a binary file; reported with shas+sizes, no hunks (no byte-range truth v1).</summary>
    Binary,
    /// <summary>Content identical (oldSha==newSha), metadata differs. Opt-in; off by default.</summary>
    Metadata,
}

/// <summary>Unified-diff hunk operation.</summary>
public enum HunkOp
{
    Insert,
    Delete,
    Replace,
}

/// <summary>
/// Maps enums to/from the locked canonical tokens used in the delta manifest and the diffTruthSha
/// digest. Spellings are frozen — a casual rename breaks the cross-tool contract.
/// </summary>
public static class CanonicalTokens
{
    public static string Token(ChangeReason r) => r switch
    {
        ChangeReason.Content => "content",
        ChangeReason.Eol => "eol",
        ChangeReason.Whitespace => "whitespace",
        ChangeReason.Encoding => "encoding",
        ChangeReason.Binary => "binary",
        ChangeReason.Metadata => "metadata",
        _ => throw new ArgumentOutOfRangeException(nameof(r), r, null),
    };

    public static ChangeReason Reason(string token) => token switch
    {
        "content" => ChangeReason.Content,
        "eol" => ChangeReason.Eol,
        "whitespace" => ChangeReason.Whitespace,
        "encoding" => ChangeReason.Encoding,
        "binary" => ChangeReason.Binary,
        "metadata" => ChangeReason.Metadata,
        _ => throw new FormatException($"unknown reason token '{token}'"),
    };

    public static string Token(HunkOp op) => op switch
    {
        HunkOp.Insert => "insert",
        HunkOp.Delete => "delete",
        HunkOp.Replace => "replace",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };

    public static HunkOp Op(string token) => token switch
    {
        "insert" => HunkOp.Insert,
        "delete" => HunkOp.Delete,
        "replace" => HunkOp.Replace,
        _ => throw new FormatException($"unknown hunk op token '{token}'"),
    };
}

/// <summary>
/// An explicit unified-diff hunk, 1-based coordinates. insert ⇒ OldLines 0; delete ⇒ NewLines 0;
/// replace ⇒ both ≥ 1. Contiguous touched lines coalesce into one hunk.
/// </summary>
public readonly record struct Hunk(HunkOp Op, int OldStart, int OldLines, int NewStart, int NewLines);

/// <summary>
/// A compact run-rule hunk for giant files: expands deterministically to explicit 1-line replaces.
/// Touched lines = { RangeStart + k*Stride : k=0,1,… while ≤ RangeEnd }, 1-based inclusive, each a
/// PerHunk-length replace. See docs/diff-delta-contract.md.
/// </summary>
public readonly record struct RunHunk(HunkOp Op, int Stride, int RangeStart, int RangeEnd, int PerHunk)
{
    /// <summary>Materialize to explicit hunks (for patch-apply and equality against an explicit oracle).</summary>
    public IEnumerable<Hunk> Expand()
    {
        if (Stride <= 0) throw new InvalidOperationException("run-rule stride must be positive");
        for (long line = RangeStart; line <= RangeEnd; line += Stride)
        {
            int start = checked((int)line);
            yield return new Hunk(Op, start, PerHunk, start, PerHunk);
        }
    }
}

/// <summary>A modified file's full record: file-level metadata + its hunks (explicit and/or run-rule).</summary>
public sealed record FileDelta(
    string Path,
    ChangeReason Reason,
    string OldSha,
    string NewSha,
    long OldSize,
    long NewSize,
    IReadOnlyList<Hunk> Hunks,
    IReadOnlyList<RunHunk> RunHunks);

/// <summary>A rename/move op. SimilarityMilli = round(sim*1000), sim = commonLines/max(old,new) over EOL-normalized lines.</summary>
public sealed record RenameOp(string From, string To, int SimilarityMilli);

/// <summary>
/// The parsed CodeSpawner base→variant delta manifest (the consumer-side model). Assert
/// <see cref="ManifestVersion"/> == 1 before trusting it; independently reproduce <see cref="DiffTruthSha"/>.
/// </summary>
public sealed record DeltaManifest(
    int ManifestVersion,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<RenameOp> Renamed,
    IReadOnlyList<FileDelta> Modified,
    string? DiffTruthSha);
