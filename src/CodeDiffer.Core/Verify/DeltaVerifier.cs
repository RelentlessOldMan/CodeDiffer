using CodeDiffer.Core.DiffTruth;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Verify;

/// <summary>Result of reproducing a delta's <c>diffTruthSha</c> independently.</summary>
public sealed record DigestVerification(bool Ok, string? Stated, string Recomputed);

/// <summary>
/// The CodeSpawner-side verify-adapter's core checks: assert the manifest version we support, and
/// independently reproduce <c>diffTruthSha</c> from the four canonical sections — the cross-tool gate
/// (CodeSpawner emits, CodeDiffer reproduces; a mismatch means the contract drifted). Composing
/// base ⊕ delta and asserting CodeDiffer's own diff output equals the manifest comes once the line-hunk
/// differ lands.
/// </summary>
public static class DeltaVerifier
{
    public const int SupportedManifestVersion = 1;

    /// <summary>Hard-assert the manifest version before trusting anything else (the contract's first rule).</summary>
    public static void AssertSupportedVersion(DeltaManifest manifest)
    {
        if (manifest.ManifestVersion != SupportedManifestVersion)
            throw new NotSupportedException(
                $"unsupported manifestVersion {manifest.ManifestVersion} (CodeDiffer supports {SupportedManifestVersion})");
    }

    /// <summary>Recompute diffTruthSha and compare to the stated value. Ok iff they match (both present).</summary>
    public static DigestVerification VerifyDigest(DeltaManifest manifest)
    {
        var recomputed = DiffTruthDigest.Compute(manifest);
        var ok = manifest.DiffTruthSha is not null
                 && string.Equals(recomputed, manifest.DiffTruthSha, StringComparison.Ordinal);
        return new DigestVerification(ok, manifest.DiffTruthSha, recomputed);
    }

    /// <summary>Recompute conflictTruthSha for a 3-way artifact and compare to the stated value.</summary>
    public static DigestVerification VerifyConflictDigest(ConflictManifest manifest)
    {
        if (manifest.ManifestVersion != SupportedManifestVersion)
            throw new NotSupportedException(
                $"unsupported manifestVersion {manifest.ManifestVersion} (CodeDiffer supports {SupportedManifestVersion})");

        var recomputed = ConflictDigest.Compute(manifest);
        var ok = manifest.ConflictTruthSha is not null
                 && string.Equals(recomputed, manifest.ConflictTruthSha, StringComparison.Ordinal);
        return new DigestVerification(ok, manifest.ConflictTruthSha, recomputed);
    }
}
