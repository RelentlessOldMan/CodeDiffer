using CodeDiffer.Core.Walk;

namespace CodeDiffer.Core.Model;

/// <summary>
/// One path's verdict in a 2-way compare. <see cref="Reason"/> is set only for Modified.
/// For a <see cref="ChangeStatus.Renamed"/>, <see cref="RelativePath"/> is the destination (the "to"
/// path), <see cref="RenamedFrom"/> is the source, and <see cref="SimilarityMilli"/> is round(sim*1000).
/// <see cref="Unreadable"/>: why a side could not be read during the compare (locked, vanished, access denied).
/// A pair that couldn't be read is listed as Modified with no reason — its verdict is unknown, so it is never
/// called identical; an add or remove that couldn't be read keeps its status but was not considered for a rename.
/// <see cref="EditedRename"/>: a rename found by line similarity — its bytes differ even when the similarity rounds
/// to 1000 (one line in 2,001 changed, lines reordered, line endings only). Only a rename without it is byte-identical.
/// <see cref="BehindLink"/>: an add or remove whose path is behind a symlink / junction the OTHER tree has there (not
/// followed), so whether it really changed is unknown — never acted on as a delete or an add (it is also Unreadable).
/// A name Windows can't open by path (a file, or a directory as one row) is such a row too, in a side or both.
/// </summary>
public sealed record FileChange(
    string RelativePath,
    ChangeStatus Status,
    ChangeReason? Reason,
    long LeftSize,
    long RightSize,
    string? RenamedFrom = null,
    int? SimilarityMilli = null,
    string? Unreadable = null,
    bool EditedRename = false,
    bool BehindLink = false)
{
    /// <summary>The reason token to show: the reason, "unreadable", or null.</summary>
    public string? ReasonLabel => Reason is { } r ? CanonicalTokens.Token(r) : Unreadable is not null ? "unreadable" : null;

    /// <summary>A byte-identical rename: the header alone moves it, nothing to diff.</summary>
    public bool PureRename => Status == ChangeStatus.Renamed && SimilarityMilli == 1000 && !EditedRename;
}

/// <summary>
/// The result of a 2-way directory compare: every path's status (changes sorted by path, ordinal, so
/// the output is deterministic — a diff of two reports is a real change). This is the status+reason
/// layer (Tiers 0–2); per-file hunks are produced lazily on top of it.
/// </summary>
public sealed class CompareReport
{
    public IReadOnlyList<FileChange> Changes { get; private set; }

    /// <summary>Identical paths counted but not in <see cref="Changes"/>: a compare3 side keeps only its changes
    /// (the merge never looks at an unchanged file), not a row for every file of the tree.</summary>
    public int UnlistedIdentical { get => _unlisted; init => _unlisted = value; }
    private int _unlisted;

    /// <summary>Directories that could not be listed on each side — a non-zero count means the report is
    /// INCOMPLETE there (missing files may be listing failures, not real removals). Never hidden.</summary>
    public int LeftDroppedDirectories { get; }
    public int RightDroppedDirectories { get; }

    /// <summary>Symlinks and junctions found in either tree and not followed (said, so nothing under one is assumed compared).</summary>
    public int SkippedLinks { get; init; }
    /// <summary>What each walk skipped (links, names Windows can't open by path). Empty for a result saved before 2026-10-08.</summary>
    public IReadOnlyList<SkippedPath> LeftSkipped { get; init; } = [];
    public IReadOnlyList<SkippedPath> RightSkipped { get; init; } = [];
    /// <summary>Names ending in '.' or ' ', or device names (unopenable by path on Windows), skipped in either tree.</summary>
    public int SkippedNames => LeftSkipped.Concat(RightSkipped).Count(s => s.Kind == SkipKind.Name);
    /// <summary>Why the hash ledgers could not be saved after the compare (the result stands; the next run reads again).</summary>
    public string? CacheSaveError { get; init; }
    /// <summary>Why edited renames were not looked for (too many candidates to score), or null: those files are listed
    /// as added and removed.</summary>
    public string? RenameLimit { get; init; }

    /// <summary>Same-size pairs that needed a content verdict (the only ones that can cost a read).</summary>
    public int ComparedPairs { get; init; }
    /// <summary>File sides answered from a trusted hash ledger instead of being read (of them, via CodeCompass).</summary>
    public int CacheHits { get; init; }
    public int CodeCompassHits { get; init; }
    /// <summary>Left files whose content was already proven by the previous compare of the same left tree
    /// (compare3's second compare reusing the base): neither read nor stat-checked again.</summary>
    public int ReusedLeftFiles { get; init; }
    /// <summary>Files whose read can't vouch for the file listed: compared as read, never cached. Each counted once
    /// (a file read on both sides is one), whatever the cause: <see cref="UnstableChanged"/>,
    /// <see cref="UnstableListing"/>, <see cref="UnstableNoChangeTime"/>.</summary>
    public int UnstableFiles { get; init; }
    /// <summary>Of <see cref="UnstableFiles"/>: the size, times or id moved during the read (a writer).</summary>
    public int UnstableChanged { get; init; }
    /// <summary>Of <see cref="UnstableFiles"/>: steady, but not what the directory listing says (a stale entry, a write
    /// through another hard link, a touch since the walk).</summary>
    public int UnstableListing { get; init; }
    /// <summary>Of <see cref="UnstableFiles"/>: the server gives no change time.</summary>
    public int UnstableNoChangeTime { get; init; }
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

    public int Total => Changes.Count + UnlistedIdentical;

    public int Count(ChangeStatus status) => (uint)status < Statuses ? Tally()[(int)status] : 0;

    public int ReasonCount(ChangeReason reason) => (uint)reason < Reasons ? Tally()[Statuses + (int)reason] : 0;

    /// <summary>Paths a side of which could not be read: their verdict is unknown (see <see cref="FileChange.Unreadable"/>).</summary>
    public int UnreadableFiles => Tally()[Statuses + Reasons];

    /// <summary>Of the modified: the pairs with no reason, since a side couldn't be read. With the reasons they make up
    /// the modified count (an unreadable add or remove is not one).</summary>
    public int UnreadableModified => Tally()[Statuses + Reasons + 1];

    private static readonly int Statuses = Enum.GetValues<ChangeStatus>().Length, Reasons = Enum.GetValues<ChangeReason>().Length;
    private int[]? _tally;

    /// <summary>The counts by status, by reason, and of unreadable paths, in one pass the first time one is asked
    /// for (the changes never change): a summary asked for again and again counted a million rows a dozen times each.</summary>
    private int[] Tally()
    {
        if (_tally is { } done) return done;
        var t = new int[Statuses + Reasons + 2];
        t[(int)ChangeStatus.Identical] = UnlistedIdentical;
        foreach (var c in Changes)
        {
            if ((uint)c.Status < Statuses) t[(int)c.Status]++;
            if (c.Reason is { } why && (uint)why < Reasons) t[Statuses + (int)why]++;
            if (c.Unreadable is not null) t[Statuses + Reasons]++;
            if (c.Status == ChangeStatus.Modified && c.Reason is null) t[Statuses + Reasons + 1]++;
        }
        return _tally = t;
    }

    /// <summary>This report with its identical rows dropped and counted in <see cref="UnlistedIdentical"/>: every
    /// count and <see cref="Total"/> stay the same.</summary>
    public CompareReport WithoutIdentical()
    {
        var changed = Changes.Where(c => c.Status != ChangeStatus.Identical).ToArray();
        var r = (CompareReport)MemberwiseClone();
        r.Changes = changed;
        r._unlisted = UnlistedIdentical + (Changes.Count - changed.Length);
        r._tally = null;
        return r;
    }
}
