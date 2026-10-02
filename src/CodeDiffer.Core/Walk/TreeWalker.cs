namespace CodeDiffer.Core.Walk;

/// <summary>
/// Walks a tree and yields every file as a <see cref="FileEntry"/>, reading size/mtime off the
/// directory enumeration (no separate per-file stat — the round-trip that dominated a naive SMB walk).
/// Directories whose name is in the ignore set are skipped whole. Inaccessible dirs/files are skipped,
/// not fatal, so a partial tree still walks (the completeness gap is the caller's to disclose).
///
/// v1 is a serial walk; bounded-concurrent directory reads (SMB round-trip overlap) are a later add.
/// </summary>
public sealed class TreeWalker
{
    private readonly IReadOnlySet<string> _ignoredDirs;

    public TreeWalker(IEnumerable<string>? ignoredDirectoryNames = null)
        => _ignoredDirs = new HashSet<string>(ignoredDirectoryNames ?? [".git"], StringComparer.OrdinalIgnoreCase);

    public IEnumerable<FileEntry> Walk(string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // don't follow junctions/symlinks (v1)
        };

        var stack = new Stack<string>();
        stack.Push(rootFull);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var info in SafeList(dir, opts))
            {
                switch (info)
                {
                    case DirectoryInfo sub when !_ignoredDirs.Contains(sub.Name):
                        stack.Push(sub.FullName);
                        break;
                    case FileInfo fi:
                        var rel = Path.GetRelativePath(rootFull, fi.FullName).Replace('\\', '/');
                        yield return new FileEntry(rel, fi.FullName, fi.Length, fi.LastWriteTimeUtc);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// List one directory's entries, materialized so an IO error surfaces here (where we can skip it)
    /// rather than mid-yield. EnumerateFileSystemInfos pre-fills Length/mtime from the one listing.
    /// </summary>
    private static FileSystemInfo[] SafeList(string dir, EnumerationOptions opts)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
