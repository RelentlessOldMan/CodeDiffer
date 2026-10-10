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
    /// <summary>True when <paramref name="hunks"/> rebuild <paramref name="newLines"/> from <paramref name="oldLines"/>;
    /// false too when a hunk points outside either file or overlaps another, or its op doesn't fit its line counts (insert:
    /// no old lines; delete: no new lines; replace: both) — a bad manifest is a failed check, not a crash.</summary>
    public static bool Rebuilds(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, IReadOnlyList<Hunk> hunks)
    {
        if (!hunks.All(OpFits)) return false;
        try { return Reconstruct(oldLines, newLines, hunks).SequenceEqual(newLines); }
        catch (FormatException) { return false; }
    }

    /// <summary>The contract's op for a hunk's counts: insert = 0 old / ≥1 new, delete = ≥1 old / 0 new, replace = both ≥1.</summary>
    public static bool OpFits(Hunk h) => h.Op switch
    {
        HunkOp.Insert => h.OldLines == 0 && h.NewLines > 0,
        HunkOp.Delete => h.OldLines > 0 && h.NewLines == 0,
        _ => h.OldLines > 0 && h.NewLines > 0,
    };

    /// <summary>
    /// Why <paramref name="hunks"/> are not the canonical hunks from <paramref name="oldLines"/> to
    /// <paramref name="newLines"/>, or null when they are. Streamed: each side is read once, in order. Rebuilding is not
    /// enough, since a hunk takes its text from the new file: a replace wider than the change, two hunks with no unchanged
    /// line between them, or a delete placed anywhere would all still rebuild. So, besides rebuilding: every op fits its
    /// counts; hunks are coalesced (an unchanged line between any two); each starts where the walk is on both sides
    /// (insert: <c>oldStart</c> = old lines before it; every hunk: <c>newStart</c> = new lines before it + 1, a delete
    /// too); a replace's first and last lines differ on the two sides (else it is wider than the change), and a replace
    /// of as many lines as it replaces leaves none of them as it was (that line would split it in two). With
    /// <paramref name="whitespaceOnly"/>, each hunk must also change only whitespace
    /// (<see cref="Compare.TextInspector.WhitespaceKey"/>).
    /// </summary>
    public static string? Problem(IEnumerable<string> oldLines, IEnumerable<string> newLines, IReadOnlyList<Hunk> hunks, bool whitespaceOnly = false)
    {
        foreach (var h in hunks)
            if (!OpFits(h)) return $"{Show(h)}: its op doesn't fit its line counts";
        using var o = oldLines.GetEnumerator();
        using var n = newLines.GetEnumerator();
        int oi = 0, ni = 0; // lines consumed
        bool first = true;
        var ordered = hunks.OrderBy(h => h.OldLines == 0 ? h.OldStart : h.OldStart - 1).ThenBy(h => h.NewStart).ToList();
        foreach (var h in ordered)
        {
            int copyUntil = h.OldLines == 0 ? h.OldStart : h.OldStart - 1;
            if (copyUntil < oi) return $"{Show(h)}: overlaps the hunk before it";
            if (!first && copyUntil == oi) return $"{Show(h)}: abuts the hunk before it (contiguous changes are one hunk)";
            first = false;
            for (; oi < copyUntil; oi++, ni++)
            {
                if (!o.MoveNext()) return $"{Show(h)}: past the end of the old file";
                if (!n.MoveNext()) return $"{Show(h)}: the new file ends at line {ni:N0}, before old line {oi + 1:N0} it should keep";
                if (!string.Equals(o.Current, n.Current, StringComparison.Ordinal))
                    return $"old line {oi + 1:N0} is outside every hunk but is not new line {ni + 1:N0}";
            }
            if (h.NewStart != ni + 1) return $"{Show(h)}: newStart should be {ni + 1:N0} (the new lines before it + 1)";
            var oldBlock = new List<string>(Math.Min(h.OldLines, 1024));
            var newBlock = new List<string>(Math.Min(h.NewLines, 1024));
            for (int k = 0; k < h.OldLines; k++, oi++)
            {
                if (!o.MoveNext()) return $"{Show(h)}: past the end of the old file";
                oldBlock.Add(o.Current);
            }
            for (int k = 0; k < h.NewLines; k++, ni++)
            {
                if (!n.MoveNext()) return $"{Show(h)}: past the end of the new file";
                newBlock.Add(n.Current);
            }
            if (h.Op == HunkOp.Replace && (oldBlock[0] == newBlock[0] || oldBlock[^1] == newBlock[^1]))
                return $"{Show(h)}: wider than the change (its {(oldBlock[0] == newBlock[0] ? "first" : "last")} line is the same on both sides)";
            // Lines edited in place, line for line: one left as it was is no part of the change, and splits the hunk.
            if (h.Op == HunkOp.Replace && h.OldLines == h.NewLines)
                for (int k = 1; k < h.OldLines - 1; k++)
                    if (oldBlock[k] == newBlock[k])
                        return $"{Show(h)}: spans old line {h.OldStart + k:N0}, which is unchanged (two hunks, not one)";
            if (whitespaceOnly && !string.Equals(WsKey(oldBlock), WsKey(newBlock), StringComparison.Ordinal))
                return $"{Show(h)}: changes more than whitespace";
        }
        while (true)
        {
            bool mo = o.MoveNext(), mn = n.MoveNext();
            if (!mo && !mn) return null;
            if (mo != mn) return mo ? $"the new file ends at line {ni:N0}, before old line {oi + 1:N0}" : $"new line {ni + 1:N0} is past the old file's end and in no hunk";
            oi++; ni++;
            if (!string.Equals(o.Current, n.Current, StringComparison.Ordinal))
                return $"old line {oi:N0} is outside every hunk but is not new line {ni:N0}";
        }
    }

    private static string WsKey(List<string> lines)
        => Compare.TextInspector.WhitespaceKey(Compare.TextInspector.NormalizeEol(string.Join('\n', lines)));

    private static string Show(Hunk h) => $"hunk {CanonicalTokens.Token(h.Op)} -{h.OldStart},{h.OldLines} +{h.NewStart},{h.NewLines}";

    /// <exception cref="FormatException">A hunk points outside either file or overlaps the one before it.</exception>
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
            if (h.OldLines < 0 || h.NewLines < 0 || copyUntil < oi || copyUntil + h.OldLines > oldLines.Count
                || (h.NewLines > 0 && (h.NewStart < 1 || h.NewStart - 1 + h.NewLines > newLines.Count)))
                throw new FormatException($"hunk {h.Op} -{h.OldStart},{h.OldLines} +{h.NewStart},{h.NewLines} is outside the files or overlaps another");
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
