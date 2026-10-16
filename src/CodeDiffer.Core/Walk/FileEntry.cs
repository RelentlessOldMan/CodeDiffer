namespace CodeDiffer.Core.Walk;

/// <summary>
/// One file found during a tree walk. <see cref="RelativePath"/> is repo-relative with forward
/// slashes (so a local tree and its SMB copy address identically). Everything else is read off the
/// directory enumeration itself (no extra per-file stat round-trip — the CodeCompass SMB trick).
/// <see cref="ChangeTimeUtcTicks"/> (NTFS/SMB ChangeTime: moves on ANY content or metadata change, even
/// when a tool restores LastWriteTime) and <see cref="FileId"/> (changes when the file is replaced) are
/// what make a cached hash trustworthy; 0 means the platform/server didn't supply them (⇒ never trusted).
/// </summary>
public readonly record struct FileEntry
{
    // The full path itself, or the walk's root: a walked file's full path is the root plus its relative path, so it
    // is built when asked for instead of kept, a second copy of every path, for the whole compare.
    private readonly object? _where;

    public FileEntry(string relativePath, string fullPath, long length, DateTime lastWriteTimeUtc,
        long changeTimeUtcTicks = 0, long fileId = 0)
        : this(relativePath, (object)fullPath, length, lastWriteTimeUtc, changeTimeUtcTicks, fileId) { }

    internal FileEntry(string relativePath, WalkRoot root, long length, DateTime lastWriteTimeUtc,
        long changeTimeUtcTicks, long fileId)
        : this(relativePath, (object)root, length, lastWriteTimeUtc, changeTimeUtcTicks, fileId) { }

    private FileEntry(string relativePath, object where, long length, DateTime lastWriteTimeUtc, long changeTimeUtcTicks, long fileId)
    {
        RelativePath = relativePath;
        _where = where;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc;
        ChangeTimeUtcTicks = changeTimeUtcTicks;
        FileId = fileId;
    }

    public string RelativePath { get; }
    /// <summary>The path to open. A walked file's is built on each call; callers that need it more than once keep it.</summary>
    public string FullPath => _where switch { string s => s, WalkRoot r => r.Join(RelativePath), _ => null! };
    public long Length { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
    public long ChangeTimeUtcTicks { get; init; }
    public long FileId { get; init; }
}

/// <summary>A walked tree's root, shared by all its entries: <see cref="Prefix"/> ends in a separator.</summary>
internal sealed class WalkRoot(string prefix)
{
    public string Prefix { get; } = prefix;

    /// <summary>The full path of <paramref name="rel"/> (forward slashes), as the walk spelled it.</summary>
    public string Join(string rel) => string.Create(Prefix.Length + rel.Length, (Prefix, rel), static (span, s) =>
    {
        s.Prefix.CopyTo(span);
        var tail = span[s.Prefix.Length..];
        s.rel.CopyTo(tail);
        if (Path.DirectorySeparatorChar != '/') tail.Replace('/', Path.DirectorySeparatorChar);
    });
}
