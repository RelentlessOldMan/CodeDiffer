using System.Collections.Concurrent;
using CodeDiffer.Core.Walk;

namespace CodeDiffer.Core.Ledger;

/// <summary>How a compare uses the hash ledger.</summary>
public enum CacheMode
{
    /// <summary>Default: reuse a cached hash when path+size+mtime match (outside the racy window); record new ones.</summary>
    On,
    /// <summary>--no-cache: never read or write a ledger; every same-size pair is proven byte by byte.</summary>
    Off,
    /// <summary>--rehash: ignore cached hashes, read everything, and rewrite the ledger from scratch.</summary>
    Rehash,
}

/// <summary>
/// The per-tree hash cache (one per compared root). Sources, in order:
/// (1) CodeDiffer's own ledger at <c>%LOCALAPPDATA%\CodeDiffer\&lt;rootKey&gt;\</c> (we are its only writer);
/// (2) CodeCompass's ledger for this root or an ANCESTOR of it, read-only — whoever read the bytes first
///     pays for the hash once. CodeCompass's binary sentinel counts as "no hash".
///
/// Trust rule (the user's call, 2026-10-05): a cached hash stands in for the file's bytes only when the
/// relative path, size and mtime all match AND the file's mtime is safely older than the ledger's write
/// time (racy-clean: 10 s on a network share, 2 s local — coarse timestamps and clock skew). Everything
/// else is re-read. A ledger that is missing, unrecognized or corrupt simply means "no cache".
/// </summary>
public sealed class HashCache
{
    private readonly CacheMode _mode;
    private readonly string _ownDir;
    private readonly long _racyTicks;
    private readonly long _runStartTicks = DateTime.UtcNow.Ticks;
    private readonly LedgerSnapshot? _own;
    private readonly LedgerSnapshot? _codeCompass;
    private readonly string _ccPrefix; // root's path under the CodeCompass-indexed ancestor ("" = same root)
    private readonly ConcurrentDictionary<string, LedgerEntry> _fresh = new(StringComparer.Ordinal);
    private int _hits, _ccHits;

    public int Hits => _hits;
    public int CodeCompassHits => _ccHits;
    public bool FromCodeCompass => _codeCompass is not null;

    private HashCache(CacheMode mode, string ownDir, long racyTicks, LedgerSnapshot? own, LedgerSnapshot? cc, string ccPrefix)
    {
        _mode = mode;
        _ownDir = ownDir;
        _racyTicks = racyTicks;
        _own = own;
        _codeCompass = cc;
        _ccPrefix = ccPrefix;
    }

    public static HashCache Open(string root, CacheMode mode, string? cacheBaseDir = null, string? codeCompassBaseDir = null)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var ownDir = Path.Combine(cacheBaseDir ?? DefaultBaseDir("CODEDIFFER_CACHE_DIR", "CodeDiffer"), LedgerFormat.RootKey(rootFull));
        long racy = TimeSpan.FromSeconds(IsNetworkPath(rootFull) ? 10 : 2).Ticks;
        if (mode != CacheMode.On)
            return new HashCache(mode, ownDir, racy, null, null, "");

        var own = LedgerFormat.TryRead(ownDir);
        var (cc, prefix) = FindCodeCompassLedger(rootFull, codeCompassBaseDir ?? DefaultBaseDir("CODECOMPASS_CACHE_DIR", "CodeCompass"));
        return new HashCache(mode, ownDir, racy, own, cc, prefix);
    }

    /// <summary>A trusted cached hash for this walked file, or false (⇒ read it).</summary>
    public bool TryGet(FileEntry e, out string hash)
    {
        hash = "";
        if (_mode != CacheMode.On) return false;
        if (_own is not null && _own.Entries.TryGetValue(e.RelativePath, out var o) && Trusted(e, o, _own.WrittenUtcTicks))
        {
            hash = o.Hash;
            Interlocked.Increment(ref _hits);
            return true;
        }
        if (_codeCompass is not null && _codeCompass.Entries.TryGetValue(_ccPrefix + e.RelativePath, out var c) &&
            Trusted(e, c, _codeCompass.WrittenUtcTicks))
        {
            hash = c.Hash;
            _fresh[e.RelativePath] = c; // carry into our own ledger
            Interlocked.Increment(ref _hits);
            Interlocked.Increment(ref _ccHits);
            return true;
        }
        return false;
    }

    /// <summary>Record a hash computed this run from the file's actual bytes.</summary>
    public void Record(FileEntry e, string hash)
    {
        if (_mode == CacheMode.Off) return;
        _fresh[e.RelativePath] = new LedgerEntry(e.Length, e.LastWriteTimeUtc.Ticks, hash);
    }

    /// <summary>
    /// Rewrite our own ledger for exactly the files walked this run: fresh hashes first, else still-valid old
    /// entries. An entry is written only if its file's mtime is safely older than this run's START (we can't
    /// prove a file modified within the racy window wasn't changed again after we hashed it) — so the new
    /// write time can never vouch for a racily-clean hash. Skipped (not fatal) if another writer holds the lock.
    /// </summary>
    public void Save(IEnumerable<FileEntry> walked)
    {
        if (_mode == CacheMode.Off) return;
        Directory.CreateDirectory(_ownDir);
        FileStream? lockFile = null;
        try
        {
            try { lockFile = new FileStream(Path.Combine(_ownDir, "ledger.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return; } // another CodeDiffer is writing this ledger — keep theirs

            long safeBefore = _runStartTicks - _racyTicks;
            var keep = new List<KeyValuePair<string, LedgerEntry>>();
            foreach (var e in walked)
            {
                long mtime = e.LastWriteTimeUtc.Ticks;
                if (mtime >= safeBefore) continue;
                if (_fresh.TryGetValue(e.RelativePath, out var f) && f.Size == e.Length && f.MTimeTicks == mtime)
                    keep.Add(new(e.RelativePath, f));
                else if (_own is not null && _own.Entries.TryGetValue(e.RelativePath, out var o) && o.Size == e.Length && o.MTimeTicks == mtime)
                    keep.Add(new(e.RelativePath, o));
            }
            LedgerFormat.Write(_ownDir, keep);
        }
        finally
        {
            lockFile?.Dispose();
        }
    }

    private bool Trusted(FileEntry e, LedgerEntry c, long writtenTicks)
        => c.Size == e.Length &&
           c.MTimeTicks == e.LastWriteTimeUtc.Ticks &&
           c.MTimeTicks < writtenTicks - _racyTicks &&
           c.Hash.Length == LedgerFormat.HashBytes * 2 &&
           c.Hash != LedgerFormat.BinarySentinel;

    /// <summary>CodeCompass ledger for this root, or for the nearest indexed ANCESTOR (then our relative paths
    /// get that ancestor's prefix). The prefix keeps the on-disk case of our root; CodeCompass stores paths as
    /// walked from its root, so on Windows the match of the ancestor itself is case-insensitive via the key.</summary>
    private static (LedgerSnapshot?, string) FindCodeCompassLedger(string rootFull, string ccBase)
    {
        if (!Directory.Exists(ccBase)) return (null, "");
        for (var dir = rootFull; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var ledger = LedgerFormat.TryRead(Path.Combine(ccBase, LedgerFormat.RootKey(dir)));
            if (ledger is null) continue;
            var rel = Path.GetRelativePath(dir, rootFull).Replace('\\', '/');
            return (ledger, rel == "." ? "" : rel + "/");
        }
        return (null, "");
    }

    private static string DefaultBaseDir(string envVar, string name)
        => Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } o
            ? o
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);

    private static bool IsNetworkPath(string full)
    {
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try { return new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network; }
        catch (ArgumentException) { return false; }
    }
}
