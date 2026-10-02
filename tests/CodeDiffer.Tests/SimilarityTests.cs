using CodeDiffer.Core.Diff;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The rename-similarity metric, pinned to the CodeSpawner contract: sim = commonLines/max over
/// EOL-normalized lines, MULTISET intersection, similarityMilli = round(sim*1000). These are the exact
/// values CodeSpawner stamps into every <c>renamed[].similarityMilli</c>, so they double as the
/// cross-tool check for the metric itself (independent of detection policy).
/// </summary>
public class SimilarityTests
{
    [Fact]
    public void IdenticalText_Is1000()
        => Assert.Equal(1000, Similarity.Milli("a\nb\nc\n", "a\nb\nc\n"));

    [Fact]
    public void BothEmpty_Is1000() // nothing to differ ⇒ defined identical
        => Assert.Equal(1000, Similarity.Milli("", ""));

    [Fact]
    public void Disjoint_Is0()
        => Assert.Equal(0, Similarity.Milli("a\nb\nc\n", "x\ny\nz\n"));

    [Fact]
    public void OneOfTenChanged_Is900() // common 9 / max 10
        => Assert.Equal(900, Similarity.Milli(
            "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\n",
            "l1\nl2\nl3\nl4\nXX\nl6\nl7\nl8\nl9\nl10\n"));

    [Fact]
    public void FewerLines_MaxIsLargerSide() // 7 common lines, old has 10 ⇒ 700
        => Assert.Equal(700, Similarity.Milli(
            "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\n",
            "l1\nl2\nl3\nl4\nl5\nl6\nl7\n"));

    [Fact]
    public void MultisetIntersection_CountsSharedDuplicatesOnce()
    {
        // old = {a,a,b}, new = {a,b,b} ⇒ common = min(2,1)+min(1,2) = 2, max = 3 ⇒ round(2/3*1000)=667.
        Assert.Equal(667, Similarity.Milli("a\na\nb\n", "a\nb\nb\n"));
    }

    [Fact]
    public void EolDifferencesAreNormalizedAway() // CRLF vs LF ⇒ fully similar
        => Assert.Equal(1000, Similarity.Milli("a\r\nb\r\nc\r\n", "a\nb\nc\n"));
}
