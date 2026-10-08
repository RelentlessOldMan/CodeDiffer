using System.Text.Json;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Verify;

/// <summary>
/// Parses CodeSpawner's 3-way conflict artifact (deltaKind "conflict-3way") into a
/// <see cref="ConflictManifest"/>. Field names mirror CodeSpawner's writer (Mutation/Mutator.cs):
/// `_meta` carries manifestVersion + conflictTruthSha + v1Tree/v2Tree; `conflicts` is an array of
/// {path, baseStart, baseLines, v1:{op,newStart,newLines}, v2:{op,newStart,newLines}}; `mergedClean`
/// is an array of {path, side, op, oldStart, oldLines, newStart, newLines}.
/// </summary>
public static class ConflictManifestParser
{
    public static ConflictManifest ParseFile(string path) => Parse(File.ReadAllText(path));

    /// <exception cref="FormatException">The JSON is not a conflict manifest (a field missing or of the wrong kind).</exception>
    public static ConflictManifest Parse(string json) => Malformed(() => ParseCore(json), "conflict manifest");

    private static ConflictManifest ParseCore(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var meta = root.GetProperty("_meta");
        int version = meta.GetProperty("manifestVersion").GetInt32();
        string? sha = Str(meta, "conflictTruthSha");
        string? v1Tree = Str(meta, "v1Tree");
        string? v2Tree = Str(meta, "v2Tree");

        var conflicts = new List<Conflict>();
        if (root.TryGetProperty("conflicts", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var c in cs.EnumerateArray())
            {
                var v1 = c.GetProperty("v1");
                var v2 = c.GetProperty("v2");
                conflicts.Add(new Conflict(
                    Req(c, "path"),
                    c.GetProperty("baseStart").GetInt32(),
                    c.GetProperty("baseLines").GetInt32(),
                    CanonicalTokens.Op(Req(v1, "op")), v1.GetProperty("newStart").GetInt32(), v1.GetProperty("newLines").GetInt32(),
                    CanonicalTokens.Op(Req(v2, "op")), v2.GetProperty("newStart").GetInt32(), v2.GetProperty("newLines").GetInt32()));
            }

        var clean = new List<CleanMerge>();
        if (root.TryGetProperty("mergedClean", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray())
                clean.Add(new CleanMerge(
                    Req(m, "path"),
                    Req(m, "side"),
                    CanonicalTokens.Op(Req(m, "op")),
                    m.GetProperty("oldStart").GetInt32(), m.GetProperty("oldLines").GetInt32(),
                    m.GetProperty("newStart").GetInt32(), m.GetProperty("newLines").GetInt32()));

        return new ConflictManifest(version, v1Tree, v2Tree, conflicts, clean, sha);
    }

    /// <summary>A required string property; anything else is a malformed manifest (<see cref="FormatException"/>).</summary>
    private static string Req(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new FormatException($"'{name}' must be a string");

    /// <summary>A missing property or a value of the wrong kind is a malformed manifest, never a crash.</summary>
    private static T Malformed<T>(Func<T> parse, string what)
    {
        try { return parse(); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            // GetProperty's KeyNotFoundException doesn't say which: the manifest is still plainly not one.
            throw new FormatException($"not a valid {what}: {(ex is KeyNotFoundException ? "a required field is missing" : ex.Message)}", ex);
        }
    }

    private static string? Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
