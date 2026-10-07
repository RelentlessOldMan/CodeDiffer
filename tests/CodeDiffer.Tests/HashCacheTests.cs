using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The hash ledger: v2 format round trip + v1 compatibility, warm-run cache hits (no bytes read), the hardened
/// trust rule (ChangeTime catches a restored-mtime edit; record-time first look + pending settle; legacy ledgers
/// re-judged; unstable reads never cached), --no-cache / --rehash, and CodeCompass ledgers (v2 used read-only,
/// v1 ignored).
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

    // Files here were just written (ChangeTime = now), so the real 3 s margins would make every first run
    // pending; most tests use zero margins and the trust-timing tests below use real/explicit ones.
    private static readonly TrustTiming Instant = new(0, 0);

    private CompareReport Run(CacheMode mode = CacheMode.On, bool strict = true, TrustTiming? timing = null) =>
        new DirectoryComparer(new CompareOptions { Cache = mode, StrictStat = strict, CacheBaseDir = CacheBase, CodeCompassBaseDir = CompassBase, Timing = timing ?? Instant })
            .Compare(Path.Combine(_dir, "L"), Path.Combine(_dir, "R"));

    private LedgerSnapshot OwnLedger(string tree) => LedgerFormat.TryRead(Path.Combine(CacheBase, LedgerFormat.RootKey(Path.Combine(_dir, tree))))!;

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
    public void PureRenames_AreHashedOnce_ThenAnsweredFromTheLedger()
    {
        MakeTrees();
        var moved = string.Concat(Enumerable.Range(0, 2000).Select(i => $"moved line {i}\n"));
        Put("L/old/m.bin", moved);
        Put("R/new/m.bin", moved);
        Put("L/gone.txt", "short\n"); // no added file has its size: never read

        var cold = Run();
        var ren = cold.Changes.Single(c => c.Status == ChangeStatus.Renamed);
        Assert.Equal(("new/m.bin", "old/m.bin", 1000), (ren.RelativePath, ren.RenamedFrom, ren.SimilarityMilli ?? 0));
        long pairs = 2 * 10 * Encoding.UTF8.GetByteCount("line 0\nsame\n");
        Assert.Equal(pairs + 2 * moved.Length, cold.BytesRead); // the rename's two reads are counted

        var warm = Run();
        Assert.Equal(22, warm.CacheHits); // 10 pairs × 2 sides + the rename's two sides
        Assert.Equal(0, warm.BytesRead);
        Assert.Equal(cold.Changes, warm.Changes);
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
    public void FreshFile_IsPending_ThenTrustedAfterASettledReRead()
    {
        // Just written: ChangeTime is inside the first-look margin, so the cold run caches nothing trusted.
        MakeTrees();
        var slow = new TrustTiming(TimeSpan.FromHours(1).Ticks, 0); // settle 0: the next read may confirm
        var cold = Run(timing: slow);
        Assert.Equal(20, cold.PendingFiles);
        Assert.All(OwnLedger("L").Entries.Values, e => Assert.True(e.MTimeTicks < 0)); // stored pending (negated)

        var second = Run(timing: slow);
        Assert.Equal(0, second.CacheHits);     // a pending entry is never a hit: re-read...
        Assert.Equal(0, second.PendingFiles);  // ...and that settled read is trusted
        Assert.All(OwnLedger("L").Entries.Values, e => Assert.True(e.MTimeTicks > 0));

        var third = Run(timing: slow);
        Assert.Equal(20, third.CacheHits);
        Assert.Equal(0, third.BytesRead);
    }

    [Fact]
    public void PendingEntry_NeedsTheReReadToBeginSettleAfterItWasRecorded()
    {
        var t = new TrustTiming(TimeSpan.FromHours(1).Ticks, TimeSpan.FromSeconds(3).Ticks);
        long now = DateTime.UtcNow.Ticks, sec = TimeSpan.TicksPerSecond;
        var e = new FileEntry("x.txt", Put("L/x.txt", "x"), 1, Old, now - sec, 1); // ChangeTime 1 s before the read
        FileHashes H(long readStart) => new(new string('A', 32), new string('B', 64), Stable: true, readStart);

        HashCache Open() => HashCache.Open(Path.Combine(_dir, "L"), CacheMode.On, false, CacheBase, CompassBase, t);
        var c1 = Open();
        c1.Record(e, H(now), recordedAtTicks: now + sec);
        Assert.Equal(1, c1.Pending);
        c1.Save([e]);
        Assert.Equal(-Old.Ticks, OwnLedger("L").Entries["x.txt"].MTimeTicks);
        Assert.False(Open().TryGet(e, out _));

        var c2 = Open();
        c2.Record(e, H(now + 3 * sec), recordedAtTicks: now + 4 * sec); // began 2 s after recording: too soon
        Assert.Equal(1, c2.Pending);
        c2.Save([e]);

        var c3 = Open();
        c3.Record(e, H(now + 8 * sec), recordedAtTicks: now + 9 * sec); // 4 s after the latest pending record
        Assert.Equal(0, c3.Pending);
        c3.Save([e]);
        Assert.True(Open().TryGet(e, out var id));
        Assert.Equal(new string('B', 64), id.Sha256);

        // A same-size edit with the stamps put back is the case the margin exists for; a changed stamp is a miss.
        Assert.False(Open().TryGet(e with { ChangeTimeUtcTicks = now }, out _));
    }

    [Fact]
    public void LegacyOwnLedger_IsRejudged_FreshEntriesBecomePending()
    {
        long hashedAt = DateTime.UtcNow.Ticks, sec = TimeSpan.TicksPerSecond;
        var oldFile = new FileEntry("old.txt", Put("L/old.txt", "o"), 1, Old, Old.Ticks, 1);
        var freshFile = new FileEntry("fresh.txt", Put("L/fresh.txt", "f"), 1, Old, hashedAt - sec, 2);
        var dir = Path.Combine(CacheBase, LedgerFormat.RootKey(Path.Combine(_dir, "L")));
        LedgerFormat.Write(dir, [ // written under the old rule: no trust.rule marker
            new("old.txt", new LedgerEntry(1, Old.Ticks, new string('A', 32), Old.Ticks, 1, hashedAt, "")),
            new("fresh.txt", new LedgerEntry(1, Old.Ticks, new string('B', 32), hashedAt - sec, 2, hashedAt, "")),
        ]);

        var cache = HashCache.Open(Path.Combine(_dir, "L"), CacheMode.On, false, CacheBase, CompassBase, TrustTiming.Local);
        Assert.True(cache.TryGet(oldFile, out _));    // well past the margin: still trusted
        Assert.False(cache.TryGet(freshFile, out _)); // within it: demoted to pending
        cache.Save([oldFile, freshFile]);
        Assert.True(File.Exists(Path.Combine(dir, HashCache.RuleMarkerName)));
        Assert.Equal(-Old.Ticks, OwnLedger("L").Entries["fresh.txt"].MTimeTicks);
    }

    [Fact]
    public void FirstLook_UsesTheLaterOfMtimeAndChangeTime()
    {
        long start = DateTime.UtcNow.Ticks, sec = TimeSpan.TicksPerSecond;
        Assert.True(HashCache.FirstLook(start - 10 * sec, start - 5 * sec, start, TrustTiming.Local));
        Assert.False(HashCache.FirstLook(start - 10 * sec, start - 1 * sec, start, TrustTiming.Local)); // ChangeTime fresh
        Assert.False(HashCache.FirstLook(start - 10 * sec, start - 10 * sec, start, TrustTiming.Network)); // share: 1 h
        Assert.False(HashCache.FirstLook(start + sec, start + sec, start, TrustTiming.Local)); // future-dated
    }

    [Fact]
    public void NetworkRoots_GetTheLongMargin()
    {
        Assert.False(TrustTiming.IsNetwork(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_dir))));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(TrustTiming.IsNetwork(@"\\server\share\tree"));
            Assert.True(TrustTiming.IsNetwork(@"\\?\UNC\server\share\tree"));
            Assert.False(TrustTiming.IsNetwork(@"\\?\" + Path.GetFullPath(_dir)));
        }
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
