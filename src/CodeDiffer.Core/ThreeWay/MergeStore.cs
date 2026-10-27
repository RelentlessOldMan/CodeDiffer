using System.Collections.Concurrent;
using CodeDiffer.Core.Ledger;

namespace CodeDiffer.Core.ThreeWay;

/// <summary>
/// How a file both sides modified merged, remembered by what its three versions hold: the outcome, conflict kind, region
/// counts and note, keyed by the base's, v1's and v2's XxHash128. A warm compare3 then classifies such a file without
/// opening any of the three (the merge read them all whole: over a share, most of its time). Being keyed by content it
/// can't go stale: an edit is another hash. It is kept per merger version, classify size and stamp resolving: a release
/// that merges anew bumps <see cref="MergerVersion"/>. One file beside the ledgers and <see cref="VerdictStore"/>'s,
/// merged on save. Only ever a shortcut: a missing, foreign or unreadable store costs a read, never a wrong answer.
/// </summary>
public sealed class MergeStore
{
    internal static readonly string FileName = VerdictStore.IsRelease ? "merges.bin" : "merges-dev.bin";
    private const int Magic = 0x3147524D; // "MRG1"
    /// <summary>The most kept: the least recently used go first (about 200 B each).</summary>
    internal const int MaxEntries = 100_000;

    /// <summary>How a file's three versions are merged: bump it with any change to an outcome, conflict kind, region count
    /// or note a merge gives, so a release that merges differently never reads an older one's. The merger pin test fails
    /// until this is bumped (and the pin updated) when the merge's sources change (MergerPinTests).</summary>
    internal const int MergerVersion = 1;

    private readonly string _path, _stamp;
    private readonly bool _lookup;
    private readonly ConcurrentDictionary<string, Merged> _entries;
    private readonly ConcurrentDictionary<string, Merged> _touched = new(StringComparer.Ordinal);
    private int _hits;

    internal sealed record Merged(Merge3Outcome Outcome, string? ConflictKind, int ConflictRegions, int CleanRegions, string? Note, long UsedTicks);

    private MergeStore(string path, string stamp, bool lookup, ConcurrentDictionary<string, Merged> entries)
        => (_path, _stamp, _lookup, _entries) = (path, stamp, lookup, entries);

    /// <summary>Files whose merge came from the store this run.</summary>
    public int Hits => _hits;

    /// <summary>The store for this cache base, classify size and stamp resolving; null with the cache off.
    /// <paramref name="lookup"/> false (a rehash) records merges but never trusts the old ones.</summary>
    public static MergeStore? Open(string? cacheBaseDir, long maxClassifyBytes, bool resolveStamps, bool lookup, bool off)
    {
        if (off) return null;
        var dir = cacheBaseDir ?? HashCache.DefaultBaseDir("CODEDIFFER_CACHE_DIR", "CodeDiffer");
        var stamp = $"merger {MergerVersion}|{maxClassifyBytes}|stamps {(resolveStamps ? 1 : 0)}";
        var path = Path.Combine(dir, FileName);
        return new MergeStore(path, stamp, lookup, lookup ? Load(path, stamp) : new(StringComparer.Ordinal));
    }

    /// <summary>The key of three versions, or null when one's content isn't known.</summary>
    internal static string? Key(ContentId b, ContentId v1, ContentId v2)
        => b.XxHash.Length > 0 && v1.XxHash.Length > 0 && v2.XxHash.Length > 0 ? $"{b.XxHash}\0{v1.XxHash}\0{v2.XxHash}" : null;

    internal bool TryGet(string key, out Merged merged)
    {
        merged = null!;
        if (!_lookup || !_entries.TryGetValue(key, out var m)) return false;
        _touched[key] = m with { UsedTicks = DateTime.UtcNow.Ticks };
        Interlocked.Increment(ref _hits);
        merged = m;
        return true;
    }

    internal void Record(string key, Merge3Entry e)
    {
        var m = new Merged(e.Outcome, e.ConflictKind, e.ConflictRegions, e.CleanRegions, e.Note, DateTime.UtcNow.Ticks);
        _entries[key] = m;
        _touched[key] = m;
    }

    /// <summary>Write back what this run used or found, merged into what's on disk now. Nothing used or found: nothing written.</summary>
    public void Save()
    {
        if (_touched.IsEmpty) return;
        var merged = Load(_path, _stamp);
        foreach (var (k, v) in _touched) merged[k] = v;
        var keep = merged.Count <= MaxEntries ? merged.ToArray()
            : merged.OrderByDescending(e => e.Value.UsedTicks).Take(MaxEntries).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var w = new BinaryWriter(new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16)))
            {
                w.Write(Magic);
                w.Write(_stamp);
                w.Write(keep.Length);
                foreach (var (k, v) in keep)
                {
                    w.Write(k);
                    w.Write((byte)v.Outcome);
                    w.Write(v.ConflictKind ?? "");
                    w.Write(v.ConflictRegions);
                    w.Write(v.CleanRegions);
                    w.Write(v.Note is null ? (byte)0 : (byte)1);
                    if (v.Note is not null) w.Write(v.Note);
                    w.Write(v.UsedTicks);
                }
            }
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static ConcurrentDictionary<string, Merged> Load(string path, string stamp)
    {
        var d = new ConcurrentDictionary<string, Merged>(StringComparer.Ordinal);
        try
        {
            using var r = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16));
            if (r.ReadInt32() != Magic || r.ReadString() != stamp) return d;
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadString();
                var outcome = (Merge3Outcome)r.ReadByte();
                var kind = r.ReadString();
                int conflicts = r.ReadInt32(), clean = r.ReadInt32();
                var note = r.ReadByte() == 1 ? r.ReadString() : null;
                long used = r.ReadInt64();
                if (!Enum.IsDefined(outcome) || conflicts < 0 || clean < 0) return new(StringComparer.Ordinal); // not a store this wrote
                d[k] = new Merged(outcome, kind.Length == 0 ? null : kind, conflicts, clean, note, used);
            }
            return d;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}
