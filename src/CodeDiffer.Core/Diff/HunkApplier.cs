using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Diff;

/// <summary>
/// Applies coordinate-only hunks. Because hunks carry no text, the replacement content is taken from
/// the NEW file at each hunk's new-side coordinates — so <see cref="Reconstruct"/> rebuilding the new
/// file from (old + hunks + new-content) is the round-trip correctness check: if the coordinates
/// partition both files correctly, the result equals the new file exactly. This is also the basis of
/// change-porting (apply a changeset's hunks onto a third tree) in a later increment.
/// </summary>
public static class HunkApplier
{
    public static List<string> Reconstruct(
        IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, IReadOnlyList<Hunk> hunks)
    {
        var result = new List<string>();
        int oi = 0; // 0-based position in oldLines

        foreach (var h in hunks)
        {
            int copyUntil = h.OldStart - 1; // copy unchanged old lines up to the hunk
            for (; oi < copyUntil; oi++)
                result.Add(oldLines[oi]);

            oi += h.OldLines; // skip the deleted/replaced old lines

            for (int j = 0; j < h.NewLines; j++) // take the new-side content
                result.Add(newLines[h.NewStart - 1 + j]);
        }

        for (; oi < oldLines.Count; oi++) // trailing unchanged tail
            result.Add(oldLines[oi]);

        return result;
    }
}
