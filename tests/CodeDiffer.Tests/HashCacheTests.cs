using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The hash ledger: CodeCompass-compatible format round trip, warm-run cache hits (no bytes read), the
/// trust rule (path+size+mtime, racy-clean), --no-cache / --rehash, and reading a CodeCompass ledger
/// for an ancestor root read-only.
/// </summary>
public class HashCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-cache-" + Guid.NewGuid().ToString("N"));
    private string CacheBase => Path.Combine(_dir, "_differ");
    private string CompassBase => Path.Combine(_dir, "_compass");
    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public HashCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Put(string rel, string text, DateTime? mtime = null)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        File.SetLastWriteTimeUtc(p, mtime ?? Old); // safely older than any ledger write ⇒ not racy
        return p;
    }

    private CompareReport Run(CacheMode mode = CacheMode.On) =>
        new DirectoryComparer(new CompareOptions { Cache = mode, CacheBaseDir = CacheBase, CodeCompassBaseDir = CompassBase })
            .Compare(Path.Combine(_dir, "L"), Path.Combine(_dir, "R"));

    private void MakeTrees()
    {
        for (int i = 0; i < 10; i++)
        {
            Put($"L/f{i}.txt", $"line {i}\nsame\n");
            Put($"R/f{i}.txt", i == 3 ? $"LINE {i}\nsame\n" : $"line {i}\nsame\n"); // f3: same size, different
        }
    }

    [Fact]
    public void LedgerFormat_RoundTrips_AndRejectsUnknownVersion()
    {
        var dir = Path.Combine(_dir, "ledger");
        var entries = new Dictionary<string, LedgerEntry>
        {
            ["b/z.c"] = new(10, 111, "00112233445566778899AABBCCDDEEFF"),
            ["a.txt"] = new(0, 222, "FFEEDDCCBBAA99887766554433221100"),
            ["ü/日本.h"] = new(5, 333, "0123456789ABCDEF0123456789ABCDEF"),
        };
        LedgerFormat.Write(dir, entries);
        var back = LedgerFormat.TryRead(dir);
        Assert.NotNull(back);
        Assert.Equal(entries.OrderBy(e => e.Key, StringComparer.Ordinal), back!.Entries.OrderBy(e => e.Key, StringComparer.Ordinal));
        Assert.True(back.WrittenUtcTicks > 0);

        // Corrupt the version field ⇒ ignored (null), never guessed.
        var basePath = Directory.GetFiles(dir, "snapshot-*.base").Single();
        var bytes = File.ReadAllBytes(basePath);
        bytes[4] = 2;
        File.WriteAllBytes(basePath, bytes);
        Assert.Null(LedgerFormat.TryRead(dir));
    }

    [Fact]
    public void RewritingLedger_ReplacesBase_AndDropsOldOne()
    {
        var dir = Path.Combine(_dir, "ledger");
        LedgerFormat.Write(dir, [new("a", new LedgerEntry(1, 1, new string('A', 32)))]);
        LedgerFormat.Write(dir, [new("b", new LedgerEntry(2, 2, new string('B', 32)))]);
        Assert.Single(Directory.GetFiles(dir, "snapshot-*.base"));
        Assert.Equal(["b"], LedgerFormat.TryRead(dir)!.Entries.Keys);
    }

    [Fact]
    public void WarmRun_AnswersFromCache_ReadsNoBytes_SameVerdicts()
    {
        MakeTrees();
        var cold = Run();
        Assert.Equal(0, cold.CacheHits);
        Assert.True(cold.BytesRead > 0);
        Assert.Equal(1, cold.Count(ChangeStatus.Modified));

        var warm = Run();
        Assert.Equal(20, warm.CacheHits);  // 10 pairs × 2 sides
        Assert.Equal(0, warm.BytesRead);
        Assert.Equal(cold.Changes, warm.Changes);
    }

    [Fact]
    public void EditWithNewMtime_IsReRead_AndDetected()
    {
        MakeTrees();
        Run();
        Put("R/f5.txt", "line X\nsame\n", Old.AddHours(1)); // same size, new mtime
        var r = Run();
        Assert.Equal(19, r.CacheHits); // only R/f5 re-read
        Assert.Contains(r.Changes, c => c.RelativePath == "f5.txt" && c.Status == ChangeStatus.Modified);
    }

    [Fact]
    public void SameSizeSameMtimeEdit_IsTrusted_ButNoCacheCatchesIt()
    {
        // The trust rule the user chose: path+size+mtime match ⇒ cached hash stands. A sneaky edit that
        // preserves both is invisible to the cache — and --no-cache is the escape hatch that proves bytes.
        MakeTrees();
        Run();
        Put("R/f7.txt", "line Z\nsame\n", Old);
        Assert.DoesNotContain(Run().Changes, c => c.RelativePath == "f7.txt" && c.Status == ChangeStatus.Modified);
        Assert.Contains(Run(CacheMode.Off).Changes, c => c.RelativePath == "f7.txt" && c.Status == ChangeStatus.Modified);
    }

    [Fact]
    public void RacilyCleanFiles_AreNotCached()
    {
        Put("L/a.txt", "x");
        Put("R/a.txt", "x", DateTime.UtcNow); // modified "now" ⇒ inside the racy window
        Run();
        var r = Run();
        Assert.Equal(1, r.CacheHits); // L side cached; R side always re-read
    }

    [Fact]
    public void NoCache_NeitherReadsNorWritesLedger()
    {
        MakeTrees();
        var r = Run(CacheMode.Off);
        Assert.Equal(0, r.CacheHits);
        Assert.False(Directory.Exists(CacheBase));
    }

    [Fact]
    public void Rehash_IgnoresCache_ButRefreshesIt()
    {
        MakeTrees();
        Run();
        Assert.Equal(0, Run(CacheMode.Rehash).CacheHits);
        Assert.Equal(20, Run().CacheHits);
    }

    [Fact]
    public void CodeCompassLedger_ForAncestorRoot_IsUsedReadOnly_AndBinarySentinelIgnored()
    {
        MakeTrees();
        // Pretend CodeCompass indexed _dir (the parent of L): its paths carry the "L/" prefix.
        var hashes = new Dictionary<string, LedgerEntry>();
        foreach (var f in Directory.GetFiles(Path.Combine(_dir, "L")))
        {
            var name = Path.GetFileName(f);
            var hash = PairComparer.HashAsync(f).GetAwaiter().GetResult();
            if (name == "f0.txt") hash = LedgerFormat.BinarySentinel;
            hashes["L/" + name] = new LedgerEntry(new FileInfo(f).Length, File.GetLastWriteTimeUtc(f).Ticks, hash);
        }
        var ccDir = Path.Combine(CompassBase, LedgerFormat.RootKey(_dir));
        LedgerFormat.Write(ccDir, hashes);
        var ccBefore = Directory.GetFiles(ccDir).Select(File.ReadAllBytes).ToList();

        var r = Run();
        Assert.Equal(9, r.CodeCompassHits); // f0 is the sentinel ⇒ re-read, never trusted
        Assert.Equal(1, r.Count(ChangeStatus.Modified));
        Assert.Equal(ccBefore, Directory.GetFiles(ccDir).Select(File.ReadAllBytes).ToList()); // untouched
    }
}
