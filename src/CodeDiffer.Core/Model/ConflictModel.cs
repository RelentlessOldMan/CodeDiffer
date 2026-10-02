namespace CodeDiffer.Core.Model;

/// <summary>
/// A 3-way conflict region: a base line-range both variants edited with differing content. The new*
/// coords reference the respective variant tree (B_v1 / B_v2); base* references B. The conflict "kind"
/// (modify/modify, modify/delete, add/add) is derivable from (ops, baseLines) and is not stored.
/// </summary>
public sealed record Conflict(
    string Path, int BaseStart, int BaseLines,
    HunkOp V1Op, int V1NewStart, int V1NewLines,
    HunkOp V2Op, int V2NewStart, int V2NewLines);

/// <summary>A clean-merged result hunk from exactly one side; its new* coords reference that side's variant tree.</summary>
public sealed record CleanMerge(string Path, string Side, HunkOp Op, int OldStart, int OldLines, int NewStart, int NewLines);

/// <summary>
/// The parsed CodeSpawner 3-way conflict artifact (deltaKind "conflict-3way"). Pair it with the two
/// B→V diff-deltas and the B_v1/B_v2 trees. Assert <see cref="ManifestVersion"/> and independently
/// reproduce <see cref="ConflictTruthSha"/> before trusting it.
/// </summary>
public sealed record ConflictManifest(
    int ManifestVersion,
    string? V1Tree,
    string? V2Tree,
    IReadOnlyList<Conflict> Conflicts,
    IReadOnlyList<CleanMerge> CleanMerges,
    string? ConflictTruthSha);
