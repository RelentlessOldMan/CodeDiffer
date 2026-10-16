using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
        var seen = new ConcurrentBag<Seen>();
        var started = DateTime.UtcNow;

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, e =>
        {
            if (e.ChangeTimeUtcTicks == 0) Count("listing: no ChangeTime");
            if (e.FileId == 0) Count("listing: no FileId");
            if (e.LastWriteTimeUtc.Ticks % TicksPerSecond == 0) Count("listing: mtime a whole second");
            if (e.ChangeTimeUtcTicks == e.LastWriteTimeUtc.Ticks) Count("listing: ChangeTime = mtime");
            FileStat attr, before, after;
            HandleIds ids;
            try
            {
                var path = e.FullPath;
                attr = FileStat.OfPath(path);
                using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.SequentialScan);
                before = FileStat.Of(h, path);
                var buf = new byte[4096];
                RandomAccess.Read(h, buf, 0);
                after = FileStat.Of(h, path);
                ids = HandleIds.Of(h);
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
            seen.Add(new Seen(e, after, ids, id));
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
        FileIds(sb, root, seen.ToArray(), threads, ct);
        return sb.ToString();
    }

    internal readonly record struct Seen(FileEntry Entry, FileStat Handle, HandleIds Ids, bool IdDiffers);

    /// <summary>What a handle says about the file beyond <see cref="FileStat"/>: its link count, its volume and its
    /// 128-bit id (0 when the server gives none).</summary>
    internal readonly record struct HandleIds(uint Links, uint Volume, ulong Id128Low, ulong Id128High)
    {
        public static HandleIds Of(SafeFileHandle h)
        {
            if (!OperatingSystem.IsWindows() || !GetFileInformationByHandle(h, out var info)) return default;
            return GetFileInformationByHandleEx(h, 18 /* FileIdInfo */, out var ex, (uint)Marshal.SizeOf<RealPath.FileIdInfo>())
                ? new HandleIds(info.NumberOfLinks, info.VolumeSerialNumber, ex.IdLow, ex.IdHigh)
                : new HandleIds(info.NumberOfLinks, info.VolumeSerialNumber, 0, 0);
        }
    }

    /// <summary>
    /// When the listing's FileId and the handle's disagree, what the two are: whether each holds still (the tree
    /// listed again, the file opened again), how they relate bit for bit, and which files they are (size, attributes,
    /// link count, folder), each set against the files whose ids agree. Counts only.
    /// </summary>
    internal static void FileIds(StringBuilder sb, string root, Seen[] seen, int threads, CancellationToken ct)
    {
        var odd = seen.Where(s => s.IdDiffers).ToArray();
        if (odd.Length == 0) return;
        var started = DateTime.UtcNow;
        var n = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        void Count(string k) => n.AddOrUpdate(k, 1, (_, v) => v + 1);
        string Group(Seen s) => s.IdDiffers ? "differs" : "agrees ";

        // The tree listed a second time (after every file was opened once) and each odd file opened again.
        var byDir = seen.GroupBy(s => Path.GetDirectoryName(s.Entry.FullPath) ?? "").ToArray();
        var relisted = new ConcurrentDictionary<string, DirRow>(StringComparer.Ordinal);
        Parallel.ForEach(byDir, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, g =>
        {
            List<DirRow> rows;
            try { rows = DirectoryLister.List(g.Key); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Count("relist: folder could not be listed again"); return; }
            var byName = new Dictionary<string, DirRow>(StringComparer.Ordinal);
            foreach (var r in rows) byName[r.Name] = r;
            foreach (var s in g)
                if (byName.TryGetValue(Path.GetFileName(s.Entry.FullPath), out var r)) relisted[s.Entry.FullPath] = r;
        });
        var reopened = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        Parallel.ForEach(odd, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, s =>
        {
            try { reopened[s.Entry.FullPath] = FileStat.OfPath(s.Entry.FullPath).FileId; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Count("reopen: could not open"); }
        });

        uint rootVolume = 0;
        try { if (OperatingSystem.IsWindows() && RealPath.Identity(root) is { } rid) rootVolume = rid.Volume; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var listingIds = seen.GroupBy(s => s.Entry.FileId).ToDictionary(g => g.Key, g => g.Count());
        var handleIds = seen.GroupBy(s => s.Handle.FileId).ToDictionary(g => g.Key, g => g.Count());

        long oddBytes = 0, allBytes = 0;
        foreach (var s in seen)
        {
            allBytes += s.Entry.Length;
            if (s.IdDiffers) oddBytes += s.Entry.Length;
            string g = Group(s);
            Count($"{g} · size {Bucket(s.Entry.Length)}");
            if (s.Ids.Links > 1) Count($"{g} · hard links > 1");
            if (rootVolume != 0 && s.Ids.Volume != 0 && s.Ids.Volume != rootVolume) Count($"{g} · on another volume than the root");
            if (relisted.TryGetValue(s.Entry.FullPath, out var row))
            {
                foreach (var (bit, name) in Attributes)
                    if ((row.Attributes & bit) != 0) Count($"{g} · attribute {name}");
                if (row.ReparseTag != 0) Count($"{g} · reparse tag 0x{row.ReparseTag:X8}");
            }
            if (!s.IdDiffers) continue;

            ulong l = (ulong)s.Entry.FileId, h = (ulong)s.Handle.FileId;
            if (l >> 32 == 0) Count("ids · listing's high 32 bits are 0");
            if (h >> 32 == 0) Count("ids · handle's high 32 bits are 0");
            if ((uint)l == (uint)h) Count("ids · low 32 bits equal");
            if (l >> 32 == h >> 32) Count("ids · high 32 bits equal");
            if ((uint)l == (uint)(h >> 32) && (uint)(l >> 32) == (uint)h) Count("ids · halves swapped");
            if (listingIds[s.Entry.FileId] > 1) Count("ids · listing's id shared with another file listed");
            if (handleIds[s.Handle.FileId] > 1) Count("ids · handle's id shared with another file opened");
            if (s.Ids.Id128Low == 0 && s.Ids.Id128High == 0) Count("ids · handle has no 128-bit id");
            else
            {
                if (s.Ids.Id128High != 0) Count("ids · handle's 128-bit id: high 64 bits not 0");
                if (s.Ids.Id128Low == l) Count("ids · handle's 128-bit id: low 64 = listing's id");
                if (s.Ids.Id128Low == h) Count("ids · handle's 128-bit id: low 64 = handle's id");
            }
            if (relisted.TryGetValue(s.Entry.FullPath, out var again))
            {
                if (again.FileIndex != 0 && again.FileIndex == (uint)h) Count("ids · handle's low 32 bits = listing's FileIndex field");
                Count(again.FileId == s.Entry.FileId ? "relist · same id as the first listing"
                    : again.FileId == s.Handle.FileId ? "relist · now the handle's id"
                    : "relist · another id");
            }
            else Count("relist · file not listed again");
            if (reopened.TryGetValue(s.Entry.FullPath, out var id2))
                Count(id2 == s.Handle.FileId ? "reopen · same id as the first open"
                    : id2 == s.Entry.FileId ? "reopen · now the listing's id"
                    : "reopen · another id");
        }
        int mixed = 0, whole = 0;
        foreach (var g in byDir)
        {
            int k = g.Count(s => s.IdDiffers);
            if (k == 0) continue;
            if (k == g.Count()) whole++; else mixed++;
        }

        sb.AppendLine($"  FileId, listing vs handle: {odd.Length:N0} of {seen.Length:N0} files differ, {Gb(oddBytes)} of {Gb(allBytes)}; " +
                      $"{(DateTime.UtcNow - started).TotalSeconds:0.#} s");
        sb.AppendLine($"    folders: {whole:N0} with every file differing · {mixed:N0} with some · {byDir.Length - whole - mixed:N0} with none");
        foreach (var (k, v) in n.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.AppendLine($"    {k,-68} {v,9:N0}");
    }

    private static string Gb(long bytes) => $"{bytes / 1e9:0.0} GB";

    private static string Bucket(long b) => b switch
    {
        < 64 << 10 => "1: < 64 KB",
        < 1 << 20 => "2: 64 KB-1 MB",
        < 16 << 20 => "3: 1-16 MB",
        < 256 << 20 => "4: 16-256 MB",
        _ => "5: >= 256 MB",
    };

    private static readonly (uint Bit, string Name)[] Attributes =
    [
        (0x1, "read-only"), (0x2, "hidden"), (0x4, "system"), (0x20, "archive"), (0x100, "temporary"), (0x200, "sparse"),
        (0x400, "reparse point"), (0x800, "compressed"), (0x1000, "offline"), (0x2000, "not content indexed"),
        (0x4000, "encrypted"), (0x8000, "integrity stream"), (0x40000, "recall on open"), (0x80000, "pinned"),
        (0x100000, "unpinned"), (0x400000, "recall on data access"),
    ];

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out RealPath.ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out RealPath.FileIdInfo info, uint size);

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
