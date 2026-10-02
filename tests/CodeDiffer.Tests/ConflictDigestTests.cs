using CodeDiffer.Core.DiffTruth;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Verify;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The 3-way cross-tool gate: reproduce CodeSpawner's frozen conflictTruthSha golden vector (its
/// digest-selftest, Program.cs) — out-of-order records, a modify/delete conflict (non-replace op), and
/// both clean sides. If this matches, CodeDiffer's ConflictDigest is byte-compatible with CodeSpawner's.
/// </summary>
public class ConflictDigestTests
{
    private const string GoldenConflictSha = "68cd14ac9a54521fc967f8c4632536bb9f0725cd394b8296cd1d62d4a9310e6a";

    private static readonly List<Conflict> Conflicts =
    [
        new("f.c", 5, 1, HunkOp.Replace, 5, 1, HunkOp.Replace, 5, 1),
        new("a.c", 9, 2, HunkOp.Replace, 9, 2, HunkOp.Delete, 9, 0),
    ];

    private static readonly List<CleanMerge> Clean =
    [
        new("f.c", "v1", HunkOp.Replace, 3, 1, 3, 1),
        new("a.c", "v2", HunkOp.Insert, 7, 0, 7, 2),
    ];

    [Fact]
    public void GoldenVector_ReproducesCodeSpawnerConflictTruthSha()
        => Assert.Equal(GoldenConflictSha, ConflictDigest.Compute(Conflicts, Clean));

    [Fact]
    public void Parse_ReproducesDigest_ThroughTheParser()
    {
        // CodeSpawner's conflict-3way JSON shape verbatim, carrying the golden input.
        const string json = """
        {
          "_meta": { "manifestVersion": 1, "deltaKind": "conflict-3way",
                     "v1Tree": "base_v1", "v2Tree": "base_v2",
                     "conflictTruthSha": "68cd14ac9a54521fc967f8c4632536bb9f0725cd394b8296cd1d62d4a9310e6a" },
          "conflicts": [
            { "path": "f.c", "baseStart": 5, "baseLines": 1,
              "v1": { "op": "replace", "newStart": 5, "newLines": 1 },
              "v2": { "op": "replace", "newStart": 5, "newLines": 1 } },
            { "path": "a.c", "baseStart": 9, "baseLines": 2,
              "v1": { "op": "replace", "newStart": 9, "newLines": 2 },
              "v2": { "op": "delete",  "newStart": 9, "newLines": 0 } }
          ],
          "mergedClean": [
            { "path": "f.c", "side": "v1", "op": "replace", "oldStart": 3, "oldLines": 1, "newStart": 3, "newLines": 1 },
            { "path": "a.c", "side": "v2", "op": "insert",  "oldStart": 7, "oldLines": 0, "newStart": 7, "newLines": 2 }
          ]
        }
        """;

        var manifest = ConflictManifestParser.Parse(json);
        Assert.Equal(1, manifest.ManifestVersion);
        Assert.Equal(2, manifest.Conflicts.Count);
        Assert.Equal(2, manifest.CleanMerges.Count);
        Assert.Equal(HunkOp.Delete, manifest.Conflicts.Single(c => c.Path == "a.c").V2Op);

        var v = DeltaVerifier.VerifyConflictDigest(manifest);
        Assert.True(v.Ok);
        Assert.Equal(GoldenConflictSha, v.Recomputed);
    }
}
