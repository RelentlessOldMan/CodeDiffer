using System.Collections.Concurrent;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Ledger;

/// <summary>
/// Why two files differ, remembered by what they hold: a modified pair's reason and noise, keyed by both sides'
/// XxHash128 and file names (a name picks the comment syntax and string rules). A warm compare then finds a modified
/// pair's verdict without opening either file, as it finds an identical one's. Being keyed by content it can't go
/// stale: an edit is another hash. It is kept per classifier version and classify size: a release that classifies anew
/// bumps <see cref="ClassifierVersion"/>, and one that doesn't keeps every verdict (re-classifying them all cost a first
/// run after each install gigabytes of allocation and a read of every modified pair).
/// One file beside the ledgers, shared by every compare; merged on save, so compares running at once each add theirs.
/// </summary>
public sealed class VerdictStore
{
    /// <summary>A release's store; a build from source keeps its own, so a classifier mid-change (its pin not yet
    /// updated) never leaves verdicts a release would trust.</summary>
    internal static readonly string FileName = IsRelease ? "verdicts.bin" : "verdicts-dev.bin";

    /// <summary>Built by build-plugin.ps1 (CDRelease), not from source.</summary>
    internal static bool IsRelease => typeof(VerdictStore).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>().Any(a => a.Key == "CDRelease" && a.Value == "true");
    private const int Magic = 0x31545644; // "DVT1"
    /// <summary>The most kept: the least recently used go first (about 150 B each).</summary>
    internal const int MaxEntries = 200_000;

    private readonly string _path, _stamp;
    private readonly bool _lookup;
    private readonly ConcurrentDictionary<string, Verdict> _entries;
    private readonly ConcurrentDictionary<string, Verdict> _touched = new(StringComparer.Ordinal);
    private int _hits;

    internal readonly record struct Verdict(ChangeReason Reason, ChangeNoise Noise, long UsedTicks);

    private VerdictStore(string path, string stamp, bool lookup, ConcurrentDictionary<string, Verdict> entries)
        => (_path, _stamp, _lookup, _entries) = (path, stamp, lookup, entries);

    /// <summary>The pairs whose verdict came from the store this run.</summary>
    public int Hits => _hits;

    /// <summary>The store for this cache base and classify size; null with the cache off. <paramref name="lookup"/>
    /// false (a rehash) records verdicts but never trusts the old ones.</summary>
    public static VerdictStore? Open(string? cacheBaseDir, long maxClassifyBytes, bool lookup, bool off)
    {
        if (off) return null;
        var dir = cacheBaseDir ?? HashCache.DefaultBaseDir("CODEDIFFER_CACHE_DIR", "CodeDiffer");
        var stamp = $"classifier {ClassifierVersion}|{maxClassifyBytes}";
        var path = Path.Combine(dir, FileName);
        return new VerdictStore(path, stamp, lookup, lookup ? Load(path, stamp) : new(StringComparer.Ordinal));
    }

    /// <summary>How pairs are classified: bump it with any change to a reason or noise any pair gets, so a release that
    /// classifies differently never reads an older one's verdicts. The classifier pin test holds the classifier's answers over a
    /// generated set of pairs (ClassifierPinTests), and fails until this is bumped (and the pin updated) when any of them change.</summary>
    internal const int ClassifierVersion = 1;

    private static readonly ChangeNoise AllNoise = Enum.GetValues<ChangeNoise>().Aggregate(ChangeNoise.None, (a, b) => a | b);

    private static string Key(ContentId l, string leftName, ContentId r, string rightName)
        => $"{l.XxHash}\0{leftName}\0{r.XxHash}\0{rightName}";

    private static bool Usable(ContentId id) => id.XxHash.Length > 0;

    public bool TryGet(ContentId l, string leftPath, ContentId r, string rightPath, out ChangeReason reason, out ChangeNoise noise)
    {
        (reason, noise) = (default, ChangeNoise.None);
        if (!_lookup || !Usable(l) || !Usable(r)) return false;
        var key = Key(l, Path.GetFileName(leftPath), r, Path.GetFileName(rightPath));
        if (!_entries.TryGetValue(key, out var v)) return false;
        _touched[key] = v with { UsedTicks = DateTime.UtcNow.Ticks };
        Interlocked.Increment(ref _hits);
        (reason, noise) = (v.Reason, v.Noise);
        return true;
    }

    public void Record(ContentId l, string leftPath, ContentId r, string rightPath, ChangeReason reason, ChangeNoise noise)
    {
        if (!Usable(l) || !Usable(r)) return;
        var key = Key(l, Path.GetFileName(leftPath), r, Path.GetFileName(rightPath));
        var v = new Verdict(reason, noise, DateTime.UtcNow.Ticks);
        _entries[key] = v;
        _touched[key] = v;
    }

    /// <summary>Write back what this run used or found, merged into what's on disk now (another compare may have
    /// saved since this one loaded). Nothing used or found: nothing written.</summary>
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
                    w.Write((byte)v.Reason); // the enums' numbers: the classifier pin holds them
                    w.Write((byte)v.Noise);
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

    /// <summary>What's on disk for this classifier, or nothing: missing, another classifier's, or unreadable (a store is only
    /// ever a shortcut, so a bad one costs a re-read, never a wrong answer).</summary>
    private static ConcurrentDictionary<string, Verdict> Load(string path, string stamp)
    {
        var d = new ConcurrentDictionary<string, Verdict>(StringComparer.Ordinal);
        try
        {
            using var r = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16));
            if (r.ReadInt32() != Magic || r.ReadString() != stamp) return d;
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadString();
                var reason = (ChangeReason)r.ReadByte();
                var noise = (ChangeNoise)r.ReadByte();
                long used = r.ReadInt64();
                if (!Enum.IsDefined(reason) || (noise & ~AllNoise) != 0) return new(StringComparer.Ordinal); // not a store this wrote
                d[k] = new Verdict(reason, noise, used);
            }
            return d;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}
