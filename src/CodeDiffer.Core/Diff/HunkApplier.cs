using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Diff;

/// <summary>
/// Applies coordinate-only hunks. Because hunks carry no text, the replacement content is taken from
/// the NEW file at each hunk's new-side coordinates — so <see cref="Reconstruct"/> rebuilding the new
/// file from (old + hunks + new-content) is the round-trip correctness check: if the coordinates
/// partition both files correctly, the result equals the new file exactly. This is also the basis of
/// change-porting (apply a changeset's hunks onto a third tree) in a later increment.
///
/// Hunks are sorted by base position before applying, since a manifest may list them in any order (the
/// diffTruthSha is order-independent, so CodeSpawner emits e.g. inserts last) — applying out of order
/// would corrupt the walk.
/// </summary>
public static class HunkApplier
{
    public static List<string> Reconstruct(
        IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, IReadOnlyList<Hunk> hunks)
    {
        var result = new List<string>();
        int oi = 0; // 0-based position in oldLines

        // Sort by base position (insert sits after OldStart; replace/delete start at OldStart-1), then by
        // new position to break ties deterministically.
        var ordered = hunks
            .OrderBy(h => h.OldLines == 0 ? h.OldStart : h.OldStart - 1)
            .ThenBy(h => h.NewStart)
            .ToList();

        foreach (var h in ordered)
        {
            // Insert anchors on the line AFTER WHICH it goes (oldStart lines precede it); replace/delete
            // anchor on the first affected line, so copy up to oldStart-1.
            int copyUntil = h.OldLines == 0 ? h.OldStart : h.OldStart - 1;
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
