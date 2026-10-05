using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The hash ledger: v2 format round trip + v1 compatibility, warm-run cache hits (no bytes read), the hardened
/// trust rule (ChangeTime catches a restored-mtime edit; racy-clean only for coarse timestamps; unstable
/// reads never cached), --no-cache / --rehash, and CodeCompass ledgers (v2 used read-only, v1 ignored).
/// </summary>
public class HashCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-cache-" + Guid.NewGuid().ToString("N"));
    private string CacheBase => Path.Combine(_dir, "_differ");
    private string CompassBase => Path.Combine(_dir, "_compass");
    // A fine-grained (sub-second) mtime, safely in the past.
    private static readonly DateTime Old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);

    public HashCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Put(string rel, string text, DateTime? mtime = null)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        File.SetLastWriteTimeUtc(p, mtime ?? Old);
        return p;
    }

    private CompareReport Run(CacheMode mode = CacheMode.On, bool strict = true) =>
        new DirectoryComparer(new CompareOptions { Cache = mode, StrictStat = strict, CacheBaseDir = CacheBase, CodeCompassBaseDir = CompassBase })
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
    public void LedgerFormat_V2_RoundTrips_AndRejectsUnknownVersion()
    {
        var dir = Path.Combine(_dir, "ledger");
        var entries = new Dictionary<string, LedgerEntry>
        {
            ["b/z.c"] = new(10, 111, "00112233445566778899AABBCCDDEEFF", 112, 7, 113, new string('A', 64)),
            ["a.txt"] = new(0, 222, "FFEEDDCCBBAA99887766554433221100", 223, 0, 224, ""),
            ["ü/日本.h"] = new(5, 333, "0123456789ABCDEF0123456789ABCDEF", 334, -5, 335, new string('1', 64)),
        };
        LedgerFormat.Write(dir, entries);
        var back = LedgerFormat.TryRead(dir);
        Assert.NotNull(back);
        Assert.Equal(2, back!.Version);
        Assert.Equal(2, LedgerFormat.PeekVersion(dir));
        Assert.Equal(entries.OrderBy(e => e.Key, StringComparer.Ordinal), back.Entries.OrderBy(e => e.Key, StringComparer.Ordinal));

        var basePath = Directory.GetFiles(dir, "snapshot-*.base").Single();
        var bytes = File.ReadAllBytes(basePath);
        bytes[4] = 3; // a version we don't know ⇒ ignored, never guessed
        File.WriteAllBytes(basePath, bytes);
        Assert.Null(LedgerFormat.TryRead(dir));
    }

    [Fact]
    public void LedgerFormat_ReadsV1_AndTreatsZeroAndFFHashesAsAbsent()
    {
        var dir = Path.Combine(_dir, "v1");
        WriteV1Ledger(dir, [("a", 1, 10, new byte[16]), ("b", 2, 20, Enumerable.Repeat((byte)0xFF, 16).ToArray()),
            ("c", 3, 30, Enumerable.Range(1, 16).Select(i => (byte)i).ToArray())]);
        var back = LedgerFormat.TryRead(dir)!;
        Assert.Equal(1, back.Version);
        Assert.Equal("", back.Entries["a"].XxHash);
        Assert.Equal("", back.Entries["b"].XxHash);
        Assert.Equal("0102030405060708090A0B0C0D0E0F10", back.Entries["c"].XxHash);
        Assert.Equal(0, back.Entries["c"].ChangeTicks); // v1 has no ChangeTime ⇒ never trusted
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

        Assert.Equal(20, Run(strict: false).CacheHits); // listing-only mode agrees on an idle tree
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
    public void RestoredMtimeEdit_IsCaughtByChangeTime()
    {
        // The gap the due-diligence pass closed: same size, content changed, mtime put back exactly.
        // ChangeTime moves anyway, so the cached hash is NOT trusted and the edit is found.
        MakeTrees();
        Run();
        Thread.Sleep(20); // ChangeTime resolution is 100 ns, but give the clock room
        Put("R/f7.txt", "line Z\nsame\n", Old);
        var r = Run();
        Assert.Equal(19, r.CacheHits);
        Assert.Contains(r.Changes, c => c.RelativePath == "f7.txt" && c.Status == ChangeStatus.Modified);
    }

    [Fact]
    public void RacyClean_OnlyAppliesToCoarseTimestamps()
    {
        var hashedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;
        var fine = hashedAt - 10; // sub-second, a hair before the hash
        Assert.True(HashCache.RacyClean(new LedgerEntry(1, fine, "X", fine, 0, hashedAt)));
        long coarseRecent = hashedAt - TimeSpan.FromMinutes(5).Ticks; // whole seconds, within the hour
        Assert.False(HashCache.RacyClean(new LedgerEntry(1, coarseRecent, "X", coarseRecent, 0, hashedAt)));
        long coarseOld = hashedAt - TimeSpan.FromHours(2).Ticks;
        Assert.True(HashCache.RacyClean(new LedgerEntry(1, coarseOld, "X", coarseOld, 0, hashedAt)));
        Assert.False(HashCache.RacyClean(new LedgerEntry(1, coarseOld, "X", coarseOld, 0, 0))); // unknown hashedAt
    }

    [Fact]
    public void UnstableRead_IsCountedAndNotCached()
    {
        var cache = HashCache.Open(Path.Combine(_dir, "L"), CacheMode.On, cacheBaseDir: CacheBase, codeCompassBaseDir: CompassBase);
        var e = new FileEntry("x.txt", Put("L/x.txt", "x"), 1, Old, 999, 1);
        cache.Record(e, new FileHashes(new string('A', 32), new string('B', 64), Stable: false, DateTime.UtcNow.Ticks));
        Assert.Equal(1, cache.Unstable);
        cache.Save([e]);
        Assert.Empty(LedgerFormat.TryRead(Path.Combine(CacheBase, LedgerFormat.RootKey(Path.Combine(_dir, "L"))))!.Entries);
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
    public async Task CodeCompassV2Ledger_ForAncestorRoot_IsUsedReadOnly_ViaXxHash()
    {
        MakeTrees();
        // Pretend CodeCompass (v2, no SHA-256 — it doesn't compute one) indexed _dir, the parent of L.
        var entries = new Dictionary<string, LedgerEntry>();
        foreach (var f in Directory.GetFiles(Path.Combine(_dir, "L")))
        {
            var st = FileStat.OfPath(f);
            var h = await PairComparer.HashAsync(f);
            entries["L/" + Path.GetFileName(f)] = new LedgerEntry(st.Length, st.LastWriteUtcTicks,
                Path.GetFileName(f) == "f0.txt" ? new string('F', 32) : h.XxHash128, st.ChangeUtcTicks, st.FileId, DateTime.UtcNow.Ticks, "");
        }
        var ccDir = Path.Combine(CompassBase, LedgerFormat.RootKey(_dir));
        LedgerFormat.Write(ccDir, entries);
        var ccBefore = Directory.GetFiles(ccDir).Select(File.ReadAllBytes).ToList();

        var r = Run();
        Assert.Equal(9, r.CodeCompassHits); // f0 carries the binary sentinel ⇒ re-read, never trusted
        Assert.Equal(1, r.Count(ChangeStatus.Modified));
        Assert.Equal(ccBefore, Directory.GetFiles(ccDir).Select(File.ReadAllBytes).ToList()); // untouched
    }

    [Fact]
    public async Task CodeCompassV1Ledger_IsIgnored()
    {
        MakeTrees();
        var rows = new List<(string, long, long, byte[])>();
        foreach (var f in Directory.GetFiles(Path.Combine(_dir, "L")))
        {
            var fi = new FileInfo(f);
            rows.Add(("L/" + fi.Name, fi.Length, fi.LastWriteTimeUtc.Ticks, Convert.FromHexString((await PairComparer.HashAsync(f)).XxHash128)));
        }
        WriteV1Ledger(Path.Combine(CompassBase, LedgerFormat.RootKey(_dir)), rows);
        Assert.Equal(0, Run().CodeCompassHits);
    }

    /// <summary>Write a CodeCompass v1 ledger byte-for-byte (CCSN v1 base + manifest), as CodeCompass does today.</summary>
    private static void WriteV1Ledger(string dir, List<(string Path, long Size, long MTime, byte[] Hash)> rows)
    {
        Directory.CreateDirectory(dir);
        rows.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var paths = rows.Select(r => Encoding.UTF8.GetBytes(r.Path)).ToArray();
        int n = rows.Count;
        long offs = 52, blob = offs + (n + 1) * 8L, sizes = blob + paths.Sum(p => (long)p.Length), mtimes = sizes + n * 8L, hashes = mtimes + n * 8L;
        using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "snapshot-00000000.base"))))
        {
            w.Write(0x4E535343u); w.Write(1); w.Write(n);
            w.Write(offs); w.Write(blob); w.Write(sizes); w.Write(mtimes); w.Write(hashes);
            long acc = 0;
            foreach (var p in paths) { w.Write(acc); acc += p.Length; }
            w.Write(acc);
            foreach (var p in paths) w.Write(p);
            foreach (var r in rows) w.Write(r.Size);
            foreach (var r in rows) w.Write(r.MTime);
            foreach (var r in rows) w.Write(r.Hash);
        }
        File.WriteAllText(Path.Combine(dir, "snapshot.manifest"), "snapshot-00000000.base\n1\n");
    }
}
