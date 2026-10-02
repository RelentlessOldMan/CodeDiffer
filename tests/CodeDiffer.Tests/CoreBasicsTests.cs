using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Hashing;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

public class ContentHasherTests
{
    [Fact]
    public void HashBytes_KnownVector_Abc()
    {
        // The canonical SHA-256("abc") test vector.
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ContentHasher.HashBytes(Encoding.ASCII.GetBytes("abc")));
    }

    [Fact]
    public void HashStream_MatchesHashBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("the quick brown fox\n");
        using var stream = new MemoryStream(bytes);
        Assert.Equal(ContentHasher.HashBytes(bytes), ContentHasher.HashStream(stream));
    }

    [Fact]
    public void HashFile_MatchesBytes_AndStreams()
    {
        // Larger than the stream buffer, to exercise the multi-chunk path.
        var payload = new byte[ContentHasher.StreamBufferBytes * 2 + 123];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + 7);

        var path = Path.Combine(Path.GetTempPath(), $"codediffer-hash-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, payload);
            Assert.Equal(ContentHasher.HashBytes(payload), ContentHasher.HashFile(path));
        }
        finally { File.Delete(path); }
    }
}

public class InputValidationTests
{
    [Fact]
    public void MissingPath_IsHardError_NotSilentSuccess()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"codediffer-nope-{Guid.NewGuid():N}");
        var real = Path.GetTempPath();
        // Regression: the prototype reported success (all-added/all-deleted) on a missing path.
        Assert.Throws<DirectoryNotFoundException>(() => InputValidation.ValidateTrees(missing, real));
        Assert.Throws<DirectoryNotFoundException>(() => InputValidation.ValidateTrees(real, missing));
    }

    [Fact]
    public void EmptyPath_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => InputValidation.ValidateTrees("", Path.GetTempPath()));
    }

    [Fact]
    public void ExistingDirectories_Pass()
    {
        InputValidation.ValidateTrees(Path.GetTempPath(), Path.GetTempPath()); // no throw
    }
}

public class CanonicalTokenTests
{
    [Theory]
    [InlineData(ChangeReason.Content, "content")]
    [InlineData(ChangeReason.Eol, "eol")]
    [InlineData(ChangeReason.Whitespace, "whitespace")]
    [InlineData(ChangeReason.Encoding, "encoding")]
    [InlineData(ChangeReason.Binary, "binary")]
    [InlineData(ChangeReason.Metadata, "metadata")]
    public void ReasonTokens_RoundTrip(ChangeReason reason, string token)
    {
        Assert.Equal(token, CanonicalTokens.Token(reason));
        Assert.Equal(reason, CanonicalTokens.Reason(token));
    }

    [Theory]
    [InlineData(HunkOp.Insert, "insert")]
    [InlineData(HunkOp.Delete, "delete")]
    [InlineData(HunkOp.Replace, "replace")]
    public void OpTokens_RoundTrip(HunkOp op, string token)
    {
        Assert.Equal(token, CanonicalTokens.Token(op));
        Assert.Equal(op, CanonicalTokens.Op(token));
    }
}

public class RunHunkTests
{
    [Fact]
    public void Expand_StridesInclusive_ProducesExpectedLines()
    {
        var run = new RunHunk(HunkOp.Replace, Stride: 20, RangeStart: 1, RangeEnd: 41, PerHunk: 1);
        var lines = run.Expand().Select(h => h.OldStart).ToArray();
        Assert.Equal(new[] { 1, 21, 41 }, lines);
        Assert.All(run.Expand(), h =>
        {
            Assert.Equal(HunkOp.Replace, h.Op);
            Assert.Equal(1, h.OldLines);
            Assert.Equal(1, h.NewLines);
            Assert.Equal(h.OldStart, h.NewStart);
        });
    }

    [Fact]
    public void Expand_ZeroStride_Throws()
    {
        var bad = new RunHunk(HunkOp.Replace, Stride: 0, RangeStart: 1, RangeEnd: 10, PerHunk: 1);
        Assert.Throws<InvalidOperationException>(() => bad.Expand().ToArray());
    }
}
