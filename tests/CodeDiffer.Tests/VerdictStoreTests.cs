using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The verdict store: a warm compare finds each modified pair's reason and noise without opening either file;
/// an edit (a new hash) or another name is classified anew; --no-cache keeps none, --rehash trusts none; a store
/// from another build or a damaged one is ignored, never trusted.
/// </summary>
public class VerdictStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-verdict-" + Guid.NewGuid().ToString("N"));
    private string CacheBase => Path.Combine(_dir, "_differ");
    private string StorePath => Path.Combine(CacheBase, VerdictStore.FileName);
    private static readonly DateTime Old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);
    private static readonly TrustTiming Instant = new(0, 0);

    public VerdictStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Put(string rel, string text, DateTime? mtime = null)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        File.SetLastWriteTimeUtc(p, mtime ?? Old);
    }

    private CompareReport Run(CacheMode mode = CacheMode.On, IReadOnlyList<string>? rehash = null) =>
        new DirectoryComparer(new CompareOptions
        {
            Cache = mode, CacheBaseDir = CacheBase, CodeCompassBaseDir = Path.Combine(_dir, "_compass"), Timing = Instant,
            RehashRoots = rehash ?? [],
        }).Compare(Path.Combine(_dir, "L"), Path.Combine(_dir, "R"));

    private void MakeTrees()
    {
        Put("L/grow.c", "int a;\n");                       Put("R/grow.c", "int a;\nint b;\n");            // size-changed: content
        Put("L/ws.c", "int a ;\n");                        Put("R/ws.c", "int  a;\n");                     // same size: whitespace
        Put("L/stamp.c", "// $Id: stamp.c 1 $\nint s;\n"); Put("R/stamp.c", "// $Id: stamp.c 2 $\nint s;\n"); // same size: content, stamp noise
        Put("L/eol.txt", "one\ntwo\n");                     Put("R/eol.txt", "one\r\ntwo\r\n");             // size-changed: eol
        Put("L/same.txt", "same\n");                        Put("R/same.txt", "same\n");
    }

    private static Dictionary<string, (ChangeReason?, ChangeNoise)> Verdicts(CompareReport r) =>
        r.Changes.Where(c => c.Status == ChangeStatus.Modified).ToDictionary(c => c.RelativePath, c => (c.Reason, c.Noise));

    [Fact]
    public void WarmRun_ReusesEveryModifiedPairsVerdict_WithoutReading()
    {
        MakeTrees();
        var cold = Run();
        Assert.Equal(0, cold.VerdictHits);
        Assert.Equal(4, Verdicts(cold).Count);
        Assert.Equal(ChangeReason.Whitespace, Verdicts(cold)["ws.c"].Item1);
        Assert.Equal(ChangeReason.Eol, Verdicts(cold)["eol.txt"].Item1);
        Assert.Equal(ChangeNoise.Stamps, Verdicts(cold)["stamp.c"].Item2);
        Assert.True(File.Exists(StorePath));

        var warm = Run();
        Assert.Equal(4, warm.VerdictHits);
        Assert.Equal(0, warm.BytesRead);
        Assert.Equal(Verdicts(cold), Verdicts(warm));
        Assert.Equal(0, cold.CacheHits);
        Assert.Equal(6, warm.CacheHits); // the 3 same-size pairs' sides only: a size-changed file's id lookup isn't one
    }

    [Fact]
    public void AColdClassify_CountsWhatItRead()
    {
        Put("L/grow.c", "int a;\n");  Put("R/grow.c", "int a;\nint b;\n"); // size-changed: classified from both sides whole
        Put("L/eol.txt", "one\ntwo\n"); Put("R/eol.txt", "one\r\ntwo\r\n");
        var cold = Run();
        Assert.Equal(2 * (7 + 14 + 8 + 10), cold.BytesRead);
        Assert.Equal(0, Run().BytesRead); // warm: the verdicts are remembered, nothing is opened
    }

    [Fact]
    public void AnEdit_IsClassifiedAnew()
    {
        MakeTrees();
        Run();
        Put("R/ws.c", "int a=1;\n", Old.AddMinutes(1)); // same file, now a real change (and a new size)
        Put("R/grow.c", "int a;\r\n", Old.AddMinutes(1)); // now only its line ending differs
        var r = Run();
        Assert.Equal(2, r.VerdictHits);
        Assert.Equal(ChangeReason.Content, Verdicts(r)["ws.c"].Item1);
        Assert.Equal(ChangeReason.Eol, Verdicts(r)["grow.c"].Item1);
        Assert.Equal(4, Run().VerdictHits); // and the new verdicts are kept
    }

    [Fact]
    public void ANameIsPartOfTheKey()
    {
        var a = new ContentId("00112233445566778899AABBCCDDEEFF", "");
        var b = new ContentId("FFEEDDCCBBAA99887766554433221100", "");
        var s = VerdictStore.Open(CacheBase, 8 << 20, lookup: true, off: false)!;
        s.Record(a, "x/one.c", b, "y/one.c", ChangeReason.Whitespace, ChangeNoise.Comments);
        s.Save();
        var t = VerdictStore.Open(CacheBase, 8 << 20, lookup: true, off: false)!;
        Assert.True(t.TryGet(a, "other/one.c", b, "z/one.c", out var reason, out var noise)); // same names elsewhere: same rules
        Assert.Equal((ChangeReason.Whitespace, ChangeNoise.Comments), (reason, noise));
        Assert.False(t.TryGet(a, "one.py", b, "one.py", out _, out _)); // another extension: other comment rules
        Assert.False(t.TryGet(b, "one.c", a, "one.c", out _, out _));   // the sides swapped
        Assert.False(VerdictStore.Open(CacheBase, 4 << 20, lookup: true, off: false)!.TryGet(a, "one.c", b, "one.c", out _, out _)); // another classify size
        Assert.False(VerdictStore.Open(CacheBase, 8 << 20, lookup: false, off: false)!.TryGet(a, "one.c", b, "one.c", out _, out _)); // a rehash
        Assert.Null(VerdictStore.Open(CacheBase, 8 << 20, lookup: true, off: true));
    }

    [Fact]
    public void NoCache_KeepsNone_AndRehash_TrustsNone()
    {
        MakeTrees();
        Assert.Equal(0, Run(CacheMode.Off).VerdictHits);
        Assert.False(File.Exists(StorePath));
        Run();
        Assert.Equal(0, Run(rehash: [Path.Combine(_dir, "R")]).VerdictHits);
        Assert.Equal(4, Run().VerdictHits);
    }

    [Fact]
    public void ADamagedOrForeignStore_IsIgnored()
    {
        MakeTrees();
        Run();
        var bytes = File.ReadAllBytes(StorePath);
        File.WriteAllBytes(StorePath, bytes[..(bytes.Length / 2)]); // cut short
        var r = Run();
        Assert.Equal(0, r.VerdictHits);
        Assert.Equal(4, Verdicts(r).Count);
        Assert.Equal(4, Run().VerdictHits); // rewritten whole

        File.WriteAllBytes(StorePath, [1, 2, 3, 4, 5]); // not a store at all
        Assert.Equal(0, Run().VerdictHits);
    }
}
