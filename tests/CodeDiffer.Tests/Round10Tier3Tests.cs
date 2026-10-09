using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Review round 10, tier 3 (and round 9's leftovers): a legacy file re-saved as UTF-8 is `encoding` at any size; a path
/// shown with U+FFFD finds its file; a listing's hunk count is the default context's; a saved diff never collides with a
/// directory; reason=unreadable lists the unreadable; export's out_path stays out of the trees.
/// </summary>
public sealed class Round10Tier3Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r10c-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };
    private static readonly string Ten = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));

    public Round10Tier3Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string rel, string text) => PutBytes(rel, new UTF8Encoding(false).GetBytes(text));

    private void PutBytes(string rel, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), bytes);
    }

    private CompareSession Compare()
    {
        var s = new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Null(s.Error);
        return s;
    }

    [Theory]
    [InlineData("25°C\nok\n", "25°C\nok\n", ChangeReason.Encoding)]
    [InlineData("25°C\nok\n", "25°C\r\nok\r\n", ChangeReason.Eol)]
    [InlineData("25°C\nok\n", "25°C\n  ok\n", ChangeReason.Whitespace)]
    [InlineData("25°C\nok\n", "25±C\nok\n", ChangeReason.Content)]
    public void ALegacyFileResavedAsUtf8_HasTheSameReasonWholeOrStreamed(string latin1, string utf8, ChangeReason expected)
    {
        PutBytes("L/t.txt", Encoding.Latin1.GetBytes(latin1));
        PutBytes("R/t.txt", Encoding.UTF8.GetBytes(utf8));
        long l = new FileInfo(P("L/t.txt")).Length, r = new FileInfo(P("R/t.txt")).Length;
        Assert.Equal(expected, new ReasonClassifier().Classify(P("L/t.txt"), l, P("R/t.txt"), r));
        Assert.Equal(expected, new ReasonClassifier(maxClassifyBytes: 4).Classify(P("L/t.txt"), l, P("R/t.txt"), r));
    }

    [Fact]
    public void APathShownWithUfffd_FindsItsFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var odd = "x\uD800.txt"; // an unpaired high surrogate: a legal NTFS name
        Put("L/" + odd, Ten);
        Put("R/" + odd, Ten.Replace("line 2", "line two"));
        var s = Compare();
        var view = AgentViews.FileDiff(s, "x�.txt");
        Assert.DoesNotContain("not in compare", view);
        Assert.Contains("+line two", view);
    }

    [Fact]
    public void AListingsHunkCount_IsTheDefaultContexts()
    {
        Put("L/a.c", string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i}\n")));
        Put("R/a.c", string.Concat(Enumerable.Range(1, 40).Select(i => i is 5 or 20 ? $"edited {i}\n" : $"line {i}\n")));
        var s = Compare();
        Assert.Contains("in 1 hunk(s)", AgentViews.FileDiff(s, "a.c", context: 10));
        Assert.False(s.DiffInfo.ContainsKey("a.c"));
        Assert.Contains("in 2 hunk(s)", AgentViews.FileDiff(s, "a.c"));
        Assert.Equal(2, s.DiffInfo["a.c"].Hunks);
    }

    [Fact]
    public void ASavedDiff_NeverCollidesWithADirectory()
    {
        var big = string.Concat(Enumerable.Range(1, 60).Select(i => $"line {i}\n"));
        var edited = string.Concat(Enumerable.Range(1, 60).Select(i => $"LINE {i}\n"));
        Put("L/a", big); Put("R/a", edited);
        Put("L/a.patch/x", big); Put("R/a.patch/x", edited);
        var s = Compare();
        var dirFirst = AgentViews.FileDiff(s, "a.patch/x", maxLines: 5);
        var fileNext = AgentViews.FileDiff(s, "a", maxLines: 5);
        string Saved(string view) => view.Split('\n').Single(l => l.StartsWith("capped:"))[(view.Split('\n').Single(l => l.StartsWith("capped:")).IndexOf("full patch: ") + 12)..];
        Assert.True(File.Exists(Saved(dirFirst)));
        Assert.True(File.Exists(Saved(fileNext)));
        Assert.Contains("_clash", Saved(fileNext));
        Assert.Contains("-line 1", File.ReadAllText(Saved(fileNext)));
    }

    [Fact]
    public void ReasonUnreadable_ListsTheUnreadable()
    {
        if (!OperatingSystem.IsWindows()) return; // a lock that refuses readers is a Windows share mode
        Put("L/a.c", Ten);
        Put("R/a.c", Ten.Replace("line 1", "line one"));
        Put("L/b.c", Ten);
        Put("R/b.c", Ten.Replace("line 1", "line uno"));
        CompareSession s;
        using (new FileStream(P("R/b.c"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            s = Compare();
        var list = AgentViews.ListFiles(s, reason: "unreadable");
        Assert.DoesNotContain("error", list);
        Assert.Contains("b.c", list);
        Assert.DoesNotContain("a.c", list);
    }

    [Fact]
    public void ExportsOutPath_StaysOutOfTheTrees()
    {
        Put("L/a.c", Ten);
        Put("R/a.c", Ten.Replace("line 1", "line one"));
        var s = Compare();
        Assert.StartsWith("error:", AgentViews.Export(s, P("R/changes.patch")));
        Assert.False(File.Exists(P("R/changes.patch")));
        Assert.StartsWith("error:", AgentViews.Export(s, P("elsewhere/x.patch"), context: -1));
        Assert.DoesNotContain("error", AgentViews.Export(s, P("elsewhere/x.patch")));
        Assert.True(File.Exists(P("elsewhere/x.patch")));
    }
}
