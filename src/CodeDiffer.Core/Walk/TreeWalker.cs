using System.Collections.Concurrent;

namespace CodeDiffer.Core.Walk;

/// <summary>A completed walk: every file found, plus how many directories could not be listed.</summary>
public sealed record WalkResult(IReadOnlyList<FileEntry> Files, int DroppedDirectories);

/// <summary>
/// Walks a tree and yields every file as a <see cref="FileEntry"/>, reading size/mtime off the
/// directory enumeration (no separate per-file stat — the round-trip that dominated a naive SMB walk).
/// Directories whose name is in the ignore set are skipped whole.
///
/// Directories are listed with bounded concurrency, one frontier (depth level) at a time: over SMB each
/// listing is a round-trip, and overlapping them is what makes a 50k-file share walk fast (SMB2 credits
/// allow many in flight; locally it washes). A listing that fails is retried once; one that still fails
/// is counted in <see cref="WalkResult.DroppedDirectories"/> so the caller can disclose the gap — a
/// network hiccup must never silently read as "files missing".
/// </summary>
public sealed class TreeWalker
{
    private readonly IReadOnlySet<string> _ignoredDirs;
    private readonly int _parallelism;

    public TreeWalker(IEnumerable<string>? ignoredDirectoryNames = null, int parallelism = 1)
    {
        _ignoredDirs = new HashSet<string>(ignoredDirectoryNames ?? [".git"], StringComparer.OrdinalIgnoreCase);
        _parallelism = Math.Max(1, parallelism);
    }

    /// <summary>Walk and return just the files (drops the dropped-dir count; tests and small trees).</summary>
    public IEnumerable<FileEntry> Walk(string root) => WalkAll(root).Files;

    public WalkResult WalkAll(string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // don't follow junctions/symlinks (v1)
        };

        var files = new ConcurrentBag<FileEntry>();
        int dropped = 0;
        var frontier = new List<string> { rootFull };
        while (frontier.Count > 0)
        {
            var next = new ConcurrentBag<string>();
            Parallel.ForEach(frontier, new ParallelOptions { MaxDegreeOfParallelism = _parallelism }, dir =>
            {
                var entries = ListWithRetry(dir, opts, dir == rootFull);
                if (entries is null)
                {
                    Interlocked.Increment(ref dropped);
                    return;
                }
                foreach (var info in entries)
                {
                    switch (info)
                    {
                        case DirectoryInfo sub when !_ignoredDirs.Contains(sub.Name):
                            next.Add(sub.FullName);
                            break;
                        case FileInfo fi:
                            var rel = Path.GetRelativePath(rootFull, fi.FullName).Replace('\\', '/');
                            files.Add(new FileEntry(rel, fi.FullName, fi.Length, fi.LastWriteTimeUtc));
                            break;
                    }
                }
            });
            frontier = [.. next];
        }

        var list = files.ToList();
        list.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath)); // deterministic order
        return new WalkResult(list, dropped);
    }

    /// <summary>
    /// List one directory's entries, materialized so an IO error surfaces here. EnumerateFileSystemInfos
    /// pre-fills Length/mtime from the one listing. A failure is retried once after a short pause (a
    /// transient SMB error); null means the directory is genuinely unreadable and must be counted.
    /// </summary>
    private static FileSystemInfo[]? ListWithRetry(string dir, EnumerationOptions opts, bool isRoot)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0) Thread.Sleep(75);
            }
        }
        return isRoot ? throw new IOException($"cannot list tree root: {dir}") : null;
    }
}
