using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CodeDiffer.Core.DiffTruth;
using CodeDiffer.Core.Verify;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// CodeSpawner 1.1.x's sharded delta (<c>--shard-size</c>): read from its index, the pages in order are the monolithic
/// delta's <c>modified</c>, so the digest is the same; a missing, extra, altered or misplaced page is an error, never a
/// partial manifest, and a page alone is refused.
/// </summary>
public sealed class ShardedDeltaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-shard-" + Guid.NewGuid().ToString("N"));

    public ShardedDeltaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string name) => Path.Combine(_dir, name);
    private string Index => P("corpus-delta.index.json");
    private static string Page(int i) => $"corpus-delta.shard-{i:D3}.json";

    private static JsonObject Record(string path) => new()
    {
        ["path"] = path, ["reason"] = "content", ["oldSha"] = "o-" + path, ["newSha"] = "n-" + path, ["oldSize"] = 100, ["newSize"] = 101,
        ["hunks"] = new JsonArray(new JsonObject { ["op"] = "replace", ["oldStart"] = 3, ["oldLines"] = 1, ["newStart"] = 3, ["newLines"] = 1 }),
    };

    private static string[] Paths(int n) => Enumerable.Range(0, n).Select(i => $"src/f{i:D2}.c").ToArray();

    private static JsonObject Meta(string? sha, int? shardSize)
    {
        var meta = new JsonObject { ["manifestVersion"] = 1, ["deltaKind"] = "diff", ["editKind"] = "content" };
        if (sha is not null) meta["diffTruthSha"] = sha;
        if (shardSize is { } s) meta["shardSize"] = s;
        return meta;
    }

    private static JsonObject FileOps(IEnumerable<string>? modified) => modified is null
        ? new JsonObject { ["added"] = new JsonArray("new.c"), ["removed"] = new JsonArray(), ["renamed"] = new JsonArray() }
        : new JsonObject { ["added"] = new JsonArray("new.c"), ["removed"] = new JsonArray(), ["renamed"] = new JsonArray(),
                           ["modified"] = new JsonArray(modified.Select(p => (JsonNode)Record(p)).ToArray()) };

    /// <summary>The monolithic delta's text, its diffTruthSha computed by CodeDiffer's digest (golden-vector checked elsewhere).</summary>
    private static (string Json, string Sha) Monolithic(string[] paths)
    {
        var unsigned = new JsonObject { ["_meta"] = Meta(null, null), ["fileOps"] = FileOps(paths) }.ToJsonString();
        var sha = DiffTruthDigest.Compute(DeltaManifestParser.Parse(unsigned));
        return (new JsonObject { ["_meta"] = Meta(sha, null), ["fileOps"] = FileOps(paths) }.ToJsonString(), sha);
    }

    /// <summary>Write a sharded delta of these records, in this order, as CodeSpawner would; returns the index for tampering.</summary>
    private JsonObject WriteSharded(string[] paths, int shardSize, string sha)
    {
        var catalog = new JsonArray();
        var pages = paths.Chunk(shardSize).ToList();
        for (int i = 0; i < pages.Count; i++)
        {
            var page = new JsonObject
            {
                ["shardIndex"] = i, ["count"] = pages[i].Length,
                ["modified"] = new JsonArray(pages[i].Select(p => (JsonNode)Record(p)).ToArray()),
            };
            File.WriteAllText(P(Page(i)), page.ToJsonString());
            catalog.Add(new JsonObject
            {
                ["file"] = Page(i), ["firstPath"] = pages[i][0], ["lastPath"] = pages[i][^1], ["count"] = pages[i].Length,
                ["shardSha"] = ShaOf(Page(i)),
            });
        }
        var index = new JsonObject { ["_meta"] = Meta(sha, shardSize), ["fileOps"] = FileOps(null), ["shards"] = catalog, ["symbols"] = new JsonObject() };
        File.WriteAllText(Index, index.ToJsonString());
        return index;
    }

    private string ShaOf(string page) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(P(page)))).ToLowerInvariant();

    private void Rewrite(JsonObject index) => File.WriteAllText(Index, index.ToJsonString());

    /// <summary>Edit a page, then put its new sha in the catalog, so only the edit itself is wrong.</summary>
    private void EditPage(JsonObject index, int i, Action<JsonObject> edit)
    {
        var page = JsonNode.Parse(File.ReadAllText(P(Page(i))))!.AsObject();
        edit(page);
        File.WriteAllText(P(Page(i)), page.ToJsonString());
        index["shards"]![i]!["shardSha"] = ShaOf(Page(i));
        Rewrite(index);
    }

    private FormatException Refused() => Assert.Throws<FormatException>(() => DeltaManifestParser.ParseFile(Index));

    [Fact]
    public void TheIndexReadsAsTheMonolithicDelta_WithTheSameDigest()
    {
        var paths = Paths(25);
        var (json, sha) = Monolithic(paths);
        WriteSharded(paths, 10, sha);

        var whole = DeltaManifestParser.Parse(json);
        var sharded = DeltaManifestParser.ParseFile(Index);
        Assert.Equal(whole.Modified.Select(f => f.Path), sharded.Modified.Select(f => f.Path));
        Assert.Equal(whole.Added, sharded.Added);
        Assert.Equal(3, sharded.Paging!.Pages);
        Assert.Equal(10, sharded.Paging.ShardSize);
        Assert.Null(whole.Paging);
        Assert.True(DeltaVerifier.VerifyDigest(sharded).Ok);
        Assert.Equal(sha, DeltaVerifier.VerifyDigest(sharded).Recomputed);
    }

    [Fact]
    public void APageAlone_IsRefused()
    {
        WriteSharded(Paths(5), 2, "x");
        var ex = Assert.Throws<FormatException>(() => DeltaManifestParser.ParseFile(P(Page(1))));
        Assert.Contains("pass its index", ex.Message);
    }

    [Fact]
    public void AMissingPage_IsAnError()
    {
        WriteSharded(Paths(5), 2, "x");
        File.Delete(P(Page(2)));
        Assert.Contains("missing", Refused().Message);
    }

    [Fact]
    public void AnAlteredPage_IsAnError()
    {
        WriteSharded(Paths(5), 2, "x");
        File.AppendAllText(P(Page(1)), " ");
        Assert.Contains("shardSha", Refused().Message);
    }

    [Fact]
    public void APageTheCatalogLeavesOut_IsAnError()
    {
        var index = WriteSharded(Paths(5), 2, "x");
        index["shards"]!.AsArray().RemoveAt(2);
        Rewrite(index);
        Assert.Contains("doesn't list it", Refused().Message);
    }

    [Fact]
    public void APageListedTwice_IsAnError()
    {
        var index = WriteSharded(Paths(4), 2, "x");
        index["shards"]![1]!["file"] = Page(0);
        Rewrite(index);
        Assert.Contains("twice", Refused().Message);
    }

    [Fact]
    public void PagesSwappedInTheCatalog_AreAnError()
    {
        var index = WriteSharded(Paths(4), 2, "x");
        var shards = index["shards"]!.AsArray();
        var first = shards[0]!.DeepClone();
        shards[0] = shards[1]!.DeepClone();
        shards[1] = first;
        Rewrite(index);
        Assert.Contains("says it is page", Refused().Message);
    }

    [Fact]
    public void RecordsOutOfOrderAcrossPages_AreAnError()
    {
        var paths = Paths(4);
        WriteSharded([paths[2], paths[3], paths[0], paths[1]], 2, "x");
        Assert.Contains("ascending path order", Refused().Message);
    }

    [Fact]
    public void ARepeatedPath_IsAnError()
    {
        var paths = Paths(3);
        WriteSharded([paths[0], paths[1], paths[1], paths[2]], 2, "x");
        Assert.Contains("ascending path order", Refused().Message);
    }

    [Fact]
    public void ACountThatDisagrees_IsAnError()
    {
        var index = WriteSharded(Paths(5), 2, "x");
        EditPage(index, 1, page => page["modified"]!.AsArray().RemoveAt(1));
        Assert.Contains("the catalog says 2", Refused().Message);
    }

    [Fact]
    public void AShortPageBeforeTheLast_IsAnError()
    {
        var index = WriteSharded(Paths(5), 2, "x");
        index["shards"]![0]!["count"] = 1;
        Rewrite(index);
        Assert.Contains("every page but the last", Refused().Message);
    }

    [Fact]
    public void APathRangeThatDisagrees_IsAnError()
    {
        var index = WriteSharded(Paths(5), 2, "x");
        index["shards"]![1]!["lastPath"] = "src/zz.c";
        Rewrite(index);
        Assert.Contains("not the catalog's", Refused().Message);
    }

    [Fact]
    public void APageOutsideTheIndexsDirectory_IsAnError()
    {
        var index = WriteSharded(Paths(2), 2, "x");
        index["shards"]![0]!["file"] = "../" + Page(0);
        Rewrite(index);
        Assert.Contains("file name alone", Refused().Message);
    }

    [Fact]
    public void AnIndexCarryingModified_IsAnError()
    {
        var index = WriteSharded(Paths(2), 2, "x");
        index["fileOps"] = FileOps(["src/extra.c"]);
        Rewrite(index);
        Assert.Contains("only its pages", Refused().Message);
    }

    [Fact]
    public void ACatalogWithoutShardSize_IsAnError()
    {
        var index = WriteSharded(Paths(2), 2, "x");
        index["_meta"]!.AsObject().Remove("shardSize");
        Rewrite(index);
        Assert.Contains("without _meta.shardSize", Refused().Message);
    }

    [Theory]
    [InlineData("conflict-3way")]
    [InlineData(null)]
    public void ShardSizeOnAnythingButADiffDelta_IsAnError(string? kind)
    {
        var index = WriteSharded(Paths(2), 2, "x");
        if (kind is null) index["_meta"]!.AsObject().Remove("deltaKind");
        else index["_meta"]!["deltaKind"] = kind;
        Rewrite(index);
        Assert.Contains("only a 2-way diff delta", Refused().Message);
    }

    [Fact]
    public void AnIndexFromText_CannotFindItsPages()
    {
        WriteSharded(Paths(2), 2, "x");
        var ex = Assert.Throws<FormatException>(() => DeltaManifestParser.Parse(File.ReadAllText(Index)));
        Assert.Contains("read it from its file", ex.Message);
    }

    [Fact]
    public void AnEmptyDeltaHasNoPages()
    {
        File.WriteAllText(Index, new JsonObject
        {
            ["_meta"] = Meta("x", 10), ["fileOps"] = FileOps(null), ["shards"] = new JsonArray(),
        }.ToJsonString());
        var m = DeltaManifestParser.ParseFile(Index);
        Assert.Empty(m.Modified);
        Assert.Equal(0, m.Paging!.Pages);
    }
}
