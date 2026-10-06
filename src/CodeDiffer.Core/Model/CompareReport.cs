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

    /// <summary>Directories that could not be listed on each side — a non-zero count means the report is
    /// INCOMPLETE there (missing files may be listing failures, not real removals). Never hidden.</summary>
    public int LeftDroppedDirectories { get; }
    public int RightDroppedDirectories { get; }

    /// <summary>Same-size pairs that needed a content verdict (the only ones that can cost a read).</summary>
    public int ComparedPairs { get; init; }
    /// <summary>File sides answered from a trusted hash ledger instead of being read (of them, via CodeCompass).</summary>
    public int CacheHits { get; init; }
    public int CodeCompassHits { get; init; }
    /// <summary>Left files whose content was already proven by the previous compare of the same left tree
    /// (compare3's second compare reusing the base): neither read nor stat-checked again.</summary>
    public int ReusedLeftFiles { get; init; }
    /// <summary>Files whose metadata moved WHILE being read (a live writer): compared as read, never cached.</summary>
    public int UnstableFiles { get; init; }
    /// <summary>Files hashed this run but modified too recently to trust yet: cached as pending, re-read next run.</summary>
    public int PendingFiles { get; init; }
    /// <summary>Content bytes read to reach the verdicts (an upper bound when early exit applies).</summary>
    public long BytesRead { get; init; }
    /// <summary>Wall time per compare phase, in order (for --timings).</summary>
    public IReadOnlyList<(string Phase, TimeSpan Elapsed)> Timings { get; init; } = [];

    public CompareReport(IReadOnlyList<FileChange> changes, int leftDroppedDirectories = 0, int rightDroppedDirectories = 0)
    {
        Changes = changes;
        LeftDroppedDirectories = leftDroppedDirectories;
        RightDroppedDirectories = rightDroppedDirectories;
    }

    public int Total => Changes.Count;

    public int Count(ChangeStatus status) => Changes.Count(c => c.Status == status);

    public int ReasonCount(ChangeReason reason) => Changes.Count(c => c.Reason == reason);
}
