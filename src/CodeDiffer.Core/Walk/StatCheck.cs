using System.Collections.Concurrent;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeDiffer.Core.Walk;

/// <summary>
/// Why a server's files read as "changed while being read": lists a tree, then for each file compares the directory
/// listing's size, mtime, ChangeTime and FileId with what an open handle reports, the way a compare does before
/// trusting a hash (<see cref="Compare.PairComparer"/>, <see cref="Ledger.HashCache"/>). Reads at most a few KB per
/// file. The report is numbers only (no path or name), so it can be shared as is.
/// </summary>
public static class StatCheck
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    public static string Run(string root, int max, int threads, CancellationToken ct = default)
    {
        var walked = new TreeWalker([".git"], threads).WalkAll(root, ct: ct).Files;
        var files = walked.Count <= max ? walked : Enumerable.Range(0, max).Select(i => walked[(int)((long)i * walked.Count / max)]).ToList();
        var n = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        void Count(string k) => n.AddOrUpdate(k, 1, (_, v) => v + 1);
        var mdeltas = new ConcurrentBag<long>();
        var cdeltas = new ConcurrentBag<long>();
        var started = DateTime.UtcNow;

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, e =>
        {
            if (e.ChangeTimeUtcTicks == 0) Count("listing: no ChangeTime");
            if (e.FileId == 0) Count("listing: no FileId");
            if (e.LastWriteTimeUtc.Ticks % TicksPerSecond == 0) Count("listing: mtime a whole second");
            if (e.ChangeTimeUtcTicks == e.LastWriteTimeUtc.Ticks) Count("listing: ChangeTime = mtime");
            FileStat attr, before, after;
            try
            {
                attr = FileStat.OfPath(e.FullPath);
                using var h = File.OpenHandle(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.SequentialScan);
                before = FileStat.Of(h, e.FullPath);
                var buf = new byte[4096];
                RandomAccess.Read(h, buf, 0);
                after = FileStat.Of(h, e.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Count("could not open: " + ex.GetType().Name);
                return;
            }
            Count("checked");
            if (before != attr) Count("handle: data-handle stat differs from the attributes-only stat");
            if (before != after) Count("handle: stat moved across a 4 KB read");
            if (after.ChangeUtcTicks == 0) Count("handle: no ChangeTime");
            if (after.FileId == 0) Count("handle: no FileId");
            if (after.LastWriteUtcTicks % TicksPerSecond == 0) Count("handle: mtime a whole second");
            if (after.ChangeUtcTicks == after.LastWriteUtcTicks) Count("handle: ChangeTime = mtime");

            bool len = after.Length != e.Length, mt = after.LastWriteUtcTicks != e.LastWriteTimeUtc.Ticks,
                 ctm = after.ChangeUtcTicks != e.ChangeTimeUtcTicks, id = after.FileId != 0 && e.FileId != 0 && after.FileId != e.FileId;
            if (!(len || mt || ctm || id) && before == after && after.ChangeUtcTicks != 0) Count("MATCH: listing = handle (cacheable)");
            if (len) Count("DIFFERS: size");
            if (mt) { Count("DIFFERS: mtime"); mdeltas.Add(after.LastWriteUtcTicks - e.LastWriteTimeUtc.Ticks); }
            if (ctm) { Count("DIFFERS: ChangeTime"); cdeltas.Add(after.ChangeUtcTicks - e.ChangeTimeUtcTicks); }
            if (id) Count("DIFFERS: FileId");
            var which = string.Join("+", new[] { len ? "size" : null, mt ? "mtime" : null, ctm ? "ChangeTime" : null, id ? "FileId" : null }.Where(x => x is not null));
            if (which.Length > 0) Count("DIFFERS exactly in: " + which);
            if (ctm && after.ChangeUtcTicks == e.LastWriteTimeUtc.Ticks) Count("ChangeTime: handle's = listing's mtime");
            if (ctm && e.ChangeTimeUtcTicks == after.LastWriteUtcTicks) Count("ChangeTime: listing's = handle's mtime");
        });

        var sb = new StringBuilder();
        sb.AppendLine($"statcheck: {walked.Count:N0} files listed, {files.Count:N0} checked, {(DateTime.UtcNow - started).TotalSeconds:0.#} s, {threads} threads");
        foreach (var (k, v) in n.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.AppendLine($"  {k,-70} {v,9:N0}");
        Deltas(sb, "mtime", mdeltas);
        Deltas(sb, "ChangeTime", cdeltas);
        return sb.ToString();
    }

    /// <summary>How far apart the two readings are (handle minus listing): rounding (a server that lists whole seconds or
    /// 2 s, FAT-style), a time-zone or DST offset (whole half-hours), or something else.</summary>
    private static void Deltas(StringBuilder sb, string what, ConcurrentBag<long> d)
    {
        if (d.IsEmpty) return;
        var a = d.ToArray();
        int Cnt(Func<long, bool> f) => a.Count(f);
        sb.AppendLine($"  {what} handle minus listing, {a.Length:N0} files:");
        sb.AppendLine($"    handle later {Cnt(x => x > 0):N0} · handle earlier {Cnt(x => x < 0):N0}");
        sb.AppendLine($"    |delta| < 1 ms {Cnt(x => Math.Abs(x) < 10_000):N0} · < 1 s {Cnt(x => Math.Abs(x) >= 10_000 && Math.Abs(x) < TicksPerSecond):N0} · " +
                      $"1-2 s {Cnt(x => Math.Abs(x) >= TicksPerSecond && Math.Abs(x) < 2 * TicksPerSecond):N0} · 2 s-1 h {Cnt(x => Math.Abs(x) >= 2 * TicksPerSecond && Math.Abs(x) < 3600 * TicksPerSecond):N0} · " +
                      $">= 1 h {Cnt(x => Math.Abs(x) >= 3600 * TicksPerSecond):N0}");
        long halfHour = 1800 * TicksPerSecond;
        sb.AppendLine($"    a whole number of half-hours (time zone / DST) {Cnt(x => x != 0 && x % halfHour == 0):N0}");
        Array.Sort(a);
        sb.AppendLine($"    min {a[0]:N0} · median {a[a.Length / 2]:N0} · max {a[^1]:N0} ticks (1 tick = 100 ns)");
    }
}
