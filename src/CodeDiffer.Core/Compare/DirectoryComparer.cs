using CodeDiffer.Core.Hashing;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Walk;

namespace CodeDiffer.Core.Compare;

public sealed class CompareOptions
{
    /// <summary>Directory names skipped whole during the walk. Default ignores VCS metadata only.</summary>
    public IReadOnlyCollection<string> IgnoredDirectoryNames { get; init; } = [".git"];

    /// <summary>Above this per-file size, reason classification stays coarse (content), not decoded.</summary>
    public long MaxClassifyBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>Resolve added/removed pairs into renames (pure + edited). On by default.</summary>
    public bool DetectRenames { get; init; } = true;

    /// <summary>Minimum similarityMilli for an EDITED rename to be kept. 500 = git's -M50% default.</summary>
    public int RenameSimilarityThresholdMilli { get; init; } = 500;
}

/// <summary>
/// The 2-way directory compare (Tiers 0–2 of the cost funnel): walk both trees, pair by relative
/// path, and classify each path as identical / added / removed / modified — confirming content by
/// SHA-256 (size is the first-pass filter) and labeling every modified file with its reason.
///
/// v1 is serial and hashes same-size pairs fully (correct, re-run cost amortized by a future hash
/// cache). Rename detection, bounded-parallel SMB reads, the hash cache, and lazy per-file hunks are
/// subsequent increments — see DESIGN.txt.
/// </summary>
public sealed class DirectoryComparer
{
    private readonly CompareOptions _options;

    public DirectoryComparer(CompareOptions? options = null) => _options = options ?? new CompareOptions();

    public CompareReport Compare(string left, string right)
    {
        InputValidation.ValidateTrees(left, right);

        var walker = new TreeWalker(_options.IgnoredDirectoryNames);
        var leftMap = walker.Walk(left).ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var rightMap = walker.Walk(right).ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var classifier = new ReasonClassifier(_options.MaxClassifyBytes);

        var paths = new SortedSet<string>(StringComparer.Ordinal);
        paths.UnionWith(leftMap.Keys);
        paths.UnionWith(rightMap.Keys);

        var changes = new List<FileChange>(paths.Count);
        var removed = new List<FileEntry>();
        var added = new List<FileEntry>();
        foreach (var path in paths)
        {
            bool inLeft = leftMap.TryGetValue(path, out var le);
            bool inRight = rightMap.TryGetValue(path, out var re);

            if (inLeft && !inRight)
            {
                removed.Add(le); // held back — may resolve into a rename below
                continue;
            }
            if (!inLeft && inRight)
            {
                added.Add(re);
                continue;
            }

            // Present on both sides: size is the cheap filter; same size still needs a hash to confirm.
            if (le.Length == re.Length &&
                ContentHasher.HashFile(le.FullPath) == ContentHasher.HashFile(re.FullPath))
            {
                changes.Add(new FileChange(path, ChangeStatus.Identical, null, le.Length, re.Length));
            }
            else
            {
                var reason = classifier.Classify(le.FullPath, le.Length, re.FullPath, re.Length);
                changes.Add(new FileChange(path, ChangeStatus.Modified, reason, le.Length, re.Length));
            }
        }

        AddResolvedAddsRemovesAndRenames(changes, removed, added);

        changes.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return new CompareReport(changes);
    }

    /// <summary>
    /// Turn the held-back left-only / right-only entries into Renamed changes (when enabled) plus the
    /// leftover Added / Removed. A rename's destination path is its RelativePath; its source rides in
    /// RenamedFrom. Left/right sizes are carried so the summary can show both ends of a move.
    /// </summary>
    private void AddResolvedAddsRemovesAndRenames(List<FileChange> changes, List<FileEntry> removed, List<FileEntry> added)
    {
        IReadOnlyList<FileEntry> leftoverRemoved = removed;
        IReadOnlyList<FileEntry> leftoverAdded = added;

        if (_options.DetectRenames)
        {
            var result = new RenameDetector(_options).Detect(removed, added);
            leftoverRemoved = result.UnmatchedRemoved;
            leftoverAdded = result.UnmatchedAdded;

            var removedByPath = removed.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
            var addedByPath = added.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
            foreach (var r in result.Renames)
            {
                long fromSize = removedByPath.TryGetValue(r.From, out var fe) ? fe.Length : 0;
                long toSize = addedByPath.TryGetValue(r.To, out var te) ? te.Length : 0;
                changes.Add(new FileChange(r.To, ChangeStatus.Renamed, null, fromSize, toSize, r.From, r.SimilarityMilli));
            }
        }

        foreach (var e in leftoverRemoved)
            changes.Add(new FileChange(e.RelativePath, ChangeStatus.Removed, null, e.Length, 0));
        foreach (var e in leftoverAdded)
            changes.Add(new FileChange(e.RelativePath, ChangeStatus.Added, null, 0, e.Length));
    }
}
