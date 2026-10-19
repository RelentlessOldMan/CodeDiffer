using System.Text.Json;

namespace CodeDiffer.Core.Verify;

/// <summary>
/// <c>_meta.bookkeeping</c> (CodeSpawner 1.3.2, manifestVersion 2): paths a variant differs from the base in without an
/// edit (the corpus's <c>compile_commands.json</c>, rewritten to name the variant's own tree). They are in no file op,
/// hunk or digest section; a reader comparing a variant to the base skips them. Version 2 exactly when the list is there.
/// </summary>
public static class ManifestBookkeeping
{
    /// <summary>The listed paths (empty without the list).</summary>
    /// <exception cref="FormatException">The list is malformed, or its presence disagrees with the manifest version.</exception>
    public static IReadOnlyList<string> Read(JsonElement meta, int version)
    {
        if (!meta.TryGetProperty("bookkeeping", out var list))
        {
            if (version == 2) throw new FormatException("manifestVersion 2 without _meta.bookkeeping: version 2 is exactly the manifests that list it");
            return [];
        }
        if (version < 2) throw new FormatException($"_meta.bookkeeping in a manifestVersion {version} manifest: only version 2 carries it");
        if (list.ValueKind != JsonValueKind.Array) throw new FormatException("_meta.bookkeeping must be an array of paths");

        var paths = new List<string>();
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String || e.GetString() is not { Length: > 0 } p)
                throw new FormatException("_meta.bookkeeping must hold non-empty path strings");
            if (paths.Contains(p, StringComparer.Ordinal)) throw new FormatException($"'{p}' is in _meta.bookkeeping more than once");
            paths.Add(p);
        }
        if (paths.Count == 0) throw new FormatException("_meta.bookkeeping is empty: a manifest with nothing to skip stays version 1");
        return paths;
    }

    /// <summary>A bookkeeping path the manifest also describes is a contradiction: skipping it would hide that record.</summary>
    /// <exception cref="FormatException">One of <paramref name="described"/> is a bookkeeping path.</exception>
    public static void NotDescribed(IReadOnlyList<string> bookkeeping, IEnumerable<string> described)
    {
        if (bookkeeping.Count == 0) return;
        var skip = bookkeeping.ToHashSet(StringComparer.Ordinal);
        foreach (var p in described)
            if (skip.Contains(p))
                throw new FormatException($"'{p}' is in _meta.bookkeeping and also a record of the manifest: bookkeeping is no edit");
    }
}
