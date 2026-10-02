namespace CodeDiffer.Core.Walk;

/// <summary>
/// One file found during a tree walk. <see cref="RelativePath"/> is repo-relative with forward
/// slashes (so a local tree and its SMB copy address identically). <see cref="Length"/> and
/// <see cref="LastWriteTimeUtc"/> are read off the directory enumeration itself (no extra per-file
/// stat round-trip — the CodeCompass SMB trick).
/// </summary>
public readonly record struct FileEntry(string RelativePath, string FullPath, long Length, DateTime LastWriteTimeUtc);
