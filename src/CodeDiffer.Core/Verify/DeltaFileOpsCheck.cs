using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Verify;

/// <summary>Where CodeDiffer's own compare and the manifest disagree, file by file (each list capped for printing).</summary>
public sealed record FileOpsCheckResult(
    int Expected, int Matched,
    IReadOnlyList<string> Missing, IReadOnlyList<string> Extra, IReadOnlyList<string> WrongReason, IReadOnlyList<string> WrongSimilarity,
    int MissingCount, int ExtraCount, int WrongReasonCount, int WrongSimilarityCount)
{
    /// <summary>Pass iff CodeDiffer reports exactly the manifest's file operations, with the same reasons and rename similarities.</summary>
    public bool Ok => MissingCount + ExtraCount + WrongReasonCount + WrongSimilarityCount == 0;
}

/// <summary>
/// The file-level verify tier: CodeDiffer's OWN compare of the base and variant trees (the verdicts every user sees:
/// status, reason, rename pairing and similarity) against the manifest's fileOps. The digest tier checks the
/// manifest, the hunk tier the line differ; this one checks the compare itself.
/// </summary>
public static class DeltaFileOpsCheck
{
    public static FileOpsCheckResult Run(CompareReport report, DeltaManifest manifest, int maxListed = 20)
    {
        // Each operation as a key; its detail (reason / similarity) compared when both sides have it.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in manifest.Added) expected[$"added {p}"] = "";
        foreach (var p in manifest.Removed) expected[$"removed {p}"] = "";
        foreach (var r in manifest.Renamed) expected[$"renamed {r.From} -> {r.To}"] = $"similarity {r.SimilarityMilli}";
        foreach (var m in manifest.Modified) expected[$"modified {m.Path}"] = CanonicalTokens.Token(m.Reason);

        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in report.Changes)
            switch (c.Status)
            {
                case ChangeStatus.Added: actual[$"added {c.RelativePath}"] = ""; break;
                case ChangeStatus.Removed: actual[$"removed {c.RelativePath}"] = ""; break;
                case ChangeStatus.Renamed: actual[$"renamed {c.RenamedFrom} -> {c.RelativePath}"] = $"similarity {c.SimilarityMilli}"; break;
                case ChangeStatus.Modified: actual[$"modified {c.RelativePath}"] = c.ReasonLabel ?? "?"; break;
            }

        var missing = expected.Keys.Where(k => !actual.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var extra = actual.Keys.Where(k => !expected.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var wrong = expected.Where(kv => actual.TryGetValue(kv.Key, out var a) && a != kv.Value)
            .Select(kv => $"{kv.Key}: manifest {kv.Value}, CodeDiffer {actual[kv.Key]}").Order(StringComparer.Ordinal).ToList();
        var reason = wrong.Where(w => !w.StartsWith("renamed ", StringComparison.Ordinal)).ToList();
        var sim = wrong.Where(w => w.StartsWith("renamed ", StringComparison.Ordinal)).ToList();

        return new FileOpsCheckResult(expected.Count, expected.Count - missing.Count - wrong.Count,
            missing.Take(maxListed).ToList(), extra.Take(maxListed).ToList(), reason.Take(maxListed).ToList(), sim.Take(maxListed).ToList(),
            missing.Count, extra.Count, reason.Count, sim.Count);
    }
}
