namespace CodeDiffer.Core.Model;

/// <summary>
/// One path's verdict in a 2-way compare. <see cref="Reason"/> is set only for Modified.
/// For a <see cref="ChangeStatus.Renamed"/>, <see cref="RelativePath"/> is the destination (the "to"
/// path), <see cref="RenamedFrom"/> is the source, and <see cref="SimilarityMilli"/> is round(sim*1000).
/// </summary>
public sealed record FileChange(
    string RelativePath,
    ChangeStatus Status,
    ChangeReason? Reason,
    long LeftSize,
    long RightSize,
    string? RenamedFrom = null,
    int? SimilarityMilli = null);

/// <summary>
/// The result of a 2-way directory compare: every path's status (changes sorted by path, ordinal, so
/// the output is deterministic — a diff of two reports is a real change). This is the status+reason
/// layer (Tiers 0–2); per-file hunks are produced lazily on top of it.
/// </summary>
public sealed class CompareReport
{
    public IReadOnlyList<FileChange> Changes { get; }

    public CompareReport(IReadOnlyList<FileChange> changes) => Changes = changes;

    public int Total => Changes.Count;

    public int Count(ChangeStatus status) => Changes.Count(c => c.Status == status);

    public int ReasonCount(ChangeReason reason) => Changes.Count(c => c.Reason == reason);
}
