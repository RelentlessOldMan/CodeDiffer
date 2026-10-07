using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// compare3's merge written as an overlay on v1: applied to a copy of v1 (copy files\ over it, delete deletes.txt),
/// it must give the merged tree; conflicts it can't write as one file are listed, and v1 keeps its version of them.
/// </summary>
public sealed class MergeOverlayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-overlay-" + Guid.NewGuid().ToString("N"));
    private readonly string _b, _v1, _v2;
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };
    private static readonly string Ten = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));

    public MergeOverlayTests()
    {
        _b = Path.Combine(_dir, "B");
        _v1 = Path.Combine(_dir, "V1");
        _v2 = Path.Combine(_dir, "V2");
        foreach (var d in new[] { "B", "V1", "V2" })
        {
            Put($"{d}/same.c", "untouched\n");
            Put($"{d}/v1only.c", "a\n");
            Put($"{d}/v2only.c", "b\n");
            Put($"{d}/gone.c", "deleted by v2\n");
            Put($"{d}/old/moved.c", string.Concat(Enumerable.Range(1, 30).Select(i => $"moved {i}\n")));
            Put($"{d}/both.c", Ten);
            Put($"{d}/clash.c", Ten);
            Put($"{d}/blob.bin", "\0\u0001base");
            Put($"{d}/model.c", "x\n");
            Put($"{d}/crlf.c", Ten.Replace("\n", "\r\n"));
        }
        Put("V1/v1only.c", "A from v1\n");
        Put("V2/v2only.c", "B from v2\n");
        File.Delete(Path.Combine(_v2, "gone.c"));
        Move(_v2, "old/moved.c", "new/moved.c");
        Put("V2/added.c", "new in v2\n");
        Put("V1/both.c", Ten.Replace("line 2\n", "line 2 v1\n"));      // separate regions: merges
        Put("V2/both.c", Ten.Replace("line 9\n", "line 9 v2\n"));
        Put("V1/clash.c", Ten.Replace("line 5\n", "five v1\n"));        // same line: conflict
        Put("V2/clash.c", Ten.Replace("line 5\n", "five v2\n"));
        Put("V1/blob.bin", "\0\u0001v1!");                              // binary both sides: unresolved
        Put("V2/blob.bin", "\0\u0001v2!!");
        File.Delete(Path.Combine(_v1, "model.c"));                      // delete/modify: unresolved
        Put("V2/model.c", "x changed\n");
        Put("V1/crlf.c", Ten.Replace("line 1\n", "one\n").Replace("\n", "\r\n")); // CRLF kept through the merge
        Put("V2/crlf.c", Ten.Replace("line 10\n", "ten\n").Replace("\n", "\r\n"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Put(string rel, string text)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(text));
    }

    private static void Move(string root, string from, string to)
    {
        var dest = Path.Combine(root, to);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Move(Path.Combine(root, from), dest);
    }

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel));

    private static void CopyTree(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, overwrite: true);
        }
    }

    [Fact]
    public void AppliedToV1_GivesTheMerge()
    {
        var r = TreeMerger.Run(_b, _v1, _v2, NoCache);
        var outDir = Path.Combine(_dir, "overlay");
        var o = MergeOverlay.Write(r, _b, _v1, _v2, outDir);

        Assert.Equal(3, o.FromV2);     // v2only.c, added.c, new/moved.c
        Assert.Equal(2, o.Merged);     // both.c, crlf.c
        Assert.Equal(1, o.Markers);    // clash.c
        Assert.Equal(2, o.Deletes);    // gone.c, old/moved.c
        Assert.Equal(2, o.Unresolved); // blob.bin, model.c
        Assert.False(File.Exists(Path.Combine(outDir, "files", "v1only.c"))); // v1 already has it

        var merged = Apply(outDir);

        Assert.Equal("untouched\n", Read(merged, "same.c"));
        Assert.Equal("A from v1\n", Read(merged, "v1only.c"));
        Assert.Equal("B from v2\n", Read(merged, "v2only.c"));
        Assert.Equal("new in v2\n", Read(merged, "added.c"));
        Assert.False(File.Exists(Path.Combine(merged, "gone.c")));
        Assert.False(File.Exists(Path.Combine(merged, "old", "moved.c")));
        Assert.Equal(Read(_v2, "new/moved.c"), Read(merged, "new/moved.c"));
        Assert.Equal(Ten.Replace("line 2\n", "line 2 v1\n").Replace("line 9\n", "line 9 v2\n"), Read(merged, "both.c"));
        Assert.Equal(Ten.Replace("line 1\n", "one\n").Replace("line 10\n", "ten\n").Replace("\n", "\r\n"), Read(merged, "crlf.c"));

        var clash = Read(merged, "clash.c");
        Assert.Contains("<<<<<<< v1 (clash.c:5)\nfive v1\n||||||| base (clash.c:5)\nline 5\n=======\nfive v2\n>>>>>>> v2 (clash.c:5)\n", clash);

        // Unresolved: v1's version stays; conflicts.txt names each side's file.
        Assert.Equal("\0\u0001v1!", Read(merged, "blob.bin"));
        Assert.False(File.Exists(Path.Combine(merged, "model.c")));
        var conflicts = Read(outDir, "conflicts.txt");
        Assert.Contains("markers  clash.c  (1 region(s))", conflicts);
        Assert.Contains("binary  blob.bin", conflicts);
        Assert.Contains("delete/modify  model.c", conflicts);
        Assert.Contains("    v1: (deleted)\n    v2: " + Path.Combine(_v2, "model.c"), conflicts);
        Assert.Contains("1. delete the v1 paths listed in deletes.txt", Read(outDir, "OVERLAY.txt"));
        Assert.Contains("3. copy files\\ over v1", Read(outDir, "OVERLAY.txt"));
        Assert.False(File.Exists(Path.Combine(outDir, "INCOMPLETE.txt")));
        Assert.Empty(Directory.EnumerateFiles(outDir, "*.codediffer-tmp", SearchOption.AllDirectories));
    }

    /// <summary>Apply the overlay to a fresh copy of v1 the documented way: deletes first, then remove the directories
    /// that left empty, then copy files\.</summary>
    private string Apply(string outDir)
    {
        var merged = Path.Combine(_dir, "M-" + Guid.NewGuid().ToString("N")[..6]);
        CopyTree(_v1, merged);
        foreach (var d in File.ReadAllLines(Path.Combine(outDir, "deletes.txt")))
        {
            var p = Path.Combine(merged, d);
            if (File.Exists(p)) File.Delete(p);
            else if (Directory.Exists(p)) Directory.Delete(p, true);
            for (var dir = Path.GetDirectoryName(p)!; dir.Length > merged.Length && !Directory.EnumerateFileSystemEntries(dir).Any(); dir = Path.GetDirectoryName(dir)!)
                Directory.Delete(dir);
        }
        CopyTree(Path.Combine(outDir, "files"), merged);
        return merged;
    }

    [Fact]
    public void AConflictLandingOnAFileV1Added_IsACollision_NotAnOverwrite()
    {
        // v2 renames clash.c -> q.c (the edit conflicts with v1's), and v1 independently adds q.c.
        Move(_v2, "clash.c", "q.c");
        Put("V1/q.c", "v1's own q\n");
        var r = TreeMerger.Run(_b, _v1, _v2, NoCache);
        Assert.All(r.Entries.Where(e => e.MergedPath == "q.c"), e => Assert.Equal("path collision", e.ConflictKind));

        var outDir = Path.Combine(_dir, "overlay");
        MergeOverlay.Write(r, _b, _v1, _v2, outDir);
        Assert.False(File.Exists(Path.Combine(outDir, "files", "q.c")));
        Assert.Equal("v1's own q\n", Read(Apply(outDir), "q.c"));
        var conflicts = Read(outDir, "conflicts.txt");
        Assert.Contains("path collision  q.c", conflicts);
        // Neither side deleted anything here: a side that didn't touch the path shows its unchanged file.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(conflicts, @"\(deleted\)")); // only model.c, which v1 did delete
        Assert.Contains($"    v2: {Path.Combine(_v2, "q.c")} (unchanged)", conflicts);
    }

    [Fact]
    public void ACaseOnlyRename_EndsUpUnderTheNewName()
    {
        Move(_v2, "same.c", "SAME.c.tmp"); // two steps: a case-only rename in place is a no-op on some APIs
        File.Move(Path.Combine(_v2, "SAME.c.tmp"), Path.Combine(_v2, "Same.c"));
        var r = TreeMerger.Run(_b, _v1, _v2, NoCache);
        var outDir = Path.Combine(_dir, "overlay");
        MergeOverlay.Write(r, _b, _v1, _v2, outDir);

        var merged = Apply(outDir);
        var names = Directory.EnumerateFiles(merged).Select(Path.GetFileName).ToList();
        Assert.Contains("Same.c", names);
        Assert.DoesNotContain("same.c", names);
        Assert.Equal("untouched\n", Read(merged, "Same.c"));
    }

    [Fact]
    public void AFileV2TurnedIntoADirectory_Applies()
    {
        File.Delete(Path.Combine(_v2, "v2only.c"));
        Put("V2/v2only.c/inner.c", "now a directory\n");
        var r = TreeMerger.Run(_b, _v1, _v2, NoCache);
        var outDir = Path.Combine(_dir, "overlay");
        MergeOverlay.Write(r, _b, _v1, _v2, outDir);
        Assert.Equal("now a directory\n", Read(Apply(outDir), "v2only.c/inner.c"));
    }

    [Fact]
    public void AMergeThatChangedSinceTheCompare_IsNotWritten()
    {
        var r = TreeMerger.Run(_b, _v1, _v2, NoCache);
        Put("V2/both.c", Ten.Replace("line 2\n", "line 2 v2 now clashes\n")); // after the compare: both.c now conflicts
        var outDir = Path.Combine(_dir, "overlay");
        var o = MergeOverlay.Write(r, _b, _v1, _v2, outDir);

        Assert.Equal(1, o.Failed);
        Assert.False(File.Exists(Path.Combine(outDir, "files", "both.c")));
        Assert.Contains("not written: changed since the compare", Read(outDir, "conflicts.txt"));
        Assert.Contains("1 FAILED to write", Read(outDir, "OVERLAY.txt"));
    }

    [Fact]
    public void CrlfConflictMarkers_UseCrlf()
    {
        Put("V1/crlf.c", Ten.Replace("line 5\n", "five v1\n").Replace("\n", "\r\n"));
        Put("V2/crlf.c", Ten.Replace("line 5\n", "five v2\n").Replace("\n", "\r\n"));
        var r = TreeMerger.Run(_b, _v1, _v2, NoCache);
        var outDir = Path.Combine(_dir, "overlay");
        MergeOverlay.Write(r, _b, _v1, _v2, outDir);
        var text = Read(Path.Combine(outDir, "files"), "crlf.c");
        Assert.Contains("<<<<<<< v1 (crlf.c:5)\r\nfive v1\r\n||||||| base", text);
        Assert.DoesNotMatch("[^\r]\n", text);
    }


    [Fact]
    public void RefusesANonEmptyDirectory_OrOneInsideATree()
    {
        var r = TreeMerger.Run(_b, _v1, _v2, NoCache);
        var busy = Path.Combine(_dir, "busy");
        Put("busy/x.txt", "x");
        Assert.Contains("not empty", Assert.Throws<ArgumentException>(() => MergeOverlay.Write(r, _b, _v1, _v2, busy)).Message);
        Assert.Contains("inside the v1 tree", Assert.Throws<ArgumentException>(() => MergeOverlay.Write(r, _b, _v1, _v2, Path.Combine(_v1, "out"))).Message);
    }

    [Fact]
    public void LargeFilesBothChanged_AreHashedNotLoaded_SameIsAgreed_DifferentIsALargeConflict()
    {
        Put("V1/clash.c", Ten.Replace("line 5\n", "five SAME\n"));   // both sides made the same edit
        Put("V2/clash.c", Ten.Replace("line 5\n", "five SAME\n"));
        var small = new CompareOptions { Cache = CacheMode.Off, MaxClassifyBytes = 16 }; // every file counts as large
        var r = TreeMerger.Run(_b, _v1, _v2, small);
        Assert.Equal(Merge3Outcome.Agreed, r.Entries.Single(e => e.Path == "clash.c").Outcome);
        var both = r.Entries.Single(e => e.Path == "both.c");
        Assert.Equal(Merge3Outcome.Conflict, both.Outcome);
        Assert.Equal("large", both.ConflictKind);
    }

    // ---- apply-overlay ----

    private string Overlay()
    {
        var outDir = Path.Combine(_dir, "overlay");
        MergeOverlay.Write(TreeMerger.Run(_b, _v1, _v2, NoCache), _b, _v1, _v2, outDir);
        return outDir;
    }

    private string CopyOfV1()
    {
        var t = Path.Combine(_dir, "T-" + Guid.NewGuid().ToString("N")[..6]);
        CopyTree(_v1, t);
        return t;
    }

    /// <summary>Every file, by exact (case-sensitive) relative name, with its bytes.</summary>
    private static Dictionary<string, string> Tree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), f => Convert.ToHexString(File.ReadAllBytes(f)));

    [Fact]
    public void ApplyOverlay_GivesWhatTheManualStepsGive_AndADryRunChangesNothing()
    {
        Move(_v2, "same.c", "SAME.c.tmp"); // a case-only rename and a file turned directory ride along
        File.Move(Path.Combine(_v2, "SAME.c.tmp"), Path.Combine(_v2, "Same.c"));
        File.Delete(Path.Combine(_v2, "v2only.c"));
        Put("V2/v2only.c/inner.c", "now a directory\n");
        var outDir = Overlay();
        var t = CopyOfV1();

        var dry = OverlayApplier.Run(outDir, t, write: false);
        Assert.Equal(Tree(_v1), Tree(t));
        Assert.Contains("old/moved.c", dry.Deletes);
        Assert.Contains("nothing was changed", OverlayApplier.Text(dry));

        var r = OverlayApplier.Run(outDir, t, write: true);
        Assert.Empty(r.Failed);
        Assert.Equal(Tree(Apply(outDir)), Tree(t));
        Assert.False(Directory.Exists(Path.Combine(t, "old"))); // emptied by the deletes: removed
        Assert.Contains(t, File.ReadAllText(Path.Combine(outDir, OverlayApplier.AppliedName)));
        Assert.False(File.Exists(Path.Combine(outDir, OverlayApplier.ApplyingName)));
        Assert.Contains("written for v1", OverlayApplier.Text(r)); // a copy, not v1 itself
    }

    [Fact]
    public void ApplyOverlay_Stopped_FinishesOnTheNextRun()
    {
        for (int i = 0; i < 12; i++) Put($"V2/many/m{i:00}.c", $"new {i}\n");
        var outDir = Overlay();
        var t = CopyOfV1();
        using var stop = new CancellationTokenSource();
        var r = OverlayApplier.Run(outDir, t, write: true, parallelism: 1, ct: stop.Token,
            progress: new CodeDiffer.Core.Port.PortProgress { AfterFile = n => { if (n == 6) stop.Cancel(); } });
        Assert.True(r.Cancelled);
        Assert.True(r.NotReached > 0);
        Assert.Contains("run the same apply-overlay again", OverlayApplier.Text(r));
        Assert.True(File.Exists(Path.Combine(outDir, OverlayApplier.ApplyingName)));
        Assert.False(File.Exists(Path.Combine(outDir, OverlayApplier.AppliedName)));
        Assert.Empty(Directory.EnumerateFiles(t, "*.codediffer.tmp", SearchOption.AllDirectories));

        var again = OverlayApplier.Run(outDir, t, write: true); // not refused: the first never finished
        Assert.False(again.Cancelled);
        Assert.True(again.AlreadyThere > 0);
        Assert.Equal(Tree(Apply(outDir)), Tree(t));
    }

    [Fact]
    public void ApplyOverlay_Twice_IsRefusedUnlessAgain()
    {
        var outDir = Overlay();
        var t = CopyOfV1();
        OverlayApplier.Run(outDir, t, write: true);
        File.WriteAllText(Path.Combine(t, "clash.c"), "resolved by hand\n");
        Assert.Contains("already applied", Assert.Throws<ArgumentException>(() => OverlayApplier.Run(outDir, t, write: true)).Message);
        Assert.Equal("resolved by hand\n", Read(t, "clash.c"));
        OverlayApplier.Run(outDir, t, write: false); // a dry run is always allowed
        OverlayApplier.Run(outDir, t, write: true, again: true);
        Assert.Contains("<<<<<<< v1", Read(t, "clash.c"));
    }

    [Fact]
    public void ApplyOverlay_RefusesAnUnfinishedOverlay_ABadDeletesLine_AndATargetInside()
    {
        var outDir = Overlay();
        var t = CopyOfV1();
        File.WriteAllText(Path.Combine(outDir, "INCOMPLETE.txt"), "");
        Assert.Contains("not finished", Assert.Throws<ArgumentException>(() => OverlayApplier.Run(outDir, t, write: true)).Message);
        File.Delete(Path.Combine(outDir, "INCOMPLETE.txt"));

        File.AppendAllText(Path.Combine(outDir, "deletes.txt"), "../outside.c\n");
        var before = Tree(t);
        Assert.Contains("not a plain relative path", Assert.Throws<ArgumentException>(() => OverlayApplier.Run(outDir, t, write: true)).Message);
        Assert.Equal(before, Tree(t)); // refused before touching anything

        Assert.Contains("inside each other", Assert.Throws<ArgumentException>(
            () => OverlayApplier.Run(outDir, Path.Combine(outDir, "files"), write: false)).Message);
        Assert.Contains("not a merge overlay", Assert.Throws<ArgumentException>(() => OverlayApplier.Run(t, outDir, write: false)).Message);
    }

    [Fact]
    public void ApplyOverlay_NeverDeletesADirectory()
    {
        var outDir = Overlay();
        var t = CopyOfV1();
        File.Delete(Path.Combine(t, "gone.c"));
        Put(Path.GetRelativePath(_dir, Path.Combine(t, "gone.c", "keep.c")), "mine\n"); // the target has a directory there
        var r = OverlayApplier.Run(outDir, t, write: true);
        Assert.Contains(r.Failed, f => f.Path == "gone.c" && f.Why.Contains("directory"));
        Assert.Equal("mine\n", Read(t, "gone.c/keep.c"));
    }

    [Fact]
    public void ApplyOverlay_ADirectoryV2TurnedIntoAFile_Lands_EvenOnADryRun()
    {
        foreach (var d in new[] { "B", "V1", "V2" })
        {
            Put($"{d}/pkg/x.c", "x one\n");
            Put($"{d}/pkg/y.c", "y two\n");
        }
        Directory.Delete(Path.Combine(_v2, "pkg"), true);
        Put("V2/pkg", "now a file\n");
        var outDir = Overlay();
        var t = CopyOfV1();

        var dry = OverlayApplier.Run(outDir, t, write: false);
        Assert.Empty(dry.Failed); // the deletes empty pkg\, so the file would land
        var r = OverlayApplier.Run(outDir, t, write: true);
        Assert.Empty(r.Failed);
        Assert.Equal("now a file\n", Read(t, "pkg"));
        Assert.Equal(Tree(Apply(outDir)), Tree(t));

        // A directory the deletes do not empty is still refused, and left alone.
        var t2 = CopyOfV1();
        Put(Path.GetRelativePath(_dir, Path.Combine(t2, "pkg", "mine.c")), "mine\n");
        Assert.Contains(OverlayApplier.Run(outDir, t2, write: false).Failed, f => f.Path == "pkg");
        Assert.Contains(OverlayApplier.Run(outDir, t2, write: true).Failed, f => f.Path == "pkg");
        Assert.Equal("mine\n", Read(t2, "pkg/mine.c"));
    }

    [Fact]
    public void ApplyOverlay_StoppedAfterTheDeletes_RemovesTheEmptiedDirectoriesOnTheNextRun()
    {
        var outDir = Overlay();
        var t = CopyOfV1();
        using var stop = new CancellationTokenSource();
        int deletes = File.ReadAllLines(Path.Combine(outDir, "deletes.txt")).Length;
        OverlayApplier.Run(outDir, t, write: true, parallelism: 1, ct: stop.Token,
            progress: new CodeDiffer.Core.Port.PortProgress { AfterFile = n => { if (n == deletes) stop.Cancel(); } });
        Assert.True(Directory.Exists(Path.Combine(t, "old"))); // deleted old/moved.c, stopped before the directories

        OverlayApplier.Run(outDir, t, write: true);
        Assert.False(Directory.Exists(Path.Combine(t, "old")));
        Assert.Equal(Tree(Apply(outDir)), Tree(t));
    }

    [Fact]
    public void IsUnder_HandlesADriveRoot()
    {
        var drive = Path.GetPathRoot(_dir)!; // e.g. C:\
        Assert.True(CodeDiffer.Core.Sessions.ResultStore.IsUnder(Path.Combine(drive, "merge"), drive));
        Assert.True(CodeDiffer.Core.Sessions.ResultStore.IsUnder(_v1 + Path.DirectorySeparatorChar, _v1));
        Assert.False(CodeDiffer.Core.Sessions.ResultStore.IsUnder(_v1 + "x", _v1));
    }
}
