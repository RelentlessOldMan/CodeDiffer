using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Giant;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Report;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Code review pass 13, tier 3: a write into a result directory leaves it saved; a report or overlay directory that holds
/// the results directory, or can't be made, is refused before the compare; a block diff past its work cap says it is
/// coarse and can be cancelled; a directory past MAX_PATH has an identity and a real path; elapsed times keep their days.
/// </summary>
public sealed class Round13Tier3Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r13c-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public Round13Tier3Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), new UTF8Encoding(false).GetBytes(text));
    }

    // ---- a write leaves the result saved ----

    [Fact]
    public void AResultPrunedWhileADiffIsWritten_IsListedAfterIt()
    {
        string Many(string tag) => string.Concat(Enumerable.Range(1, 60).Select(i => i % 3 == 0 ? $"{tag} {i}\n" : $"line {i}\n"));
        Put("L/x.txt", Many("old"));
        Put("R/x.txt", Many("new"));
        var s = new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        // A prune that got as far as compare.json: only a diff written now would be left, never listed.
        var saved = Assert.Single(ResultStore.List(P("results")));
        ResultStore.Delete(P("results"), saved);
        AgentViews.FileDiff(s, "x.txt", maxLines: 5);
        Assert.Single(ResultStore.List(P("results")));
        Assert.True(File.Exists(Path.Combine(s.ResultDir!, ResultStore.MetaName)));
    }

    // ---- refused before the compare ----

    [Fact]
    public void AnOutputDirectoryHoldingTheResults_OrThatCantBeMade_IsRefusedFirst()
    {
        foreach (var t in new[] { "B", "V1", "V2" }) Put($"{t}/a.txt", "a\n");
        Put("afile", "x\n");
        var holder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(ResultStore.DefaultRoot)))!;
        Assert.Contains("holds the results directory", HtmlReport.OutDirProblem(holder, [P("B")]));
        Assert.Contains("holds the results directory", HtmlReport.OutDirProblem(ResultStore.DefaultRoot, [P("B")]));
        Assert.Contains("holds the results directory",
            Assert.Throws<ArgumentException>(() => MergeOverlay.CheckDir(holder, P("B"), P("V1"), P("V2"))).Message);

        Assert.Contains("is a file, so", HtmlReport.OutDirProblem(P("afile/report"), [P("B")]));
        if (OperatingSystem.IsWindows())
        {
            var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
            var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).FirstOrDefault(c => !used.Contains(c));
            if (free != default) Assert.Contains("isn't there", HtmlReport.OutDirProblem($@"{free}:\report", [P("B")]));
        }
        Assert.Null(HtmlReport.OutDirProblem(P("new/report"), [P("B")]));
    }

    // ---- a coarse block diff ----

    private static List<Chunk> Blocks(IEnumerable<string> shas)
        => shas.Select((s, i) => new Chunk((long)i * 65536, 65536, s)).ToList();

    [Fact]
    public void ABlockDiffPastItsWorkCap_SaysItIsCoarse_AndCanBeStopped()
    {
        const int n = 60_000;
        var old = Blocks(Enumerable.Range(0, n).Select(i => "b" + i));
        var scattered = Blocks(Enumerable.Range(0, n).Select(i => "b" + (i % 2 == 0 ? i * 7919 % n : i))); // every other block moved
        var r = GiantFileDiffer.Diff(old, scattered);
        Assert.True(r.Coarse);
        Assert.False(GiantFileDiffer.Diff(old, Blocks(Enumerable.Range(0, n).Select(i => i == 7 ? "c" : "b" + i))).Coarse);

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => GiantFileDiffer.Diff(old, scattered, stop.Token));
    }

    // ---- long paths and identities ----

    [Fact]
    public void ADirectoryPastMaxPath_HasAnIdentity_AndIsInsideItself()
    {
        if (!OperatingSystem.IsWindows()) return;
        var deep = P(string.Join('/', Enumerable.Repeat(new string('d', 40), 7)));
        Directory.CreateDirectory(deep);
        Assert.True(deep.Length > 260);
        Assert.NotNull(RealPath.Identity(deep));
        Assert.True(RealPath.InsideByIdentity(Path.Combine(deep, "x"), deep));
        Assert.Equal(deep, RealPath.Of(deep), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(@"\\?\UNC\srv\share\" + new string('x', 300), RealPath.Long(@"\\srv\share\" + new string('x', 300)));
        Assert.Equal(@"C:\short", RealPath.Long(@"C:\short"));
    }

    // ---- days kept ----

    [Fact]
    public void AnElapsedTimeOfDays_KeepsThem()
        => Assert.Equal("26:03:04", ProgressView.Clock(new TimeSpan(1, 2, 3, 4)));
}
