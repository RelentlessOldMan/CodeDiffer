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

        // Apply: copy files\ over a copy of v1, then the deletes.
        var merged = Path.Combine(_dir, "M");
        CopyTree(_v1, merged);
        CopyTree(Path.Combine(outDir, "files"), merged);
        foreach (var d in File.ReadAllLines(Path.Combine(outDir, "deletes.txt")))
            File.Delete(Path.Combine(merged, d));

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
        Assert.Contains("copy files\\ over v1", Read(outDir, "OVERLAY.txt"));
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
}
