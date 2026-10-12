using System.Diagnostics;
using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using CodeDiffer.Core.Verify;
using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Review round 12, tier 2: a slow line diff is cut short sooner and can be cancelled; edited renames among thousands
/// of alike files keep only each file's best candidates and still pair as before; a run rule claiming 2³¹ lines is
/// checked as it streams; a merge-out that can't be made is refused before the compare; a result pruned by another
/// process is saved again before anything is written into it.
/// </summary>
public sealed class Round12Tier2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r12b-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public Round12Tier2Tests() => Directory.CreateDirectory(_dir);

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

    // ---- A3: the line diff's work cap and cancellation ----

    [Fact]
    public void ALineDiff_StopsWhenCancelled()
    {
        var rnd = new Random(3);
        string[] Gen() => Enumerable.Range(0, 50_000).Select(_ => "l" + rnd.Next(20)).ToArray();
        var a = Gen();
        var b = Gen();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => LineDiffer.Diff(a, b, 0, out _, ct: cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        Assert.True(LineDiffer.DefaultMaxWork <= 200_000_000);
    }

    // ---- A4: edited renames keep each file's best candidates ----

    [Fact]
    public void EditedRenames_PairTheSame_WithOneCandidateKeptAsWithAll()
    {
        var rnd = new Random(12);
        for (int round = 0; round < 6; round++)
        {
            var root = P($"ren{round}");
            int n = 30;
            for (int i = 0; i < n; i++)
            {
                // Few distinct lines: many pairs over the threshold, many ties, rows whose best candidates go elsewhere.
                var lines = Enumerable.Range(0, rnd.Next(3, 9)).Select(_ => "x" + rnd.Next(5)).ToList();
                Put($"ren{round}/L/{(rnd.Next(2) == 0 ? "a" : "b")}/f{i}.txt", string.Join("\n", lines) + "\n");
                if (rnd.Next(3) > 0) lines[rnd.Next(lines.Count)] = "y" + rnd.Next(3);
                if (rnd.Next(2) == 0) lines.Add("z" + rnd.Next(4));
                Put($"ren{round}/R/{(rnd.Next(2) == 0 ? "a" : "c")}/{(rnd.Next(4) == 0 ? "f" : "g")}{rnd.Next(n)}-{i}.txt", string.Join("\n", lines) + "\n");
            }
            var removed = new TreeWalker().Walk(Path.Combine(root, "L")).ToList();
            var added = new TreeWalker().Walk(Path.Combine(root, "R")).ToList();
            var opt = new CompareOptions { Cache = CacheMode.Off };
            var all = new RenameDetector(opt) { TopCandidates = int.MaxValue }.Detect(removed, added);
            var one = new RenameDetector(opt) { TopCandidates = 1 }.Detect(removed, added);
            Assert.NotEmpty(all.Renames);
            Assert.Equal(all.Renames, one.Renames);
        }
    }

    // ---- B7: a run rule is checked as it streams ----

    [Fact]
    public void ARunRuleClaimingEveryLineToTheIntLimit_FailsWithoutExpanding()
    {
        Put("b/g.txt", string.Concat(Enumerable.Range(1, 12).Select(i => $"line {i}\n")));
        Put("v/g.txt", string.Concat(Enumerable.Range(1, 12).Select(i => i % 2 == 1 && i < 10 || i == 12 ? $"edit {i}\n" : $"line {i}\n")));
        DeltaManifest M(int rangeEnd) => new(1, [], [], [],
            [new FileDelta("g.txt", ChangeReason.Content, "", "", 0, 0, [new Hunk(HunkOp.Replace, 12, 1, 12, 1)],
                [new RunHunk(HunkOp.Replace, 2, 1, rangeEnd, 1)])], DiffTruthSha: null);

        var good = DeltaTreeCrossCheck.Run(P("b"), P("v"), M(9)).Files.Single();
        Assert.True(good.Reconstructs, good.Problem);

        var sw = Stopwatch.StartNew();
        var bad = DeltaTreeCrossCheck.Run(P("b"), P("v"), M(int.MaxValue)).Files.Single();
        Assert.False(bad.Reconstructs);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }

    // ---- C3: a merge-out that can't be made ----

    [Fact]
    public void AMergeOutThatCantBeMade_IsRefusedBeforeTheCompare()
    {
        foreach (var t in new[] { "B", "V1", "V2" }) Put($"{t}/a.txt", "a\n");
        Put("afile", "x\n");
        void Check(string dir, string want)
            => Assert.Contains(want, Assert.Throws<ArgumentException>(() => MergeOverlay.CheckDir(dir, P("B"), P("V1"), P("V2"))).Message);
        Check(P("afile"), "is a file;");
        Check(P("afile/sub/deeper"), "is a file, so");
        if (OperatingSystem.IsWindows())
        {
            var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
            var free = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).FirstOrDefault(c => !used.Contains(c));
            if (free != default) Check($@"{free}:\merge", "isn't there");
        }
        MergeOverlay.CheckDir(P("new/merge"), P("B"), P("V1"), P("V2")); // new directories under one that exists: fine
    }

    // ---- C5: a result pruned by another process ----

    [Fact]
    public void AResultPrunedElsewhere_IsSavedAgainBeforeAnythingGoesIntoIt()
    {
        Put("L/x.txt", "a\n");
        Put("R/x.txt", "b\n");
        var s = new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Null(s.Error);
        var dir = s.ResultDir!;
        Directory.Delete(dir, true); // another CodeDiffer's prune
        Assert.Equal(dir, AgentViews.OutDir(s));
        Assert.True(File.Exists(Path.Combine(dir, ResultStore.MetaName)));
        Assert.True(File.Exists(Path.Combine(dir, "changes.jsonl")));
        var reopened = (CompareSession)new SessionStore(resultsRoot: P("results")).Get(s.Id)!;
        Assert.Single(reopened.Report!.Changes, c => c.Status == ChangeStatus.Modified);
    }
}
