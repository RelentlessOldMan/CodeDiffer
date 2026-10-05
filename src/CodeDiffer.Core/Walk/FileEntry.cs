namespace CodeDiffer.Core.Walk;

/// <summary>
/// One file found during a tree walk. <see cref="RelativePath"/> is repo-relative with forward
/// slashes (so a local tree and its SMB copy address identically). Everything else is read off the
/// directory enumeration itself (no extra per-file stat round-trip — the CodeCompass SMB trick).
/// <see cref="ChangeTimeUtcTicks"/> (NTFS/SMB ChangeTime: moves on ANY content or metadata change, even
/// when a tool restores LastWriteTime) and <see cref="FileId"/> (changes when the file is replaced) are
/// what make a cached hash trustworthy; 0 means the platform/server didn't supply them (⇒ never trusted).
/// </summary>
public readonly record struct FileEntry(
    string RelativePath,
    string FullPath,
    long Length,
    DateTime LastWriteTimeUtc,
    long ChangeTimeUtcTicks = 0,
    long FileId = 0);
