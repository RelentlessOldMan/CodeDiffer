using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Port;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// 3-way tree compare: every outcome and conflict kind, renames followed across sides, the diff3-marker
/// merge view, and a cross-check that a clean merge equals porting base→v1 onto v2.
/// </summary>
public class TreeMergerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-3way-" + Guid.NewGuid().ToString("N"));
    private string B => Path.Combine(_dir, "B");
    private string V1 => Path.Combine(_dir, "V1");
    private string V2 => Path.Combine(_dir, "V2");

    public TreeMergerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Put(string rel, string text) => PutBytes(rel, new UTF8Encoding(false).GetBytes(text));

    private void PutBytes(string rel, byte[] bytes)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    /// <summary>The same file in all three trees.</summary>
    private void All3(string rel, string text) { Put("B/" + rel, text); Put("V1/" + rel, text); Put("V2/" + rel, text); }

    private static string Lines(int from, int to, string prefix = "line") =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"{prefix} {i}\n"));

    private static string Edit(string text, int line, string with)
    {
        var l = text.Split('\n');
        l[line - 1] = with;
        return string.Join('\n', l);
    }

    private ThreeWayReport Run() => TreeMerger.Run(B, V1, V2, new CompareOptions { Cache = CacheMode.Off });

    private static Merge3Entry E(ThreeWayReport r, string path) => r.Entries.Single(e => e.Path == path);

    [Fact]
    public void EveryOutcome_IsClassified()
    {
        var t = Lines(1, 30);
        All3("same.c", t);
        Put("B/one1.c", t); Put("V1/one1.c", Edit(t, 5, "v1")); Put("V2/one1.c", t);
        Put("B/one2.c", t); Put("V1/one2.c", t); Put("V2/one2.c", Edit(t, 5, "v2"));
        Put("B/agree.c", t); Put("V1/agree.c", Edit(t, 5, "both")); Put("V2/agree.c", Edit(t, 5, "both"));
        Put("B/merge.c", t); Put("V1/merge.c", Edit(t, 5, "v1")); Put("V2/merge.c", Edit(t, 20, "v2"));
        Put("B/clash.c", t); Put("V1/clash.c", Edit(t, 5, "v1")); Put("V2/clash.c", Edit(t, 5, "v2"));
        Put("B/moddel.c", t); Put("V1/moddel.c", Edit(t, 5, "v1"));                       // v2 deleted it
        Put("B/gone.c", t);                                                                // both deleted
        Put("V1/addsame.c", "x\n"); Put("V2/addsame.c", "x\n");
        Put("V1/adddiff.c", "x\n"); Put("V2/adddiff.c", "y\n");
        PutBytes("B/bin.dat", [0, 1, 2]); PutBytes("V1/bin.dat", [0, 9, 2]); PutBytes("V2/bin.dat", [0, 1, 7]);

        var r = Run();
        Assert.DoesNotContain(r.Entries, e => e.Path == "same.c");
        Assert.Equal(Merge3Outcome.V1Only, E(r, "one1.c").Outcome);
        Assert.Equal(Merge3Outcome.V2Only, E(r, "one2.c").Outcome);
        Assert.Equal(Merge3Outcome.Agreed, E(r, "agree.c").Outcome);
        Assert.Equal(Merge3Outcome.Merged, E(r, "merge.c").Outcome);
        Assert.Equal(2, E(r, "merge.c").CleanRegions);
        Assert.Equal(("content", 1), (E(r, "clash.c").ConflictKind, E(r, "clash.c").ConflictRegions));
        Assert.Equal("modify/delete", E(r, "moddel.c").ConflictKind);
        Assert.Equal(Merge3Outcome.Agreed, E(r, "gone.c").Outcome);
        Assert.Equal(Merge3Outcome.Agreed, E(r, "addsame.c").Outcome);
        Assert.Equal("add/add", E(r, "adddiff.c").ConflictKind);
        Assert.Equal("binary", E(r, "bin.dat").ConflictKind);
    }

    [Fact]
    public void RenameOnOneSide_EditOnTheOther_MergesAtTheNewName()
    {
        var t = Lines(1, 40, "mv");
        Put("B/old.c", t);
        Put("V1/new.c", t);                         // pure rename in v1
        Put("V2/old.c", Edit(t, 10, "edited"));     // edit in v2
        Put("B/r.c", t); Put("V1/r1.c", t); Put("V2/r2.c", t); // renamed to different names

        var r = Run();
        var e = E(r, "old.c");
        Assert.Equal(Merge3Outcome.Merged, e.Outcome);
        Assert.Equal("new.c", e.MergedPath);
        Assert.Equal(Edit(t, 10, "edited"), TreeMerger.MergedText(e, B, V1, V2));
        Assert.Equal("rename/rename", E(r, "r.c").ConflictKind);
    }

    [Fact]
    public void TwoChangesLandingOnOnePath_AreACollision()
    {
        var t = Lines(1, 40, "c");
        Put("B/a.c", t);
        Put("V1/dest.c", t);                            // v1 renames a.c -> dest.c
        Put("V2/a.c", t); Put("V2/dest.c", "other\n");  // v2 adds an unrelated dest.c
        var r = Run();
        Assert.All(r.Entries.Where(x => x.MergedPath == "dest.c"), x => Assert.Equal("path collision", x.ConflictKind));
    }

    [Fact]
    public void MergedText_ShowsDiff3Markers_ForConflicts()
    {
        var t = Lines(1, 30);
        Put("B/c.c", t);
        Put("V1/c.c", Edit(Edit(t, 5, "ONE"), 25, "far"));
        Put("V2/c.c", Edit(t, 5, "TWO"));
        var r = Run();
        var text = TreeMerger.MergedText(E(r, "c.c"), B, V1, V2)!;
        Assert.Contains("<<<<<<< v1 (c.c:5)\nONE\n||||||| base (c.c:5)\nline 5\n=======\nTWO\n>>>>>>> v2 (c.c:5)\n", text);
        Assert.Contains("far\n", text); // the clean v1-only region is resolved
    }

    [Fact]
    public void CleanMerge_EqualsPortingV1OntoV2()
    {
        var rng = new Random(5);
        for (int f = 0; f < 40; f++)
        {
            var baseLines = Enumerable.Range(1, 60).Select(i => $"b{f}-{i}").ToArray();
            // v1 edits odd lines in the first half, v2 in the second: always separable.
            var a = (string[])baseLines.Clone();
            var b = (string[])baseLines.Clone();
            for (int k = rng.Next(1, 6); k > 0; k--) a[rng.Next(0, 14) * 2] = $"v1-{k}";
            for (int k = rng.Next(1, 6); k > 0; k--) b[30 + rng.Next(0, 14) * 2] = $"v2-{k}";
            Put($"B/f{f}.txt", string.Join('\n', baseLines) + "\n");
            Put($"V1/f{f}.txt", string.Join('\n', a) + "\n");
            Put($"V2/f{f}.txt", string.Join('\n', b) + "\n");
        }
        var r = Run();
        Assert.All(r.Entries, e => Assert.Equal(Merge3Outcome.Merged, e.Outcome));
        var merged = r.Entries.ToDictionary(e => e.Path, e => TreeMerger.MergedText(e, B, V1, V2));

        // Port base->v1 onto (a copy of) v2: must produce exactly the merge result.
        var copy = Path.Combine(_dir, "V2copy");
        foreach (var file in Directory.GetFiles(V2)) { Directory.CreateDirectory(copy); File.Copy(file, Path.Combine(copy, Path.GetFileName(file))); }
        var report = new DirectoryComparer(new CompareOptions { Cache = CacheMode.Off }).Compare(B, V1);
        var port = ChangePorter.Run(report, B, V1, copy, write: true);
        Assert.Equal(0, port.Count(PortStatus.Conflict));
        foreach (var (path, text) in merged)
            Assert.Equal(text, File.ReadAllText(Path.Combine(copy, path)));
    }

    [Fact]
    public void Views_SummaryListAndFileDiff()
    {
        var t = Lines(1, 30);
        Put("B/c.c", t); Put("V1/c.c", Edit(t, 5, "ONE")); Put("V2/c.c", Edit(t, 5, "TWO"));
        Put("B/o.c", t); Put("V1/o.c", Edit(t, 9, "solo")); Put("V2/o.c", t);
        var s = new SessionStore().Start3(B, V1, V2, new CompareOptions { Cache = CacheMode.Off });
        Assert.True(s.Wait(TimeSpan.FromSeconds(60)));
        Assert.Null(s.Error);

        var sum = ThreeWayViews.Summary(s);
        Assert.Contains("CONFLICT         1    content 1", sum);
        Assert.Contains("v1 only          1", sum);
        var list = ThreeWayViews.ListFiles(s, status: "conflict");
        Assert.Contains("1 path(s) match", list);
        Assert.Contains("CF c.c", list);
        Assert.Contains("<<<<<<< v1", ThreeWayViews.FileDiff(s, "c.c"));
        Assert.Contains("+solo", ThreeWayViews.FileDiff(s, "o.c"));
        Assert.StartsWith("error:", ThreeWayViews.ListFiles(s, status: "nope"));
        Assert.Throws<DirectoryNotFoundException>(() => new SessionStore().Start3(B, Path.Combine(_dir, "missing"), V2));
    }
}
