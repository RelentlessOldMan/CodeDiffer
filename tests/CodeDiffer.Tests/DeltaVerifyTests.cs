using CodeDiffer.Core.Model;
using CodeDiffer.Core.Verify;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The verify-adapter end to end: parse a delta in CodeSpawner's EXACT diff-delta JSON shape
/// (Mutation/Mutator.cs writer) and independently reproduce its diffTruthSha. The payload mirrors
/// CodeSpawner's frozen golden vector (z/last.c, a/first.c, big.h + run-rule), so a green here means
/// parse + digest together are byte-compatible with the generator — the cross-tool gate, through the parser.
/// </summary>
public class DeltaVerifyTests
{
    // CodeSpawner's diff-delta format verbatim (deltaKind "diff"); values = the digest-selftest golden input.
    private const string GoldenDeltaJson = """
    {
      "_meta": {
        "manifestVersion": 1,
        "deltaKind": "diff",
        "editKind": "content",
        "baseSeed": 1337,
        "editSeed": 42,
        "target": "source",
        "editDensity": 0.05,
        "baseManifestSha": "deadbeefbase",
        "prevTruthSha": "cafef00d",
        "diffTruthSha": "66c7e62566ee105e63dce7e770d47e1a9fbe71b86d50249e209f5bf41f03d542"
      },
      "fileOps": {
        "added": [],
        "removed": [],
        "modified": [
          { "path": "z/last.c", "reason": "content", "oldSha": "o1", "newSha": "n1", "oldSize": 100, "newSize": 110,
            "hunks": [ { "op": "replace", "oldStart": 5, "oldLines": 2, "newStart": 5, "newLines": 2 } ] },
          { "path": "a/first.c", "reason": "content", "oldSha": "o2", "newSha": "n2", "oldSize": 200, "newSize": 205,
            "hunks": [ { "op": "replace", "oldStart": 1, "oldLines": 1, "newStart": 1, "newLines": 1 },
                       { "op": "replace", "oldStart": 9, "oldLines": 3, "newStart": 9, "newLines": 3 } ] },
          { "path": "big.h", "reason": "content", "oldSha": "o3", "newSha": "n3", "oldSize": 1048576, "newSize": 1050000,
            "hunks": [ { "op": "replace", "kind": "run", "stride": 20, "rangeStart": 1, "rangeEnd": 5000, "perHunk": 1 } ] }
        ],
        "renamed": []
      },
      "symbols": {}
    }
    """;

    private const string GoldenSha = "66c7e62566ee105e63dce7e770d47e1a9fbe71b86d50249e209f5bf41f03d542";

    [Fact]
    public void Parse_ReadsMetaAndFileOps()
    {
        var m = DeltaManifestParser.Parse(GoldenDeltaJson);
        Assert.Equal(1, m.ManifestVersion);
        Assert.Equal(GoldenSha, m.DiffTruthSha);
        Assert.Equal(3, m.Modified.Count);

        var big = m.Modified.Single(f => f.Path == "big.h");
        Assert.Empty(big.Hunks);          // the run-rule is NOT an explicit hunk
        Assert.Single(big.RunHunks);
        Assert.Equal(20, big.RunHunks[0].Stride);

        var first = m.Modified.Single(f => f.Path == "a/first.c");
        Assert.Equal(2, first.Hunks.Count); // two explicit hunks parsed
    }

    [Fact]
    public void VerifyDigest_ReproducesStatedSha_ThroughTheParser()
    {
        var m = DeltaManifestParser.Parse(GoldenDeltaJson);
        DeltaVerifier.AssertSupportedVersion(m);

        var v = DeltaVerifier.VerifyDigest(m);
        Assert.True(v.Ok);
        Assert.Equal(GoldenSha, v.Recomputed);
        Assert.Equal(GoldenSha, v.Stated);
    }

    [Fact]
    public void VerifyDigest_DetectsATamperedStatedSha()
    {
        var tampered = GoldenDeltaJson.Replace(GoldenSha, new string('0', 64));
        var m = DeltaManifestParser.Parse(tampered);

        var v = DeltaVerifier.VerifyDigest(m);
        Assert.False(v.Ok);                     // stated no longer matches…
        Assert.Equal(GoldenSha, v.Recomputed);  // …but what the bytes actually hash to is unchanged
    }

    [Fact]
    public void AssertSupportedVersion_RejectsUnknownVersion()
    {
        var m = new DeltaManifest(ManifestVersion: 2, Added: [], Removed: [], Renamed: [], Modified: [], DiffTruthSha: null);
        Assert.Throws<NotSupportedException>(() => DeltaVerifier.AssertSupportedVersion(m));
    }
}
