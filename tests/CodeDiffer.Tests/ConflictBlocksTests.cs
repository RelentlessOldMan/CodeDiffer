using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Conflict blocks settled one at a time: <c>resolve ... --blocks</c> replaces the blocks picked in a markers file with
/// v1's lines, v2's, or both, keeping the cleanly merged lines around them, so the file reads as merged by hand once no
/// block is left; a block is found by the line counts the overlay recorded, never by a bare <c>=======</c>.
/// </summary>
public sealed class ConflictBlocksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-blocks-" + Guid.NewGuid().ToString("N"));
    private readonly string _b, _v1, _v2;
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };
    private static readonly string Twenty = string.Concat(Enumerable.Range(1, 20).Select(i => $"l{i}\n"));

    public ConflictBlocksTests()
    {
        _b = Path.Combine(_dir, "B");
        _v1 = Path.Combine(_dir, "V1");
        _v2 = Path.Combine(_dir, "V2");
        // Three blocks (lines 3, 10, 17), and one clean change from each side (6 from v1, 13 from v2).
        Put("B/multi.c", Twenty);
        Put("V1/multi.c", Twenty.Replace("l3\n", "v1 three\n").Replace("l6\n", "v1 six\n").Replace("l10\n", "v1 ten\n").Replace("l17\n", "v1 seventeen\n"));
        Put("V2/multi.c", Twenty.Replace("l3\n", "v2 three\n").Replace("l13\n", "v2 thirteen\n").Replace("l10\n", "v2 ten\n").Replace("l17\n", "v2 seventeen\n"));
        Put("B/crlf.txt", "x\r\ny\r\nz\r\n");                                    // CRLF kept
        Put("V1/crlf.txt", "x\r\ny one\r\nz\r\n");
        Put("V2/crlf.txt", "x\r\ny two\r\nz\r\n");
        Put("B/nonl.txt", "a\nb");                                               // last line has no line ending
        Put("V1/nonl.txt", "a\nb one");
        Put("V2/nonl.txt", "a\nb two");
        Put("B/doc.rst", "intro\nTitle\n=======\nbody\n");                      // a bare ======= of its own
        Put("V1/doc.rst", "intro\nTitle one\n=======\nbody one\n");
        Put("V2/doc.rst", "intro\nTitle two\n=====\nbody two\n");
        PutBytes("B/wide.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("p\nq\nr\n")).ToArray()); // UTF-16
        PutBytes("V1/wide.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("p\nq one\nr\n")).ToArray());
        PutBytes("V2/wide.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("p\nq two\nr\n")).ToArray());
        byte[] bom = [0xEF, 0xBB, 0xBF];                                         // UTF-8 BOM, the block on line 1
        PutBytes("B/bom.txt", [.. bom, .. "a\nb\nc\n"u8]);
        PutBytes("V1/bom.txt", [.. bom, .. "a one\nb\nc\n"u8]);
        PutBytes("V2/bom.txt", [.. bom, .. "a two\nb\nc\n"u8]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Put(string rel, string text) => PutBytes(rel, new UTF8Encoding(false).GetBytes(text));

    private void PutBytes(string rel, byte[] bytes)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    private string Overlay()
    {
        var r = TreeMerger.Run(_b, _v1, _v2, NoCache);
        var dir = Path.Combine(_dir, "overlay-" + Guid.NewGuid().ToString("N")[..6]);
        MergeOverlay.Write(r, _b, _v1, _v2, dir);
        return dir;
    }

    private static ResolveResult Blocks(string overlay, string choice, string pattern, string blocks, bool write = true)
        => ConflictResolver.Run(overlay, choice, [pattern], [], write, blocks);

    private static string Markers(string o, string rel) => Path.Combine(o, "files", rel);

    [Fact]
    public void TheOverlayRecordsEachBlocksLineCounts()
    {
        var s = OverlayConflicts.Load(Overlay());
        Assert.Equal([new ConflictBlock(1, 1, 1, 0), new ConflictBlock(1, 1, 1, 0), new ConflictBlock(1, 1, 1, 0)],
            s.All.Single(c => c.Path == "multi.c").Blocks!);
        Assert.Equal([new ConflictBlock(1, 1, 1, 7)], s.All.Single(c => c.Path == "nonl.txt").Blocks!); // every side's last line open
        Assert.All(s.All, c => Assert.NotNull(c.Markers));
    }

    [Theory]
    [InlineData("crlf.txt")]
    [InlineData("nonl.txt")]
    [InlineData("doc.rst")]
    [InlineData("wide.txt")]
    [InlineData("bom.txt")]
    public void EverySideForEveryBlock_GivesThatSidesBytes(string file)
    {
        foreach (var (side, tree) in new[] { ("v1", _v1), ("v2", _v2) })
        {
            var o = Overlay();
            var r = Blocks(o, side, "/" + file, "all");
            Assert.Empty(r.Refused);
            Assert.Equal(0, Assert.Single(r.Blocks!).Left);
            Assert.Equal(File.ReadAllBytes(Path.Combine(tree, file)), File.ReadAllBytes(Markers(o, file)));
            var s = OverlayConflicts.Load(o);
            Assert.True(s.HandMerged(s.All.Single(c => c.Path == file)));
        }
    }

    [Fact]
    public void ADifferentSidePerBlock_KeepsTheCleanMerges_AndTheNumbers()
    {
        var o = Overlay();
        Assert.Equal(2, Assert.Single(Blocks(o, "v1", "/multi.c", "1").Blocks!).Left);
        var r = Blocks(o, "v2", "/multi.c", "2");
        Assert.Equal(1, Assert.Single(r.Blocks!).Left);
        var s = OverlayConflicts.Load(o);
        Assert.Equal(1, s.Edited["multi.c"]);                                    // being merged by hand
        var view = ConflictBlocks.View(s, ["/multi.c"], []);
        Assert.Contains("3 block(s) · 1 as written", view);
        Assert.Contains("block 1: settled or edited by hand", view);
        Assert.Contains("block 3 (line ", view);
        Assert.Contains("| v1 seventeen", view);

        Assert.Contains("block(s) 2 are not as written", Assert.Single(Blocks(o, "v1", "/multi.c", "2").Refused).Why);
        Assert.Contains("it has 3 block(s), so no block 4", Assert.Single(Blocks(o, "v1", "/multi.c", "4").Refused).Why);

        var both = Blocks(o, "both", "/multi.c", "3");
        Assert.Contains("block(s) 3 → both  multi.c  (merged: no block left)", ConflictResolver.Text(both));
        var expected = Twenty.Replace("l3\n", "v1 three\n").Replace("l6\n", "v1 six\n").Replace("l10\n", "v2 ten\n")
            .Replace("l13\n", "v2 thirteen\n").Replace("l17\n", "v1 seventeen\nv2 seventeen\n");
        Assert.Equal(expected, File.ReadAllText(Markers(o, "multi.c")));
        s = OverlayConflicts.Load(o);
        Assert.True(s.HandMerged(s.All.Single(c => c.Path == "multi.c")));
        Assert.Equal(1, Blocks(o, "v1", "/multi.c", "all").AlreadyDecided);     // merged now: left alone
        Assert.Contains("merged by hand already", Assert.Single(Blocks(o, "v1", "/multi.c", "1").Refused).Why); // numbers: said
    }

    [Fact]
    public void ADryRun_ChangesNothing()
    {
        var o = Overlay();
        var before = File.ReadAllBytes(Markers(o, "multi.c"));
        var r = Blocks(o, "v2", "/multi.c", "1-3", write: false);
        Assert.Equal([1, 2, 3], Assert.Single(r.Blocks!).Numbers);
        Assert.Contains("dry run · blocks · take v2", ConflictResolver.Text(r));
        Assert.Equal(before, File.ReadAllBytes(Markers(o, "multi.c")));
        Assert.Empty(OverlayConflicts.Load(o).Edited);
    }

    [Fact]
    public void Undo_PutsTheFileBackAsWritten()
    {
        var o = Overlay();
        var before = File.ReadAllBytes(Markers(o, "multi.c"));
        Blocks(o, "v1", "/multi.c", "1,3");
        var u = ConflictResolver.Run(o, "undo", ["/multi.c"], [], true);
        Assert.Single(u.Restored);
        Assert.Equal(before, File.ReadAllBytes(Markers(o, "multi.c")));
        Assert.Empty(OverlayConflicts.Load(o).Edited);
    }

    [Fact]
    public void ABlockEditedByHand_IsNeverTouched()
    {
        var o = Overlay();
        var m = Markers(o, "multi.c");
        File.WriteAllText(m, File.ReadAllText(m).Replace("v2 ten\n", "v2 ten\nadded by hand\n"));
        Assert.Contains("block(s) 2 are not as written", Assert.Single(Blocks(o, "v1", "/multi.c", "2").Refused).Why);
        var r = Blocks(o, "v1", "/multi.c", "all");                               // all: the blocks still as written
        Assert.Equal([1, 3], Assert.Single(r.Blocks!).Numbers);
        Assert.Equal(1, r.Blocks![0].Left);
        Assert.Contains("added by hand", File.ReadAllText(m));
    }

    [Fact]
    public void ALineChangedByHand_KeepsItsBlock()
    {
        var o = Overlay();
        var m = Markers(o, "multi.c");
        File.WriteAllText(m, File.ReadAllText(m).Replace("v1 ten\n", "v1 ten, fixed\n")); // no line added or taken out
        Assert.Contains("block(s) 2 are not as written", Assert.Single(Blocks(o, "v2", "/multi.c", "2").Refused).Why);
        Assert.Equal([1, 3], Assert.Single(Blocks(o, "v2", "/multi.c", "all").Blocks!).Numbers);
        Assert.Contains("v1 ten, fixed", File.ReadAllText(m));
    }

    [Fact]
    public void BlockNumbers_AreOneFiles()
    {
        var o = Overlay();
        Assert.Contains("block numbers are one file's", Assert.Throws<ArgumentException>(() => Blocks(o, "v1", "*", "1")).Message);
        var r = ConflictResolver.Run(o, "v2", [], ["content"], true, "all");      // all: every file
        Assert.Equal(6, r.Blocks!.Count);
        Assert.Empty(OverlayConflicts.Load(o).Undecided);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_v2, "doc.rst")), File.ReadAllBytes(Markers(o, "doc.rst")));
    }

    [Fact]
    public void ChoicesAndNumbers_AreChecked()
    {
        var o = Overlay();
        Assert.Throws<ArgumentException>(() => ConflictResolver.Run(o, "both", ["/multi.c"], [], false));
        Assert.Throws<ArgumentException>(() => Blocks(o, "regenerate", "/multi.c", "1"));
        Assert.Throws<ArgumentException>(() => Blocks(o, "undo", "/multi.c", "all"));
        foreach (var bad in new[] { "0", "x", "3-1", "", "1,,-2" })
            Assert.Throws<ArgumentException>(() => ConflictBlocks.Parse(bad));
        Assert.Equal([1, 3, 4, 5], ConflictBlocks.Parse("1, 3-5")!);
        Assert.Null(ConflictBlocks.Parse("all"));
        Assert.Equal("1-3,5,7-8", ConflictBlocks.Ranges([8, 1, 2, 3, 5, 7]));
    }

    [Fact]
    public void AnOverlayThatDoesntRecordItsBlocks_IsRefused()
    {
        var o = Overlay();
        var json = Path.Combine(o, OverlayConflicts.FileName);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(json))!;
        foreach (var c in root["conflicts"]!.AsArray()) c!.AsObject().Remove("blocks"); // as an older CodeDiffer wrote it
        File.WriteAllText(json, root.ToJsonString());
        Assert.Contains("written by an older CodeDiffer", Assert.Single(Blocks(o, "v1", "/multi.c", "1").Refused).Why);
    }
}
