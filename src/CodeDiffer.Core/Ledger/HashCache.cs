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
/// The ledger's trust margins. FirstLook: how much older than the read's start a file's mtime and ChangeTime
/// must be to trust the hash at once (3 s local; 1 h on a network share, whose stamps come from the server's
/// clock). Settle: how long after a PENDING entry was recorded a re-read must begin to trust it (our clock only).
/// </summary>
internal sealed record TrustTiming(long FirstLookTicks, long SettleTicks)
{
    public static readonly TrustTiming Local = new(TimeSpan.FromSeconds(3).Ticks, TimeSpan.FromSeconds(3).Ticks);
    public static readonly TrustTiming Network = new(TimeSpan.FromHours(1).Ticks, TimeSpan.FromSeconds(3).Ticks);

    public static TrustTiming For(string rootFull) => IsNetwork(rootFull) ? Network : Local;

    /// <summary>UNC paths, mapped drives, and network mounts (NFS/CIFS/SMB on Linux/macOS — .NET reports them
    /// as DriveType.Network). If the drive can't be identified, assume network: the longer margin only costs a
    /// re-read, never a wrong answer.</summary>
    internal static bool IsNetwork(string rootFull)
    {
        if (OperatingSystem.IsWindows())
        {
            if (rootFull.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
            if (rootFull.StartsWith(@"\\?\", StringComparison.Ordinal) || rootFull.StartsWith(@"\\.\", StringComparison.Ordinal))
                rootFull = rootFull[4..];
            else if (rootFull.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        }
        try
        {
            DriveInfo? best = null;
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var d in DriveInfo.GetDrives())
            {
                var mount = d.RootDirectory.FullName;
                bool under = rootFull.StartsWith(mount, cmp) &&
                             (rootFull.Length == mount.Length || mount.EndsWith(Path.DirectorySeparatorChar) || rootFull[mount.Length] == Path.DirectorySeparatorChar);
                if (under && (best is null || mount.Length > best.RootDirectory.FullName.Length)) best = d;
            }
            return best is null || best.DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}

/// <summary>
/// The per-tree hash cache (one per compared root). Sources: (1) CodeDiffer's own ledger at
/// <c>%LOCALAPPDATA%\CodeDiffer\&lt;rootKey&gt;\</c> (we are its only writer); (2) a CodeCompass ledger for this
/// root or an ANCESTOR, read-only — but only a v2 one: v1 entries lack ChangeTime and are never trusted.
///
/// Trust is decided when an entry is RECORDED (the same rule as CodeCompass, 2026-10-06); the reader only checks
/// identity. Timestamps only advance on a ~1–16 ms timer, so a same-size rewrite just after our read can land on
/// the very same mtime AND ChangeTime — "a sub-second stamp can't be racy" is false. So:
/// <list type="number">
/// <item>Recorded only if the file's handle stat was identical before and after the read and matched the
///   listing (an edit DURING the read is never cached).</item>
/// <item>First look: trusted if mtime and ChangeTime are both at least <see cref="TrustTiming.FirstLookTicks"/>
///   older than the read's start.</item>
/// <item>Otherwise PENDING: stored with mtime negated and HashedAt = when it was recorded (our clock). A later
///   stable read that begins <see cref="TrustTiming.SettleTicks"/> after that, with size/mtime/ChangeTime/FileId
///   unchanged, is trusted: every write that could carry those stamps happened before the pending entry was
///   recorded.</item>
/// <item>Reader: trusted iff mtime &gt; 0, ChangeTime known, and size, mtime, ChangeTime (and FileId when both
///   known) equal the live file. Strict (default): the live HANDLE stat must match too, because a directory
///   listing can be stale while a writer holds the file open (~0.4 ms/file over SMB).</item>
/// </list>
/// mtime 0 = unknown, negative = pending; neither ever equals a live stamp. An own ledger written before this
/// rule (no <c>trust.rule</c> marker) is re-judged on load: entries that fail the first look become pending.
/// A ledger that is missing, unrecognized or corrupt simply means "no cache"; every miss costs a read, never
/// a wrong answer.
/// </summary>
public sealed class HashCache
{
    internal const string RuleMarkerName = "trust.rule"; // present ⇒ entries were judged by the record-time rule

    private readonly CacheMode _mode;
    private readonly bool _strict;
    private readonly string _ownDir;
    private readonly LedgerSnapshot? _own;
    // Rehash: the old ledger, never trusted for a lookup, but kept by Save for files this run didn't read
    // (a cancelled rehash, or compare3's second pass over the base) — so a rehash never loses hashes.
    private LedgerSnapshot? _prior;
    private readonly LedgerSnapshot? _codeCompass;
    private readonly string _ccPrefix; // root's path under the CodeCompass-indexed ancestor ("" = same root)
    private readonly TrustTiming _timing;
    private readonly ConcurrentDictionary<string, LedgerEntry> _fresh = new(StringComparer.Ordinal);
    private int _hits, _ccHits, _unstable, _pending;

    public int Hits => _hits;
    public int CodeCompassHits => _ccHits;
    /// <summary>Files whose metadata moved while being read (live writer) — compared, but not cached.</summary>
    public int Unstable => _unstable;
    /// <summary>Files hashed this run but modified too recently to trust yet (re-read and trusted on a later run).</summary>
    public int Pending => _pending;

    private HashCache(CacheMode mode, bool strict, string ownDir, LedgerSnapshot? own, LedgerSnapshot? cc, string ccPrefix, TrustTiming timing)
    {
        _mode = mode;
        _strict = strict;
        _ownDir = ownDir;
        _own = own;
        _codeCompass = cc;
        _ccPrefix = ccPrefix;
        _timing = timing;
    }

    public static HashCache Open(string root, CacheMode mode, bool strict = true, string? cacheBaseDir = null, string? codeCompassBaseDir = null)
        => Open(root, mode, strict, cacheBaseDir, codeCompassBaseDir, null);

    internal static HashCache Open(string root, CacheMode mode, bool strict, string? cacheBaseDir, string? codeCompassBaseDir, TrustTiming? timing)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var ownDir = Path.Combine(cacheBaseDir ?? DefaultBaseDir("CODEDIFFER_CACHE_DIR", "CodeDiffer"), LedgerFormat.RootKey(rootFull));
        var t = timing ?? TrustTiming.For(rootFull);
        if (mode == CacheMode.Off)
            return new HashCache(mode, strict, ownDir, null, null, "", t);

        var own = LedgerFormat.TryRead(ownDir);
        if (own is not null && !File.Exists(Path.Combine(ownDir, RuleMarkerName)))
            own = Rejudge(own, t);
        if (mode == CacheMode.Rehash)
            return new HashCache(mode, strict, ownDir, null, null, "", t) { _prior = own };
        var (cc, prefix) = FindCodeCompassLedger(rootFull, codeCompassBaseDir ?? DefaultBaseDir("CODECOMPASS_CACHE_DIR", "CodeCompass"));
        return new HashCache(mode, strict, ownDir, own, cc, prefix, t);
    }

    /// <summary>The trusted cached content id for this walked file, or false (⇒ read it).</summary>
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

    /// <summary>Record hashes computed this run from the file's actual bytes (only if the read was stable), as
    /// trusted (first or second look) or pending.</summary>
    public void Record(FileEntry e, FileHashes h) => Record(e, h, DateTime.UtcNow.Ticks);

    internal void Record(FileEntry e, FileHashes h, long recordedAtTicks)
    {
        if (_mode == CacheMode.Off) return;
        if (!h.Stable)
        {
            Interlocked.Increment(ref _unstable);
            return;
        }
        long mtime = e.LastWriteTimeUtc.Ticks;
        if (mtime <= 0 || e.ChangeTimeUtcTicks == 0 || h.HashedAtUtcTicks == 0) return; // can't vouch ⇒ don't cache

        var entry = new LedgerEntry(e.Length, mtime, h.XxHash128, e.ChangeTimeUtcTicks, e.FileId, h.HashedAtUtcTicks, h.Sha256);
        bool settled = _own is not null && _own.Entries.TryGetValue(e.RelativePath, out var p) && p.MTimeTicks < 0 &&
                       Identity(e, p) && h.HashedAtUtcTicks >= p.HashedAtTicks + _timing.SettleTicks;
        if (!settled && !FirstLook(mtime, e.ChangeTimeUtcTicks, h.HashedAtUtcTicks, _timing))
        {
            entry = entry with { MTimeTicks = -mtime, HashedAtTicks = recordedAtTicks };
            Interlocked.Increment(ref _pending);
        }
        _fresh[e.RelativePath] = entry;
    }

    /// <summary>
    /// Rewrite our own ledger (v2) for exactly the files walked this run: fresh entries first, else old entries
    /// (trusted or pending) whose identity still matches the listing. Then mark it as judged by the record-time
    /// rule. Skipped (not fatal) if another CodeDiffer holds the lock.
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

            var old = _own ?? _prior;
            var keep = new List<KeyValuePair<string, LedgerEntry>>();
            foreach (var e in walked)
            {
                if (_fresh.TryGetValue(e.RelativePath, out var f) && Identity(e, f))
                    keep.Add(new(e.RelativePath, f));
                else if (old is not null && old.Entries.TryGetValue(e.RelativePath, out var o) && Identity(e, o) && o.ChangeTicks != 0)
                    keep.Add(new(e.RelativePath, o));
            }
            LedgerFormat.Write(_ownDir, keep);
            File.WriteAllText(Path.Combine(_ownDir, RuleMarkerName), "record-time trust (first look + pending settle)\n");
        }
        finally
        {
            lockFile?.Dispose();
        }
    }

    /// <summary>First look: both stamps at least the margin older than the read's start.</summary>
    internal static bool FirstLook(long mtime, long ctime, long readStart, TrustTiming t)
        => Math.Max(mtime, ctime) <= readStart - t.FirstLookTicks;

    /// <summary>A ledger written under the old reader-side rule: keep what passes the first look, demote the rest
    /// to pending. Its HashedAt (just before the old read) is after the stamps were listed, so it is a valid
    /// "recorded" anchor; an entry without one is dropped.</summary>
    private static LedgerSnapshot Rejudge(LedgerSnapshot own, TrustTiming t)
    {
        var map = new Dictionary<string, LedgerEntry>(own.Entries.Count, StringComparer.Ordinal);
        foreach (var (path, c) in own.Entries)
        {
            if (c.MTimeTicks <= 0 || c.HashedAtTicks == 0) continue;
            map[path] = FirstLook(c.MTimeTicks, c.ChangeTicks, c.HashedAtTicks, t) ? c : c with { MTimeTicks = -c.MTimeTicks };
        }
        return own with { Entries = map };
    }

    private static bool Trusted(FileEntry e, LedgerEntry c)
        => c.MTimeTicks > 0 &&
           c.ChangeTicks != 0 &&
           Identity(e, c) &&
           (c.Sha256.Length > 0 || c.XxHash.Length > 0);

    /// <summary>Same file as listed — for a pending entry, against its un-negated mtime.</summary>
    private static bool Identity(FileEntry e, LedgerEntry c)
        => c.Size == e.Length &&
           c.MTimeTicks != 0 && Math.Abs(c.MTimeTicks) == e.LastWriteTimeUtc.Ticks &&
           c.ChangeTicks == e.ChangeTimeUtcTicks &&
           (c.FileId == 0 || e.FileId == 0 || c.FileId == e.FileId);

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
    /// paths get that ancestor's prefix). A v1 ledger is skipped outright — none of its entries could be trusted,
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
