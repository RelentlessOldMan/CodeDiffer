using System.Security.Cryptography;
using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Port;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// A warm compare3 merges a file both sides modified without opening it: the merge store remembers how its three
/// versions merged, by their hashes, which the compares know; v1 and v2 holding the same is agreed outright. And the
/// lines a merge splits, cut from the bytes with the base's shared head and tail reused, are those of the whole text.
/// </summary>
public sealed class MergeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-merge-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel);

    private void Put(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), new UTF8Encoding(false).GetBytes(text));
    }

    private CompareOptions Opts(CacheMode cache = CacheMode.On, params string[] rehash) => Opts(false, cache, rehash);

    private CompareOptions Opts(bool resolveStamps, CacheMode cache = CacheMode.On, params string[] rehash) => new()
    {
        Cache = cache,
        ResolveStamps = resolveStamps,
        Parallelism = 1,
        CacheBaseDir = P("cache"),
        CodeCompassBaseDir = P("no-compass"),
        Timing = new TrustTiming(0, 0),
        RehashRoots = rehash.Select(P).ToList(),
    };

    private static string Doc(int n, Func<int, string>? line = null)
        => string.Concat(Enumerable.Range(1, n).Select(i => (line?.Invoke(i) ?? $"line {i}") + "\n"));

    /// <summary>Clean merges, a content conflict, a stamps conflict, an eol change, one both sides made alike, one untouched.</summary>
    private void Trees()
    {
        Put("B/clean.c", Doc(40));
        Put("V1/clean.c", Doc(40, i => i == 5 ? "five" : $"line {i}"));
        Put("V2/clean.c", Doc(40, i => i == 35 ? "thirty-five" : $"line {i}"));
        Put("B/clash.c", Doc(10));
        Put("V1/clash.c", Doc(10, i => i == 4 ? "four by v1" : $"line {i}"));
        Put("V2/clash.c", Doc(10, i => i == 4 ? "four by v2" : $"line {i}"));
        Put("B/stamp.c", "$Id: stamp.c 1 $\n" + Doc(10));
        Put("V1/stamp.c", "$Id: stamp.c 2 $\n" + Doc(10, i => i == 9 ? "nine" : $"line {i}"));
        Put("V2/stamp.c", "$Id: stamp.c 3 $\n" + Doc(10));
        Put("B/eol.h", "int a;\nint b;\n");
        Put("V1/eol.h", "int a;\r\nint b;\r\n");
        Put("V2/eol.h", "int a;\nint b;\nint c;\n");
        Put("B/alike.txt", "old\n");
        Put("V1/alike.txt", "new\n");
        Put("V2/alike.txt", "new\n");
        foreach (var t in new[] { "B", "V1", "V2" }) Put($"{t}/same.txt", "same\n");
    }

    private static List<string> Shape(ThreeWayReport r)
        => r.Entries.Select(e => $"{e.Path}|{e.Outcome}|{e.ConflictKind}|{e.ConflictRegions}|{e.CleanRegions}|{e.MergedPath}|{e.Note}").ToList();

    [Fact]
    public void AWarmMerge_OpensNoFile_AndGivesTheSameEntries()
    {
        Trees();
        var cold = TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts());
        Assert.Equal(0, cold.Steps!.Remembered);
        Assert.Equal(1, cold.Steps.SameContent); // alike.txt: by its hashes, never opened
        Assert.Equal(4, cold.Steps.Files);
        Assert.True(File.Exists(Path.Combine(P("cache"), MergeStore.FileName)));

        var warm = TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts());
        Assert.Equal(Shape(cold), Shape(warm));
        Assert.Equal(4, warm.Steps!.Remembered);
        Assert.Equal(1, warm.Steps.SameContent);
        Assert.Equal(0, warm.Steps.Files);
        Assert.Equal(0, warm.Steps.BytesRead);
        // And what it says about each is what a merge from the files says.
        Assert.Equal(Shape(cold), Shape(TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts(CacheMode.Off))));
    }

    [Fact]
    public void AnEditedSide_IsMergedFromItsFilesAgain()
    {
        Trees();
        TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts());
        Put("V2/clean.c", Doc(40, i => i == 5 ? "five, but v2's" : $"line {i}")); // now it conflicts
        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts());
        Assert.Equal(3, r.Steps!.Remembered);
        Assert.Equal(1, r.Steps.Files);
        var e = r.Entries.Single(x => x.Path == "clean.c");
        Assert.Equal(Merge3Outcome.Conflict, e.Outcome);
        Assert.Equal("content", e.ConflictKind);
    }

    [Fact]
    public void OffOrRehashed_TheStoreIsNotTrusted()
    {
        Trees();
        TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts(CacheMode.Off));
        Assert.False(File.Exists(Path.Combine(P("cache"), MergeStore.FileName)));

        TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts());
        foreach (var side in new[] { "B", "V1", "V2" })
        {
            var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts(CacheMode.On, side));
            Assert.Equal(0, r.Steps!.Remembered);
            Assert.Equal(4, r.Steps.Files);
        }
    }

    [Fact]
    public void AnotherClassifySizeOrStampResolving_KeepsItsOwnMerges()
    {
        Trees();
        TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts());
        var resolve = Opts(resolveStamps: true);
        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), resolve);
        Assert.Equal(0, r.Steps!.Remembered);
        Assert.Equal(Merge3Outcome.Merged, r.Entries.Single(e => e.Path == "stamp.c").Outcome);
        var again = TreeMerger.Run(P("B"), P("V1"), P("V2"), resolve);
        Assert.Equal(4, again.Steps!.Remembered);
        Assert.Equal(Shape(r), Shape(again));
    }

    [Fact]
    public void AStoreItDidNotWrite_IsAMiss()
    {
        Trees();
        Directory.CreateDirectory(P("cache"));
        File.WriteAllBytes(Path.Combine(P("cache"), MergeStore.FileName), [1, 2, 3, 4, 5, 6, 7]);
        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts());
        Assert.Equal(0, r.Steps!.Remembered);
        Assert.Equal(4, TreeMerger.Run(P("B"), P("V1"), P("V2"), Opts()).Steps!.Remembered); // and it was rewritten
    }

    /// <summary>The lines cut from three texts' bytes are, for every way a side can share a head and tail with the base,
    /// those of each whole text split on its own.</summary>
    [Fact]
    public void LinesAll_AreEachTextsOwnLines()
    {
        uint seed = 777;
        int Next(int max) { seed = seed * 1664525 + 1013904223; return (int)((seed >> 8) % (uint)max); }
        string[] pool = ["a", "b", "", "  x", "a\r", "dup", "dup", "end", "é", "\t"];
        string Make(List<string> lines, string eol, bool tail) => string.Join(eol, lines) + (tail && lines.Count > 0 ? eol : "");
        int cases = 0;
        for (int i = 0; i < 3000; i++)
        {
            var baseLines = Enumerable.Range(0, Next(9)).Select(_ => pool[Next(pool.Length)]).ToList();
            string eol = Next(4) == 0 ? "\r\n" : "\n";
            bool tail = Next(5) != 0;
            var texts = new List<string> { Make(baseLines, eol, tail) };
            for (int s = 0; s < 2; s++)
            {
                var l = new List<string>(baseLines);
                string e = Next(6) == 0 ? (eol == "\n" ? "\r\n" : "\n") : eol;
                bool t = Next(6) == 0 ? !tail : tail;
                for (int m = Next(4); m > 0; m--)
                    switch (Next(5))
                    {
                        case 0: l.Add(pool[Next(pool.Length)]); break;
                        case 1: l.Insert(0, pool[Next(pool.Length)]); break;
                        case 2: if (l.Count > 0) l.RemoveAt(Next(l.Count)); break;
                        case 3: if (l.Count > 0) l[Next(l.Count)] += "z"; break;
                        default: l.Insert(Next(l.Count + 1), pool[Next(pool.Length)]); break;
                    }
                var text = Make(l, e, t);
                if (Next(10) == 0) text = text.Length > 0 ? text[..Next(text.Length)] : text; // cut anywhere, mid-CRLF too
                texts.Add(text);
            }
            bool bom = Next(8) == 0;
            var bytes = texts.Select(x => (byte[])[.. bom ? [0xEF, 0xBB, 0xBF] : Array.Empty<byte>(), .. Encoding.UTF8.GetBytes(x)]).ToArray();
            if (Next(10) == 0) bytes[1 + Next(2)] = Encoding.Latin1.GetBytes(texts[1] + "é"); // not UTF-8: an 8-bit side
            if (!ChangePorter.Text.TryReadAll(bytes[0], bytes[1], bytes[2], out var ta, out var tb, out var tc)) continue;
            foreach (var normalize in new[] { false, true })
            {
                var (la, lb, lc) = ChangePorter.Text.LinesAll(ta, tb, tc, normalize);
                Assert.True(ChangePorter.Text.TryReadAll(bytes[0], bytes[1], bytes[2], out var wa, out var wb, out var wc));
                Assert.Equal(ChangePorter.Lines(wa.Content, normalize), la);
                Assert.Equal(ChangePorter.Lines(wb.Content, normalize), lb);
                Assert.Equal(ChangePorter.Lines(wc.Content, normalize), lc);
                cases++;
            }
        }
        Assert.True(cases > 5000, $"{cases} cases");
    }

    // ---- the merger's version ----

    /// <summary>What decides a merge's outcome, conflict kind, region counts and note: what the store keeps.</summary>
    private static readonly string[] Sources =
    [
        "src/CodeDiffer.Core/ThreeWay/TreeMerger.cs",
        "src/CodeDiffer.Core/Diff/ThreeWayMerger.cs",
        "src/CodeDiffer.Core/Diff/LineDiffer.cs",
        "src/CodeDiffer.Core/Diff/LineText.cs",
        "src/CodeDiffer.Core/Port/ChangePorter.cs",
        "src/CodeDiffer.Core/Compare/NoiseFinder.cs",
        "src/CodeDiffer.Core/ThreeWay/MergeStore.cs",
    ];

    /// <summary>A merger version and a source it was pinned at. The sources changed: if no file anywhere could merge to
    /// another outcome, conflict kind, region count or note, add a line with the same version and the new source; else
    /// bump <see cref="MergeStore.MergerVersion"/> and add a line with it. Never edit an earlier line.</summary>
    private static readonly (int Version, string Source)[] Pins =
    [
        (1, "EAA04C5140E70AFD"),
        (1, "7ECB19DD12C3DE26"), // v2 listed while base->v1 runs: the same compares, sooner
        (1, "85A2B650CFA522D5"), // noise's strip steps done once per text: the same answers, less memory
        (1, "EEC9C0B481F5439C"), // comments only
    ];

    [Fact]
    public void TheMergersSource_IsPinnedAtItsVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodeDiffer.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var all = new StringBuilder();
        foreach (var s in Sources)
            all.Append(s).Append('\n').Append(File.ReadAllText(Path.Combine(dir.FullName, s)).Replace("\r\n", "\n")).Append('\n');
        var source = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all.ToString())))[..16];
        Assert.Equal(MergeStore.MergerVersion, Pins[^1].Version);
        Assert.True(Pins.Any(p => p.Version == MergeStore.MergerVersion && p.Source == source),
            $"The merger's source changed (source {source}). If no file anywhere could merge differently, add a Pins line with this " +
            "version and this source; else bump MergeStore.MergerVersion and add a line with it.");
    }
}
