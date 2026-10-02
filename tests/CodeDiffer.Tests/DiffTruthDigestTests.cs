using System.Security.Cryptography;
using CodeDiffer.Core.DiffTruth;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Guards the diffTruthSha canonicalization mechanics until CodeSpawner ships its digest-selftest
/// golden vector (at which point a byte-for-byte reproduction test joins these). Covers the properties
/// the locked form promises: deterministic, order-independent, reason/sha-sensitive, and the all-empty
/// vector (three GS bytes, no special-casing).
/// </summary>
public class DiffTruthDigestTests
{
    private static FileDelta Mod(string path, ChangeReason reason, params Hunk[] hunks) =>
        new(path, reason, OldSha: "00aa", NewSha: "11bb", OldSize: 100, NewSize: 110,
            Hunks: hunks, RunHunks: []);

    private static DeltaManifest Manifest(IReadOnlyList<FileDelta> modified, IReadOnlyList<RenameOp> renamed) =>
        new(ManifestVersion: 1, Added: [], Removed: [], Renamed: renamed, Modified: modified, DiffTruthSha: null);

    private static readonly FileDelta F1 = Mod("block1/src_0.c", ChangeReason.Content, new Hunk(HunkOp.Replace, 10, 1, 10, 1));
    private static readonly FileDelta F2 = Mod("block2/src_1.c", ChangeReason.Eol);
    private static readonly RenameOp R1 = new("old/a.c", "new/b.c", 900);

    [Fact]
    public void Deterministic_SameInput_SameHash()
    {
        var a = DiffTruthDigest.Compute(Manifest([F1, F2], [R1]));
        var b = DiffTruthDigest.Compute(Manifest([F1, F2], [R1]));
        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void OrderIndependent_RecordOrderDoesNotMatter()
    {
        var forward = DiffTruthDigest.Compute(Manifest([F1, F2], [R1]));
        var reversed = DiffTruthDigest.Compute(Manifest([F2, F1], [R1]));
        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void ReasonFlip_ChangesHash()
    {
        var content = DiffTruthDigest.Compute(Manifest([Mod("x.c", ChangeReason.Content)], []));
        var eol = DiffTruthDigest.Compute(Manifest([Mod("x.c", ChangeReason.Eol)], []));
        Assert.NotEqual(content, eol);
    }

    [Fact]
    public void ShaFlip_ChangesHash()
    {
        var baseline = DiffTruthDigest.Compute(Manifest([F1], []));
        var tweaked = DiffTruthDigest.Compute(Manifest(
            [F1 with { NewSha = "deadbeef" }], []));
        Assert.NotEqual(baseline, tweaked);
    }

    [Fact]
    public void EmptyManifest_IsSha256OfThreeGroupSeparators()
    {
        // All four sections empty => the digest input is just the three GS boundaries between them.
        var expected = Convert.ToHexString(
            SHA256.HashData([DiffTruthDigest.GS, DiffTruthDigest.GS, DiffTruthDigest.GS])).ToLowerInvariant();
        var actual = DiffTruthDigest.Compute(Manifest([], []));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DuplicateRecords_CollapseBeforeHashing()
    {
        var once = DiffTruthDigest.Compute(Manifest([F1], []));
        var twice = DiffTruthDigest.Compute(Manifest([F1, F1], [])); // identical record deduped
        Assert.Equal(once, twice);
    }
}
