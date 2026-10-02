using CodeDiffer.Core.Compare;

namespace CodeDiffer.Core.Diff;

/// <summary>
/// The rename-similarity metric, frozen to the CodeSpawner delta contract so CodeDiffer reproduces the
/// <c>similarityMilli</c> CodeSpawner emits for every <c>renamed[]</c> entry:
///
///   sim = commonLines / max(oldLineCount, newLineCount)   over EOL-NORMALIZED lines, MULTISET intersection
///   similarityMilli = round(sim * 1000)
///
/// Multiset intersection means each distinct line contributes min(countInOld, countInNew) to commonLines
/// — duplicated lines count only as often as both sides share them. Two empty files are defined similar
/// (sim = 1.0): there is nothing to differ. This is a pure function of the two texts; detection policy
/// (threshold, best-match assignment) lives in <see cref="Compare.RenameDetector"/>.
/// </summary>
public static class Similarity
{
    /// <summary>similarityMilli for two whole texts, per the contract (EOL-normalized, line multiset).</summary>
    public static int Milli(string oldText, string newText)
    {
        var oldLines = LineText.SplitLines(TextInspector.NormalizeEol(oldText));
        var newLines = LineText.SplitLines(TextInspector.NormalizeEol(newText));
        return MilliFromLines(oldLines, newLines);
    }

    /// <summary>similarityMilli for two already-split, already-EOL-normalized line arrays.</summary>
    public static int MilliFromLines(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        int max = Math.Max(oldLines.Count, newLines.Count);
        if (max == 0) return 1000; // both empty ⇒ identical

        int common = CommonLines(oldLines, newLines);
        // round(common/max * 1000) with exact integer arithmetic (no float rounding surprises).
        return (int)(((long)common * 1000 + max / 2) / max);
    }

    /// <summary>Size of the line multiset intersection: Σ over distinct lines of min(oldCount, newCount).</summary>
    private static int CommonLines(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        // Count the smaller side into the map, then draw down against the larger — O(n) memory on the min.
        var (small, large) = oldLines.Count <= newLines.Count ? (oldLines, newLines) : (newLines, oldLines);

        var counts = new Dictionary<string, int>(small.Count, StringComparer.Ordinal);
        foreach (var line in small)
            counts[line] = counts.TryGetValue(line, out var n) ? n + 1 : 1;

        int common = 0;
        foreach (var line in large)
            if (counts.TryGetValue(line, out var n) && n > 0)
            {
                counts[line] = n - 1;
                common++;
            }
        return common;
    }
}
