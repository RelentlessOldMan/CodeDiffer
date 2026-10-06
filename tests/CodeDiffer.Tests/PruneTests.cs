using System.Globalization;
using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>results --prune: which saved compares go, and that nothing else under the results root is touched.</summary>
public sealed class PruneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cd-prune-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    public PruneTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>A saved compare started <paramref name="daysAgo"/> days before <see cref="Now"/>.</summary>
    private string Saved(string id, double daysAgo, string state = "done")
    {
        var started = Now - TimeSpan.FromDays(daysAgo);
        var dir = Path.Combine(_root, $"{started:yyyyMMdd-HHmmss}-{id}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ResultStore.MetaName), $$"""
            { "format": "{{ResultStore.Format}}", "version": {{ResultStore.FormatVersion}}, "kind": "compare", "id": "{{id}}",
              "state": "{{state}}", "started": "{{started.ToString("O", CultureInfo.InvariantCulture)}}",
              "roots": { "left": "L", "right": "R" } }
            """);
        File.WriteAllText(Path.Combine(dir, "changes.jsonl"), new string('x', 1000));
        return dir;
    }

    private IEnumerable<string> Ids(TimeSpan? olderThan, int keep)
        => ResultStore.PruneCandidates(_root, keep, olderThan, Now).Select(c => c.Id);

    [Fact]
    public void KeepsTheNewest_AndOnlyOlderOnesWhenAsked()
    {
        Saved("aaa1", 30);
        Saved("aaa2", 10);
        Saved("aaa3", 5);
        Saved("aaa4", 1);
        Saved("aaa5", 0.1);

        Assert.Equal(["aaa3", "aaa2", "aaa1"], Ids(null, keep: 2));          // newest first
        Assert.Equal(["aaa2", "aaa1"], Ids(TimeSpan.FromDays(7), keep: 2));   // ...and older than 7 days
        Assert.Equal(["aaa1"], Ids(TimeSpan.FromDays(7), keep: 4));
        Assert.Empty(Ids(null, keep: 5));
        Assert.Equal(5, Ids(null, keep: 0).Count());
    }

    [Fact]
    public void LeavesARecentRunningCompare_AndAnythingThatIsNotAResult()
    {
        Saved("bbb1", 3, state: "running");     // stale: its process is long gone
        Saved("bbb2", 0.5, state: "running");   // may still be running in an MCP server
        Saved("bbb3", 2, state: "cancelled");
        var stranger = Path.Combine(_root, "20200101-000000-cccc");
        Directory.CreateDirectory(stranger);
        File.WriteAllText(Path.Combine(stranger, "notes.txt"), "not ours");

        var doomed = ResultStore.PruneCandidates(_root, 0, null, Now);
        Assert.Equal(["bbb3", "bbb1"], doomed.Select(c => c.Id));
        foreach (var c in doomed)
        {
            Assert.Equal(1000 + new FileInfo(Path.Combine(c.Dir, ResultStore.MetaName)).Length, ResultStore.SizeOf(c.Dir));
            ResultStore.Delete(_root, c);
            Assert.False(Directory.Exists(c.Dir));
        }
        Assert.True(Directory.Exists(stranger));
        Assert.Single(ResultStore.List(_root)); // bbb2
    }

    [Fact]
    public void Delete_RefusesADirectoryOutsideTheRoot()
    {
        var dir = Saved("ddd1", 1);
        var c = ResultStore.List(_root).Single();
        Assert.Throws<ArgumentException>(() => ResultStore.Delete(Path.Combine(_root, "elsewhere"), c));
        Assert.True(Directory.Exists(dir));
    }
}
