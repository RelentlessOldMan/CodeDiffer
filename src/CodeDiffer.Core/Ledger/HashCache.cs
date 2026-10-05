using System.Collections.Concurrent;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Walk;

namespace CodeDiffer.Core.Ledger;

/// <summary>How a compare uses the hash ledger.</summary>
public enum CacheMode
{
    /// <summary>Default: reuse a cached hash when the file provably hasn't changed (see <see cref="HashCache"/>).</summary>
    On,
    /// <summary>--no-cache: never read or write a ledger; every same-size pair is proven byte by byte.</summary>
    Off,
    /// <summary>--rehash: ignore cached hashes, read everything, and rewrite the ledger from scratch.</summary>
    Rehash,
}

/// <summary>What a cache lookup produced for one file: its content hashes (SHA-256 and/or XxHash128, "" = absent).</summary>
public readonly record struct ContentId(string XxHash, string Sha256)
{
    /// <summary>Same content? SHA-256 decides when both sides have it, else XxHash128; null = can't tell.</summary>
    public static bool? Same(ContentId a, ContentId b)
        => a.Sha256.Length > 0 && b.Sha256.Length > 0 ? a.Sha256 == b.Sha256
         : a.XxHash.Length > 0 && b.XxHash.Length > 0 ? a.XxHash == b.XxHash
         : null;
}

/// <summary>
/// The per-tree hash cache (one per compared root). Sources: (1) CodeDiffer's own ledger at
/// <c>%LOCALAPPDATA%\CodeDiffer\&lt;rootKey&gt;\</c> (we are its only writer); (2) a CodeCompass ledger for this
/// root or an ANCESTOR, read-only â€” but only a v2 one: v1 entries lack ChangeTime and are never trusted.
///
/// Trust rule (clock-free; agreed with CodeCompass 2026-10-05 after the due-diligence pass):
/// <list type="number">
/// <item>Size, mtime and ChangeTime equal the live file and ChangeTime is known; FileId equal when both known.
///   ChangeTime moves on ANY content/metadata change, even when a tool restores mtime.</item>
/// <item>The hash was recorded only if the file's handle stat was identical before and after the read and
///   matched the listing (so an edit during the read is never cached).</item>
/// <item>Coarse timestamps only (no sub-second part in mtime or ChangeTime â‡’ FAT/some NAS): additionally the
///   file must be over an hour older than when it was hashed (racy-clean; a 1 h margin dwarfs clock skew).</item>
/// <item>Strict (default): the listing is only a pre-filter â€” the live HANDLE stat must match too, because a
///   directory listing can be stale while a writer holds the file open (~0.4 ms/file over SMB).</item>
/// </list>
/// A ledger that is missing, unrecognized or corrupt simply means "no cache"; every miss costs a read, never
/// a wrong answer.
/// </summary>
public sealed class HashCache
{
    private static readonly long RacyMarginTicks = TimeSpan.FromHours(1).Ticks;

    private readonly CacheMode _mode;
    private readonly bool _strict;
    private readonly string _ownDir;
    private readonly LedgerSnapshot? _own;
    private readonly LedgerSnapshot? _codeCompass;
    private readonly string _ccPrefix; // root's path under the CodeCompass-indexed ancestor ("" = same root)
    private readonly ConcurrentDictionary<string, LedgerEntry> _fresh = new(StringComparer.Ordinal);
    private int _hits, _ccHits, _unstable;

    public int Hits => _hits;
    public int CodeCompassHits => _ccHits;
    /// <summary>Files whose metadata moved while being read (live writer) â€” compared, but not cached.</summary>
    public int Unstable => _unstable;

    private HashCache(CacheMode mode, bool strict, string ownDir, LedgerSnapshot? own, LedgerSnapshot? cc, string ccPrefix)
    {
        _mode = mode;
        _strict = strict;
        _ownDir = ownDir;
        _own = own;
        _codeCompass = cc;
        _ccPrefix = ccPrefix;
    }

    public static HashCache Open(string root, CacheMode mode, bool strict = true, string? cacheBaseDir = null, string? codeCompassBaseDir = null)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var ownDir = Path.Combine(cacheBaseDir ?? DefaultBaseDir("CODEDIFFER_CACHE_DIR", "CodeDiffer"), LedgerFormat.RootKey(rootFull));
        if (mode != CacheMode.On)
            return new HashCache(mode, strict, ownDir, null, null, "");

        var own = LedgerFormat.TryRead(ownDir);
        var (cc, prefix) = FindCodeCompassLedger(rootFull, codeCompassBaseDir ?? DefaultBaseDir("CODECOMPASS_CACHE_DIR", "CodeCompass"));
        return new HashCache(mode, strict, ownDir, own, cc, prefix);
    }

    /// <summary>The trusted cached content id for this walked file, or false (â‡’ read it).</summary>
    public bool TryGet(FileEntry e, out ContentId id)
    {
        id = default;
        if (_mode != CacheMode.On) return false;

        bool fromCc = false;
        if (!(_own is not null && _own.Entries.TryGetValue(e.RelativePath, out var c) && Trusted(e, c)))
        {
            if (!(_codeCompass is not null && _codeCompass.Entries.TryGetValue(_ccPrefix + e.RelativePath, out c) && Trusted(e, c)))
                return false;
            fromCc = true;
        }
        if (_strict && !LiveMatches(e.FullPath, c)) return false;

        id = new ContentId(c.XxHash, c.Sha256);
        if (fromCc)
        {
            _fresh[e.RelativePath] = c; // carry into our own ledger
            Interlocked.Increment(ref _ccHits);
        }
        Interlocked.Increment(ref _hits);
        return true;
    }

    /// <summary>Record hashes computed this run from the file's actual bytes (only if the read was stable).</summary>
    public void Record(FileEntry e, FileHashes h)
    {
        if (_mode == CacheMode.Off) return;
        if (!h.Stable)
        {
            Interlocked.Increment(ref _unstable);
            return;
        }
        var entry = new LedgerEntry(e.Length, e.LastWriteTimeUtc.Ticks, h.XxHash128, e.ChangeTimeUtcTicks, e.FileId, h.HashedAtUtcTicks, h.Sha256);
        if (RacyClean(entry)) _fresh[e.RelativePath] = entry; // a too-fresh coarse-timestamp file would never be trusted
    }

    /// <summary>
    /// Rewrite our own ledger (v2) for exactly the files walked this run: fresh hashes first, else old entries
    /// whose identity still matches the listing. Each entry carries its own hashedAt, so nothing depends on the
    /// ledger file's timestamps. Skipped (not fatal) if another CodeDiffer holds the lock.
    /// </summary>
    public void Save(IEnumerable<FileEntry> walked)
    {
        if (_mode == CacheMode.Off) return;
        Directory.CreateDirectory(_ownDir);
        FileStream? lockFile = null;
        try
        {
            try { lockFile = new FileStream(Path.Combine(_ownDir, "ledger.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return; } // another CodeDiffer is writing this ledger â€” keep theirs

            var keep = new List<KeyValuePair<string, LedgerEntry>>();
            foreach (var e in walked)
            {
                if (_fresh.TryGetValue(e.RelativePath, out var f) && Identity(e, f))
                    keep.Add(new(e.RelativePath, f));
                else if (_own is not null && _own.Entries.TryGetValue(e.RelativePath, out var o) && Identity(e, o) && o.ChangeTicks != 0)
                    keep.Add(new(e.RelativePath, o));
            }
            LedgerFormat.Write(_ownDir, keep);
        }
        finally
        {
            lockFile?.Dispose();
        }
    }

    private static bool Trusted(FileEntry e, LedgerEntry c)
        => c.ChangeTicks != 0 &&
           Identity(e, c) &&
           (c.Sha256.Length > 0 || c.XxHash.Length > 0) &&
           RacyClean(c);

    private static bool Identity(FileEntry e, LedgerEntry c)
        => c.Size == e.Length &&
           c.MTimeTicks == e.LastWriteTimeUtc.Ticks &&
           c.ChangeTicks == e.ChangeTimeUtcTicks &&
           (c.FileId == 0 || e.FileId == 0 || c.FileId == e.FileId);

    /// <summary>Fine-grained timestamps (any sub-second part) can't be racily clean; coarse ones need the margin.</summary>
    internal static bool RacyClean(LedgerEntry c)
    {
        bool fine = c.MTimeTicks % TimeSpan.TicksPerSecond != 0 || c.ChangeTicks % TimeSpan.TicksPerSecond != 0;
        return fine || (c.HashedAtTicks != 0 && Math.Max(c.MTimeTicks, c.ChangeTicks) < c.HashedAtTicks - RacyMarginTicks);
    }

    private static bool LiveMatches(string path, LedgerEntry c)
    {
        try
        {
            var s = FileStat.OfPath(path);
            return s.Length == c.Size && s.LastWriteUtcTicks == c.MTimeTicks && s.ChangeUtcTicks == c.ChangeTicks &&
                   (s.FileId == 0 || c.FileId == 0 || s.FileId == c.FileId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>CodeCompass ledger (v2 only) for this root, or for the nearest indexed ANCESTOR (then our relative
    /// paths get that ancestor's prefix). A v1 ledger is skipped outright â€” none of its entries could be trusted,
    /// so loading it would only cost memory.</summary>
    private static (LedgerSnapshot?, string) FindCodeCompassLedger(string rootFull, string ccBase)
    {
        if (!Directory.Exists(ccBase)) return (null, "");
        for (var dir = rootFull; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var ccDir = Path.Combine(ccBase, LedgerFormat.RootKey(dir));
            if (LedgerFormat.PeekVersion(ccDir) < 2) continue;
            var ledger = LedgerFormat.TryRead(ccDir);
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
}
