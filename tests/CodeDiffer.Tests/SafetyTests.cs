using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Port;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Review round 8, tier 1: what must never lose data or give a wrong answer — an incomplete compare is never ported or
/// written as an overlay; nothing is written through a link; a path behind a link is unknown, not removed; names that
/// differ only in case never overwrite each other; an edited rename is never treated as byte-identical; odd names and
/// timestamps don't end the compare; a ledger that can't be saved doesn't lose the result.
/// </summary>
public sealed class SafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-safety-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };
    private static readonly string Ten = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));

    public SafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Junctions first (the link itself), so a recursive delete never reaches through one.
        foreach (var d in Directory.EnumerateDirectories(_dir, "*", SearchOption.AllDirectories).ToList())
            if (Directory.Exists(d) && new DirectoryInfo(d).LinkTarget is not null) { try { Directory.Delete(d); } catch { } }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string rel, string text)
    {
        var p = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(text));
    }

    /// <summary>A junction (no admin rights needed); false off Windows.</summary>
    private bool Junction(string rel, string targetRel)
    {
        if (!OperatingSystem.IsWindows()) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        using var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{P(rel)}\" \"{P(targetRel)}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        mk.WaitForExit();
        Assert.True(Directory.Exists(P(rel)), "mklink /J failed");
        return true;
    }

    [Fact]
    public void AnIncompleteCompare_IsNeverPorted_NorWrittenAsAnOverlay()
    {
        Directory.CreateDirectory(P("T"));
        var incomplete = new CompareReport([new FileChange("sub/keep.txt", ChangeStatus.Removed, null, 3, 0)], 0, 1);
        var ex = Assert.Throws<ArgumentException>(() => ChangePorter.Run(incomplete, P("L"), P("R"), P("T"), write: true));
        Assert.Contains("incomplete", ex.Message);

        var r3 = new ThreeWayReport { V1Report = new CompareReport([]), V2Report = new CompareReport([], 0, 1), Entries = [] };
        Assert.Throws<ArgumentException>(() => MergeOverlay.Write(r3, P("B"), P("V1"), P("V2"), P("overlay")));
        Assert.False(Directory.Exists(P("overlay")));

        // An overlay an older build wrote from an incomplete compare is refused too.
        Directory.CreateDirectory(P("old/files"));
        File.WriteAllText(P("old/deletes.txt"), "sub/keep.txt\n");
        File.WriteAllText(P("old/OVERLAY.txt"), "CodeDiffer merge overlay\n  v1   X\n\nINCOMPLETE COMPARE: 1 director(ies) could not be read\n");
        Assert.Throws<ArgumentException>(() => OverlayApplier.Run(P("old"), P("T"), write: true));
    }

    [Fact]
    public void ACaseOnlyClash_InCompare3_NeverOverwritesV1sEdit()
    {
        if (!OperatingSystem.IsWindows()) return;
        Put("B/Foo.c", Ten);
        Put("V1/Foo.c", Ten.Replace("line 3\n", "v1 edit\n"));                // v1 edits Foo.c
        Put("V2/foo.c", "an unrelated rewrite\nnothing in common\n");        // v2: deletes Foo.c, adds foo.c
        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache);
        var add = r.Entries.Single(e => e.Path == "foo.c");
        Assert.Equal((Merge3Outcome.Conflict, "path collision"), (add.Outcome, add.ConflictKind));

        MergeOverlay.Write(r, P("B"), P("V1"), P("V2"), P("overlay"));
        Assert.False(File.Exists(P("overlay/files/foo.c")));
        var t = P("T");
        Directory.CreateDirectory(t);
        File.Copy(P("V1/Foo.c"), Path.Combine(t, "Foo.c"));
        OverlayApplier.Run(P("overlay"), t, write: true);
        Assert.Contains("v1 edit", File.ReadAllText(Path.Combine(t, "Foo.c"))); // v1's edit is still there
    }

    [Fact]
    public void ACaseOnlyRemoveAndAdd_IsNotPorted_SoTheFileStays()
    {
        if (!OperatingSystem.IsWindows()) return;
        Put("L/Foo.c", Ten);
        Put("R/foo.c", "rewritten\nentirely\n");
        Put("T/Foo.c", Ten);
        var report = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        Assert.Equal(2, report.Changes.Count(c => c.Status is ChangeStatus.Added or ChangeStatus.Removed));
        for (int run = 0; run < 5; run++) // it was a race: the add saw Foo.c, then the delete removed it
        {
            var r = ChangePorter.Run(report, P("L"), P("R"), P("T"), write: true);
            Assert.All(r.Files, f => Assert.Equal(PortStatus.Conflict, f.Status));
            Assert.Equal(Ten, File.ReadAllText(P("T/Foo.c")));
        }
    }

    [Fact]
    public void NothingIsWrittenOrDeletedThroughAJunctionInTheTarget()
    {
        Put("outside/x.c", "outside the target\n");
        Put("outside/del.c", "keep me\n");
        Directory.CreateDirectory(P("T"));
        if (!Junction("T/lib", "outside")) return;

        // apply: an add, a delete and a modify under the junction are all refused.
        Put("L/lib/del.c", "keep me\n");
        Put("L/lib/x.c", "outside the target\n");
        Put("R/lib/x.c", "changed\n");
        Put("R/lib/new.c", "new\n");
        var report = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        var r = ChangePorter.Run(report, P("L"), P("R"), P("T"), write: true);
        Assert.All(r.Files, f => Assert.Equal(PortStatus.Conflict, f.Status));
        Assert.Contains("symlink/junction", r.Files[0].Note);
        Assert.Equal("outside the target\n", File.ReadAllText(P("outside/x.c")));
        Assert.True(File.Exists(P("outside/del.c")));
        Assert.False(File.Exists(P("outside/new.c")));

        // apply-overlay: the same.
        Directory.CreateDirectory(P("ov/files/lib"));
        File.WriteAllText(P("ov/files/lib/x.c"), "from the overlay\n");
        File.WriteAllText(P("ov/deletes.txt"), "lib/del.c\n");
        File.WriteAllText(P("ov/OVERLAY.txt"), "CodeDiffer merge overlay\n  v1   T\n");
        var o = OverlayApplier.Run(P("ov"), P("T"), write: true);
        Assert.Equal(2, o.Failed.Count);
        Assert.Equal("outside the target\n", File.ReadAllText(P("outside/x.c")));
        Assert.True(File.Exists(P("outside/del.c")));
    }

    [Fact]
    public void AFileBehindALink_IsUnknown_NeverADelete()
    {
        Put("B/d/f.c", Ten);
        Put("B/keep.c", "x\n");
        Put("V1/d/f.c", Ten.Replace("line 2\n", "v1\n"));
        Put("V1/keep.c", "x\n");
        Put("V2/keep.c", "x\n");
        Put("elsewhere/f.c", Ten);
        if (!Junction("V2/d", "elsewhere")) return;

        var two = new DirectoryComparer(NoCache).Compare(P("B"), P("V2"));
        var c = two.Changes.Single(x => x.RelativePath == "d/f.c");
        Assert.True(c.BehindLink);
        Assert.Contains("symlink/junction", c.Unreadable);

        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache);
        var e = r.Entries.Single(x => x.Path == "d/f.c");
        Assert.Equal((Merge3Outcome.Conflict, "behind a link"), (e.Outcome, e.ConflictKind));
        var s = new SessionStore(save: false).Start3(P("B"), P("V1"), P("V2"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromSeconds(60)));
        Assert.Contains("symlink(s)/junction(s) not followed", ThreeWayViews.Summary(s)); // compare3 says so too

        MergeOverlay.Write(r, P("B"), P("V1"), P("V2"), P("overlay"));
        Assert.DoesNotContain("d/f.c", File.ReadAllText(P("overlay/deletes.txt")));
    }

    [Fact]
    public void AnEditedRename_At100Percent_IsNeverTreatedAsByteIdentical()
    {
        Put("L/swap_old.txt", "alpha\nbeta\ngamma\n");
        Put("R/swap_new.txt", "gamma\nbeta\nalpha\n"); // the same lines, reordered: similarity 1000
        Put("L/same_old.txt", "unchanged\ncontent\n");
        Put("R/same_new.txt", "unchanged\ncontent\n"); // byte-identical
        var report = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        var swap = report.Changes.Single(c => c.RelativePath == "swap_new.txt");
        var same = report.Changes.Single(c => c.RelativePath == "same_new.txt");
        Assert.Equal((1000, true, false), (swap.SimilarityMilli ?? 0, swap.EditedRename, swap.PureRename));
        Assert.True(same.PureRename);

        var w = new StringWriter { NewLine = "\n" };
        PatchWriter.Write(w, report, P("L"), P("R"), new PatchOptions());
        var patch = w.ToString();
        Assert.Contains("+alpha", patch); // the edit is in the patch, not a header-only move

        var s = new SessionStore(save: false).Adopt(P("L"), P("R"), NoCache, report, DateTime.UtcNow, TimeSpan.Zero);
        Assert.Contains("1 pure", AgentViews.Summary(s));
        Assert.Contains("(but edited)", AgentViews.ListFiles(s));
    }

    [Fact]
    public void NamesEndingInADot_AreSkippedAndSaid_NotACrash()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var side in new[] { "L", "R" })
        {
            Put($"{side}/a", "plain a\n");
            Directory.CreateDirectory(P(side));
            File.WriteAllText(@"\\?\" + P($"{side}/a."), "a with a dot\n"); // legal on NTFS, unopenable by plain path
        }
        var r = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        Assert.Equal(2, r.SkippedNames);
        Assert.Equal(ChangeStatus.Identical, r.Changes.Single(c => c.RelativePath == "a").Status);
        Assert.Contains(AgentViews.Notes(r), n => n.Contains("ending in '.' or ' '"));
        foreach (var side in new[] { "L", "R" }) File.Delete(@"\\?\" + P($"{side}/a."));
    }

    [Fact]
    public void ATimestampPastYear9999_IsUnknown_NotACrash()
    {
        Assert.Equal(0, CodeDiffer.Core.Walk.DirectoryLister.FileTimeToUtcTicks(long.MaxValue));
        Assert.Equal(0, CodeDiffer.Core.Walk.DirectoryLister.FileTimeToUtcTicks(DateTime.MaxValue.ToFileTimeUtc() + 1));
        Assert.True(CodeDiffer.Core.Walk.DirectoryLister.FileTimeToUtcTicks(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()) > 0);
    }

    [Fact]
    public void ALedgerThatCannotBeSaved_KeepsTheResult_AndSaysSo()
    {
        Put("L/a.c", "one\n");
        Put("R/a.c", "two\n");
        File.WriteAllText(P("cache"), "a file where the ledger directory should go");
        var r = new DirectoryComparer(new CompareOptions { CacheBaseDir = P("cache") }).Compare(P("L"), P("R"));
        Assert.Equal(ChangeStatus.Modified, r.Changes.Single().Status);
        Assert.NotNull(r.CacheSaveError);
        Assert.Contains(AgentViews.Notes(r), n => n.Contains("hash cache could not be saved"));
    }
}
