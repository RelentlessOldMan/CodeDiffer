using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.DiffTruth;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.ThreeWay;
using CodeDiffer.Core.Verify;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// manifestVersion 2 (CodeSpawner 1.3.2): <c>_meta.bookkeeping</c> lists paths a variant differs in without an edit (each
/// variant's compile db, rewritten to name its own tree). Read strictly, skipped by the tree checks and reported, never
/// part of a digest.
/// </summary>
public sealed class BookkeepingTests : IDisposable
{
    private const string Db = "compile_commands.json";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-bookkeep-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public BookkeepingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), new UTF8Encoding(false).GetBytes(text));
    }

    private static string Delta(string meta, string modified = "")
        => "{\"_meta\":{" + meta + "},\"fileOps\":{\"added\":[],\"removed\":[],\"renamed\":[],\"modified\":[" + modified + "]}}";

    private static string Conflict(string meta, string clean = "")
        => "{\"_meta\":{" + meta + "},\"conflicts\":[],\"mergedClean\":[" + clean + "]}";

    private const string V2Meta = $$"""
        "manifestVersion":2,"bookkeeping":["{{Db}}"]
        """;

    [Fact]
    public void Version2_IsReadWithItsBookkeeping_AndSupported()
    {
        var d = DeltaManifestParser.Parse(Delta(V2Meta));
        Assert.Equal(2, d.ManifestVersion);
        Assert.Equal([Db], d.Bookkeeping);
        DeltaVerifier.AssertSupportedVersion(d);

        var c = ConflictManifestParser.Parse(Conflict(V2Meta));
        Assert.Equal([Db], c.Bookkeeping);
        DeltaVerifier.AssertSupportedVersion(c);

        Assert.Empty(DeltaManifestParser.Parse(Delta("\"manifestVersion\":1")).Bookkeeping!);
        Assert.Throws<NotSupportedException>(() => DeltaVerifier.AssertSupportedVersion(DeltaManifestParser.Parse(Delta("\"manifestVersion\":3"))));
        Assert.Throws<NotSupportedException>(() => DeltaVerifier.AssertSupportedVersion(DeltaManifestParser.Parse(Delta("\"manifestVersion\":0"))));
    }

    [Theory]
    [InlineData("\"manifestVersion\":2")]                                             // version 2 is exactly the ones that list it
    [InlineData("\"manifestVersion\":1,\"bookkeeping\":[\"compile_commands.json\"]")] // and version 1 never carries it
    [InlineData("\"manifestVersion\":2,\"bookkeeping\":[]")]
    [InlineData("\"manifestVersion\":2,\"bookkeeping\":\"compile_commands.json\"")]
    [InlineData("\"manifestVersion\":2,\"bookkeeping\":[\"\"]")]
    [InlineData("\"manifestVersion\":2,\"bookkeeping\":[1]")]
    [InlineData("\"manifestVersion\":2,\"bookkeeping\":[\"a\",\"a\"]")]
    public void ABadBookkeepingList_IsRefused(string meta)
    {
        // A FormatException: verify reports it as an error (exit 2), never a crash.
        Assert.Throws<FormatException>(() => DeltaManifestParser.Parse(Delta(meta)));
        Assert.Throws<FormatException>(() => ConflictManifestParser.Parse(Conflict(meta)));
    }

    [Fact]
    public void ABookkeepingPathTheManifestAlsoDescribes_IsRefused()
    {
        var record = $$"""{"path":"{{Db}}","reason":"content","oldSha":"o","newSha":"n","oldSize":1,"newSize":1,"hunks":[]}""";
        Assert.Throws<FormatException>(() => DeltaManifestParser.Parse(Delta(V2Meta, record)));
        var added = "{\"_meta\":{" + V2Meta + "},\"fileOps\":{\"added\":[\"" + Db + "\"],\"removed\":[],\"renamed\":[],\"modified\":[]}}";
        Assert.Throws<FormatException>(() => DeltaManifestParser.Parse(added));

        var clean = $$"""{"path":"{{Db}}","side":"v1","op":"replace","oldStart":1,"oldLines":1,"newStart":1,"newLines":1}""";
        Assert.Throws<FormatException>(() => ConflictManifestParser.Parse(Conflict(V2Meta, clean)));
    }

    [Fact]
    public void Bookkeeping_ChangesNoDigest()
    {
        var record = """{"path":"src/a.c","reason":"content","oldSha":"o","newSha":"n","oldSize":1,"newSize":1,"hunks":[{"op":"replace","oldStart":1,"oldLines":1,"newStart":1,"newLines":1}]}""";
        var v1 = DeltaManifestParser.Parse(Delta("\"manifestVersion\":1", record));
        var v2 = DeltaManifestParser.Parse(Delta(V2Meta, record));
        Assert.Equal(DiffTruthDigest.Compute(v1), DiffTruthDigest.Compute(v2));
    }

    [Fact]
    public void FileOpsCheck_SkipsAndReportsTheBookkeepingPath()
    {
        Put("B/a.c", "one\n");
        Put("V/a.c", "two\n");
        Put($"B/{Db}", "[{\"directory\":\"B\"}]\n");
        Put($"V/{Db}", "[{\"directory\":\"V\"}]\n");
        Put("B/keep.c", "same\n");
        Put("V/keep.c", "same\n");
        var report = new DirectoryComparer(NoCache).Compare(P("B"), P("V"));
        var a = new FileDelta("a.c", ChangeReason.Content, "o", "n", 4, 4, [new Hunk(HunkOp.Replace, 1, 1, 1, 1)], []);

        var v2 = new DeltaManifest(2, [], [], [], [a], null, Bookkeeping: [Db]);
        var fo = DeltaFileOpsCheck.Run(report, v2);
        Assert.True(fo.Ok, string.Join("; ", fo.Missing.Concat(fo.Extra)));
        Assert.Equal([$"modified {Db}"], fo.BookkeepingSkipped);

        // Without the list the db is an operation the manifest leaves out.
        var v1 = v2 with { ManifestVersion = 1, Bookkeeping = null };
        var bad = DeltaFileOpsCheck.Run(report, v1);
        Assert.False(bad.Ok);
        Assert.Equal([$"modified {Db}"], bad.Extra);
        Assert.Empty(bad.BookkeepingSkipped);

        // Only the listed path is set aside: another file left out of the manifest still fails.
        Put("V/keep.c", "edited\n");
        var other = DeltaFileOpsCheck.Run(new DirectoryComparer(NoCache).Compare(P("B"), P("V")), v2);
        Assert.Equal(["modified keep.c"], other.Extra);
        Assert.Equal([$"modified {Db}"], other.BookkeepingSkipped);

        // An unchanged bookkeeping path is nothing to report.
        Put($"V/{Db}", "[{\"directory\":\"B\"}]\n");
        Put("V/keep.c", "same\n");
        var same = DeltaFileOpsCheck.Run(new DirectoryComparer(NoCache).Compare(P("B"), P("V")), v2);
        Assert.True(same.Ok);
        Assert.Empty(same.BookkeepingSkipped);
    }

    [Fact]
    public void Compare3Check_SkipsAndReportsTheBookkeepingPath()
    {
        // Each variant's db names its own tree: both sides rewrote the same line, so compare3 conflicts in it.
        foreach (var t in new[] { "B", "V1", "V2" }) Put($"{t}/{Db}", $"[{{\"directory\":\"{t}\"}}]\n");
        foreach (var t in new[] { "B", "V1", "V2" }) Put($"{t}/keep.c", "same\n");
        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache);

        var v2 = new ConflictManifest(2, "V1", "V2", [], [], null, [Db]);
        var c3 = ConflictTreeCrossCheck.Compare3(r, v2);
        Assert.True(c3.Ok, string.Join("; ", c3.ConflictOnlyInCompare3.Concat(c3.MergedNotInManifest)));
        Assert.Equal([$"{Db} (conflict)"], c3.BookkeepingSkipped);

        var v1 = v2 with { ManifestVersion = 1, Bookkeeping = null };
        var bad = ConflictTreeCrossCheck.Compare3(r, v1);
        Assert.Equal([Db], bad.ConflictOnlyInCompare3);
        Assert.False(bad.Ok);

        // A file both sides edited that the manifest leaves out still fails beside a skipped db.
        Put("V1/keep.c", "same\nv1\n");
        Put("V2/keep.c", "v2\nsame\n");
        var other = ConflictTreeCrossCheck.Compare3(TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache), v2);
        Assert.Equal(["keep.c"], other.MergedNotInManifest);
        Assert.False(other.Ok);
    }
}
