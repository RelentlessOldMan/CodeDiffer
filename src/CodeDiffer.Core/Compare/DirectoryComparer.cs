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
        foreach (var path in paths)
        {
            bool inLeft = leftMap.TryGetValue(path, out var le);
            bool inRight = rightMap.TryGetValue(path, out var re);

            if (inLeft && !inRight)
            {
                changes.Add(new FileChange(path, ChangeStatus.Removed, null, le.Length, 0));
                continue;
            }
            if (!inLeft && inRight)
            {
                changes.Add(new FileChange(path, ChangeStatus.Added, null, 0, re.Length));
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

        return new CompareReport(changes);
    }
}
