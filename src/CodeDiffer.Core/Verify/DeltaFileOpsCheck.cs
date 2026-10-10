using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Verify;

/// <summary>Where CodeDiffer's own compare and the manifest disagree, file by file (each list capped for printing).</summary>
public sealed record FileOpsCheckResult(
    int Expected, int Matched,
    IReadOnlyList<string> Missing, IReadOnlyList<string> Extra, IReadOnlyList<string> WrongReason, IReadOnlyList<string> WrongSimilarity,
    int MissingCount, int ExtraCount, int WrongReasonCount, int WrongSimilarityCount, int MetadataCount = 0)
{
    /// <summary>Pass iff CodeDiffer reports exactly the manifest's file operations, with the same reasons and rename similarities.</summary>
    public bool Ok => MissingCount + ExtraCount + WrongReasonCount + WrongSimilarityCount == 0;
}

/// <summary>
/// The file-level verify tier: CodeDiffer's OWN compare of the base and variant trees (the verdicts every user sees:
/// status, reason, rename pairing and similarity) against the manifest's fileOps. The digest tier checks the
/// manifest, the hunk tier the line differ; this one checks the compare itself. A rename with edits is one operation:
/// the contract lists its hunks as a <c>modified</c> record under the new path, which CodeDiffer reports as the rename
/// (its similarity says it was edited), so that record is not a separate expected "modified". Given the trees, a rename's
/// reason is compared too: its modified record's reason, or "identical" without one, against CodeDiffer's classifier on
/// the pair (a compare gives a rename no reason of its own). A <c>metadata</c> record is content identical
/// (<c>oldSha==newSha</c>) and CodeDiffer doesn't diff metadata, so the compare must report that file unchanged (a rename
/// with one, identical).
/// </summary>
public static class DeltaFileOpsCheck
{
    public static FileOpsCheckResult Run(CompareReport report, DeltaManifest manifest, int maxListed = 20, string? baseDir = null, string? variantDir = null)
    {
        bool reasons = baseDir is not null && variantDir is not null;
        // Each operation as a key; its detail (reason / similarity) compared when both sides have it.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in manifest.Added) expected[$"added {p}"] = "";
        foreach (var p in manifest.Removed) expected[$"removed {p}"] = "";
        var modifiedAt = new Dictionary<string, FileDelta>(StringComparer.Ordinal);
        foreach (var m in manifest.Modified) modifiedAt.TryAdd(m.Path, m);
        foreach (var r in manifest.Renamed)
            expected[$"renamed {r.From} -> {r.To}"] = $"similarity {r.SimilarityMilli}" +
                (!reasons ? "" : modifiedAt.TryGetValue(r.To, out var m) && m.Reason != ChangeReason.Metadata ? $", {CanonicalTokens.Token(m.Reason)}" : ", identical");
        var renameTo = manifest.Renamed.Select(r => r.To).ToHashSet(StringComparer.Ordinal);
        var metadata = new List<string>();
        foreach (var m in manifest.Modified)
            if (renameTo.Contains(m.Path)) continue;
            else if (m.Reason == ChangeReason.Metadata) { expected[$"unchanged {m.Path} (metadata)"] = ""; metadata.Add(m.Path); }
            else expected[$"modified {m.Path}"] = CanonicalTokens.Token(m.Reason);
        var classifier = new Compare.ReasonClassifier();

        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in report.Changes)
            switch (c.Status)
            {
                case ChangeStatus.Added: actual[$"added {c.RelativePath}"] = ""; break;
                case ChangeStatus.Removed: actual[$"removed {c.RelativePath}"] = ""; break;
                case ChangeStatus.Renamed:
                    actual[$"renamed {c.RenamedFrom} -> {c.RelativePath}"] = $"similarity {c.SimilarityMilli}" +
                        (!reasons ? "" : c.PureRename ? ", identical" : $", {RenameReason(classifier, baseDir!, variantDir!, c)}");
                    break;
                case ChangeStatus.Modified: actual[$"modified {c.RelativePath}"] = c.ReasonLabel ?? "?"; break;
            }
        if (metadata.Count > 0)
        {
            var changed = report.Changes.Where(c => c.Status != ChangeStatus.Identical).SelectMany(c => c.RenamedFrom is { } from ? [c.RelativePath, from] : new[] { c.RelativePath })
                .ToHashSet(StringComparer.Ordinal);
            foreach (var p in metadata)
                if (!changed.Contains(p)) actual[$"unchanged {p} (metadata)"] = "";
        }

        var missing = expected.Keys.Where(k => !actual.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var extra = actual.Keys.Where(k => !expected.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var wrong = expected.Where(kv => actual.TryGetValue(kv.Key, out var a) && a != kv.Value)
            .Select(kv => $"{kv.Key}: manifest {kv.Value}, CodeDiffer {actual[kv.Key]}").Order(StringComparer.Ordinal).ToList();
        var reason = wrong.Where(w => !w.StartsWith("renamed ", StringComparison.Ordinal)).ToList();
        var sim = wrong.Where(w => w.StartsWith("renamed ", StringComparison.Ordinal)).ToList();

        return new FileOpsCheckResult(expected.Count, expected.Count - missing.Count - wrong.Count,
            missing.Take(maxListed).ToList(), extra.Take(maxListed).ToList(), reason.Take(maxListed).ToList(), sim.Take(maxListed).ToList(),
            missing.Count, extra.Count, reason.Count, sim.Count, metadata.Count);
    }

    private static string RenameReason(Compare.ReasonClassifier classifier, string baseDir, string variantDir, FileChange c)
    {
        string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        try { return CanonicalTokens.Token(classifier.Classify(Full(baseDir, c.RenamedFrom!), c.LeftSize, Full(variantDir, c.RelativePath), c.RightSize)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "unreadable"; }
    }
}
