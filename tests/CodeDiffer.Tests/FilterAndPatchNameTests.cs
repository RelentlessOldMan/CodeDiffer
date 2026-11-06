using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// A path filter's terms split on ';' and ',' except inside a /regex/'s brackets, and a patch names a path the way git
/// does: as it is, or C-quoted when it holds a quote, a backslash or a control character.
/// </summary>
public class FilterAndPatchNameTests
{
    [Theory]
    [InlineData(@"/a{1,3}\.c/", new[] { @"/a{1,3}\.c/" })]
    [InlineData("/[,;]/", new[] { "/[,;]/" })]
    [InlineData("/[(]x/,*.h", new[] { "/[(]x/", "*.h" })]
    [InlineData("/src/*.c,/lib/", new[] { "/src/*.c", "/lib/" })]
    [InlineData(@"/a\,b/;*.c", new[] { @"/a\,b/", "*.c" })]
    [InlineData("*.c; *.h; !third_party/**", new[] { "*.c", " *.h", " !third_party/**" })]
    public void Terms_SplitOnlyOutsideARegexsBrackets(string pattern, string[] expected)
        => Assert.Equal(expected, AgentViews.Terms(pattern));

    [Fact]
    public void ARegexWithACommaInItsCount_StillFilters()
    {
        Assert.True(AgentViews.TryPathFilter(@"/^src/a{1,3}\.c$/", out var f, out var err), err);
        Assert.True(f("src/aa.c", null));
        Assert.False(f("src/aaaa.c", null));
    }

    [Theory]
    [InlineData("a/", "src/x.c", "a/src/x.c")]
    [InlineData("b/", "dir with space/x.c", "b/dir with space/x.c")]
    [InlineData("a/", "q\"uote.c", "\"a/q\\\"uote.c\"")]
    [InlineData("a/", "back\\slash.c", "\"a/back\\\\slash.c\"")]
    [InlineData("b/", "new\nline.c", "\"b/new\\nline.c\"")]
    [InlineData("b/", "tab\there.c", "\"b/tab\\there.c\"")]
    [InlineData("", "bell\u0007.c", "\"bell\\007.c\"")]
    public void GitName_QuotesOnlyWhatGitQuotes(string prefix, string path, string expected)
        => Assert.Equal(expected, UnifiedDiff.GitName(prefix, path));

    [Fact]
    public void APatchHeader_NeverLetsANewlineInANameStartALine()
    {
        var w = new StringWriter();
        UnifiedDiff.Write(w, "x\n+++ b/evil", "x\n+++ b/evil", "a\n", "b\n");
        Assert.DoesNotContain(w.ToString().Split('\n'), l => l == "+++ b/evil\"");
        Assert.StartsWith("diff --git \"a/x\\n+++ b/evil\" \"b/x\\n+++ b/evil\"\n", w.ToString());
    }
}
