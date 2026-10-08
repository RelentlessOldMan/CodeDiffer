using System.Text.Json;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Verify;

/// <summary>
/// Parses CodeSpawner's base→variant diff-delta JSON (deltaKind "diff") into a <see cref="DeltaManifest"/>.
/// Field names mirror CodeSpawner's writer exactly (Mutation/Mutator.cs): `_meta` carries
/// manifestVersion + diffTruthSha; `fileOps` carries added/removed (string arrays), modified
/// (per-file {path,reason,oldSha,newSha,oldSize,newSize,hunks[]}), and renamed ({from,to,similarityMilli}).
/// A run-rule is a hunk object bearing `"kind":"run"`; everything else is an explicit hunk. Unknown
/// fields (editKind, target, prevTruthSha, symbols, a per-file metadata object) are tolerated/ignored.
/// </summary>
public static class DeltaManifestParser
{
    public static DeltaManifest ParseFile(string path) => Parse(File.ReadAllText(path));

    /// <exception cref="FormatException">The JSON is not a delta manifest (a field missing or of the wrong kind).</exception>
    public static DeltaManifest Parse(string json) => Malformed(() => ParseCore(json), "delta manifest");

    private static DeltaManifest ParseCore(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var meta = root.GetProperty("_meta");
        int version = meta.GetProperty("manifestVersion").GetInt32();
        string? diffSha = meta.TryGetProperty("diffTruthSha", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;

        var added = new List<string>();
        var removed = new List<string>();
        var renamed = new List<RenameOp>();
        var modified = new List<FileDelta>();

        if (root.TryGetProperty("fileOps", out var fileOps))
        {
            ReadStringArray(fileOps, "added", added);
            ReadStringArray(fileOps, "removed", removed);

            if (fileOps.TryGetProperty("renamed", out var ren) && ren.ValueKind == JsonValueKind.Array)
                foreach (var r in ren.EnumerateArray())
                    renamed.Add(new RenameOp(
                        Req(r, "from"),
                        Req(r, "to"),
                        r.GetProperty("similarityMilli").GetInt32()));

            if (fileOps.TryGetProperty("modified", out var mod) && mod.ValueKind == JsonValueKind.Array)
                foreach (var f in mod.EnumerateArray())
                    modified.Add(ReadFileDelta(f));
        }

        return new DeltaManifest(version, added, removed, renamed, modified, diffSha);
    }

    private static FileDelta ReadFileDelta(JsonElement f)
    {
        var hunks = new List<Hunk>();
        var runs = new List<RunHunk>();

        if (f.TryGetProperty("hunks", out var hs) && hs.ValueKind == JsonValueKind.Array)
            foreach (var h in hs.EnumerateArray())
            {
                var op = CanonicalTokens.Op(Req(h, "op"));
                if (h.TryGetProperty("kind", out var kind) && kind.GetString() == "run")
                    runs.Add(new RunHunk(
                        op,
                        h.GetProperty("stride").GetInt32(),
                        h.GetProperty("rangeStart").GetInt32(),
                        h.GetProperty("rangeEnd").GetInt32(),
                        h.GetProperty("perHunk").GetInt32()));
                else
                    hunks.Add(new Hunk(
                        op,
                        h.GetProperty("oldStart").GetInt32(),
                        h.GetProperty("oldLines").GetInt32(),
                        h.GetProperty("newStart").GetInt32(),
                        h.GetProperty("newLines").GetInt32()));
            }

        return new FileDelta(
            Req(f, "path"),
            CanonicalTokens.Reason(Req(f, "reason")),
            Req(f, "oldSha"),
            Req(f, "newSha"),
            f.GetProperty("oldSize").GetInt64(),
            f.GetProperty("newSize").GetInt64(),
            hunks,
            runs);
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

    private static void ReadStringArray(JsonElement obj, string name, List<string> into)
    {
        if (obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String)
                    into.Add(e.GetString()!);
    }
}
