using System.Security.Cryptography;
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
/// <para>
/// A sharded delta (CodeSpawner 1.1.x <c>--shard-size</c>) is read from its index, <c>&lt;corpus&gt;-delta.index.json</c>:
/// deltaKind "diff" plus <c>_meta.shardSize</c>, the file ops other than <c>modified</c> inline, and a <c>shards[]</c>
/// catalog of pages beside it. Every page is checked against the catalog (its file's sha256, its index, its count, its
/// first and last path) and the records must run in ascending path order across the pages; <c>modified</c> is the pages
/// in order, so the digest reproduces exactly as from the monolithic delta. A missing, extra, altered or misplaced page
/// is a <see cref="FormatException"/>, never a partial manifest.
/// </para>
/// </summary>
public static class DeltaManifestParser
{
    private const string IndexSuffix = ".index.json";

    /// <summary>A delta from its file; a sharded delta's index also reads the pages beside it.</summary>
    /// <exception cref="FormatException">Not a delta manifest, or a sharded one whose pages don't match its index.</exception>
    public static DeltaManifest ParseFile(string path)
    {
        var json = File.ReadAllText(path);
        return Malformed(() => ParseCore(json, path), "delta manifest");
    }

    /// <exception cref="FormatException">The JSON is not a delta manifest (a field missing or of the wrong kind).</exception>
    public static DeltaManifest Parse(string json) => Malformed(() => ParseCore(json, indexPath: null), "delta manifest");

    private static DeltaManifest ParseCore(string json, string? indexPath)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("_meta", out _) && root.TryGetProperty("shardIndex", out _))
            throw new FormatException("this is one page of a sharded delta, not the delta: pass its index (<corpus>-delta.index.json)");

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

        if (!meta.TryGetProperty("shardSize", out var size))
        {
            // Pages without the marker would verify as a delta with nothing modified.
            if (root.TryGetProperty("shards", out _))
                throw new FormatException("a shards[] catalog without _meta.shardSize: a sharded delta's index carries both");
            return new DeltaManifest(version, added, removed, renamed, modified, diffSha);
        }

        if (size.ValueKind != JsonValueKind.Number || !size.TryGetInt32(out int shardSize) || shardSize < 1)
            throw new FormatException("_meta.shardSize must be a positive whole number");
        if (!(meta.TryGetProperty("deltaKind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "diff"))
            throw new FormatException("_meta.shardSize on a delta whose deltaKind isn't \"diff\": only a 2-way diff delta is sharded");
        if (indexPath is null)
            throw new FormatException("a sharded delta's index: read it from its file, so its pages can be found beside it");
        if (fileOps.ValueKind == JsonValueKind.Object && fileOps.TryGetProperty("modified", out _))
            throw new FormatException("a sharded delta's index carries fileOps.modified: only its pages may");

        var pages = ReadPages(root, indexPath, shardSize);
        return new DeltaManifest(version, added, removed, renamed, pages.Modified, diffSha, new DeltaPaging(shardSize, pages.Count));
    }

    /// <summary>The <c>modified</c> records of a sharded delta: every page in the index's catalog, each checked against it.</summary>
    private static (List<FileDelta> Modified, int Count) ReadPages(JsonElement index, string indexPath, int shardSize)
    {
        if (!index.TryGetProperty("shards", out var catalog) || catalog.ValueKind != JsonValueKind.Array)
            throw new FormatException("a sharded delta's index has no shards[] catalog");

        var dir = Path.GetDirectoryName(Path.GetFullPath(indexPath))!;
        var entries = catalog.EnumerateArray().ToList();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modified = new List<FileDelta>();
        string? previous = null;

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            string file = Req(e, "file");
            string where = $"page {i} ({file})";
            if (file != Path.GetFileName(file) || file is "." or "..")
                throw new FormatException($"{where}: a page is named by its file name alone, beside the index");
            if (!listed.Add(file))
                throw new FormatException($"{where}: listed twice in the catalog");

            int count = e.GetProperty("count").GetInt32();
            if (count < 1 || count > shardSize || (count < shardSize && i < entries.Count - 1))
                throw new FormatException($"{where}: count {count} with shardSize {shardSize}: every page but the last holds " +
                                          "exactly shardSize records, and none is empty");

            var full = Path.Combine(dir, file);
            if (!File.Exists(full))
                throw new FormatException($"{where}: missing");
            var bytes = File.ReadAllBytes(full);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Req(e, "shardSha"), StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"{where}: its sha256 isn't the catalog's shardSha (truncated, altered or replaced)");

            List<FileDelta> page;
            try
            {
                using var pageDoc = JsonDocument.Parse(bytes);
                page = ReadPage(pageDoc.RootElement, i, count, where);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"{where}: {ex.Message}", ex);
            }

            if (page[0].Path != Req(e, "firstPath") || page[^1].Path != Req(e, "lastPath"))
                throw new FormatException($"{where}: runs {page[0].Path} .. {page[^1].Path}, not the catalog's " +
                                          $"{Req(e, "firstPath")} .. {Req(e, "lastPath")}");
            foreach (var f in page)
            {
                if (previous is not null && string.CompareOrdinal(previous, f.Path) >= 0)
                    throw new FormatException($"{where}: {f.Path} after {previous}: the records must run in ascending path " +
                                              "order across the pages, each path once");
                previous = f.Path;
            }
            modified.AddRange(page);
        }

        // A page beside the index that its catalog doesn't list: a stray, or a page the index lost.
        var name = Path.GetFileName(indexPath);
        if (name.EndsWith(IndexSuffix, StringComparison.OrdinalIgnoreCase))
            foreach (var f in Directory.EnumerateFiles(dir, name[..^IndexSuffix.Length] + ".shard-*.json"))
                if (!listed.Contains(Path.GetFileName(f)))
                    throw new FormatException($"{Path.GetFileName(f)} lies beside the index, but its catalog doesn't list it");

        return (modified, entries.Count);
    }

    /// <summary>One page: <c>{ shardIndex, count, modified[] }</c>, at its place in the catalog and as long as it says.</summary>
    private static List<FileDelta> ReadPage(JsonElement page, int index, int count, string where)
    {
        if (page.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{where}: not a page");
        if (page.TryGetProperty("_meta", out _))
            throw new FormatException($"{where}: has a _meta, so it is a delta or an index, not a page");
        int stated = page.GetProperty("shardIndex").GetInt32();
        if (stated != index)
            throw new FormatException($"{where}: says it is page {stated}");
        if (!page.TryGetProperty("modified", out var mod) || mod.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{where}: no modified[]");

        var records = mod.EnumerateArray().Select(ReadFileDelta).ToList();
        int own = page.GetProperty("count").GetInt32();
        if (own != count || records.Count != count)
            throw new FormatException($"{where}: the catalog says {count} record(s), the page says {own} and holds {records.Count}");
        return records;
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
