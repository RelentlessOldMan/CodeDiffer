using System.Collections.Concurrent;

namespace CodeDiffer.Core.Walk;

/// <summary>A completed walk: every file found, plus how many directories could not be listed.</summary>
public sealed record WalkResult(IReadOnlyList<FileEntry> Files, int DroppedDirectories);

/// <summary>
/// Walks a tree and yields every file as a <see cref="FileEntry"/>, reading size/mtime/ChangeTime/FileId
/// off the directory listing (no separate per-file stat — the round-trip that dominated a naive SMB walk).
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

    /// <param name="listed">Called after each directory is listed with the number of files in it (progress).</param>
    /// <param name="ct">Cancels the walk (throws <see cref="OperationCanceledException"/>).</param>
    public WalkResult WalkAll(string root, Action<int>? listed = null, CancellationToken ct = default)
    {
        var rootFull = RootOf(root);

        var files = new ConcurrentBag<FileEntry>();
        int dropped = 0;
        var frontier = new List<string> { rootFull };
        while (frontier.Count > 0)
        {
            var next = new ConcurrentBag<string>();
            Parallel.ForEach(frontier, new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = ct }, dir =>
            {
                var rows = ListWithRetry(dir, dir == rootFull);
                if (rows is null)
                {
                    Interlocked.Increment(ref dropped);
                    return;
                }
                int found = 0;
                foreach (var row in rows)
                {
                    if (row.IsReparsePoint) continue; // don't follow junctions/symlinks (v1)
                    var full = Path.Combine(dir, row.Name);
                    if (row.IsDirectory)
                    {
                        if (!_ignoredDirs.Contains(row.Name)) next.Add(full);
                        continue;
                    }
                    var rel = Path.GetRelativePath(rootFull, full).Replace('\\', '/');
                    files.Add(new FileEntry(rel, full, row.Length, new DateTime(row.LastWriteUtcTicks, DateTimeKind.Utc),
                        row.ChangeUtcTicks, row.FileId));
                    found++;
                }
                listed?.Invoke(found);
            });
            frontier = [.. next];
        }

        var list = files.ToList();
        list.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath)); // deterministic order
        return new WalkResult(list, dropped);
    }

    /// <summary>The full root path without a trailing separator — except a drive root keeps it: "Z:" alone means
    /// the current directory on Z:, not its root.</summary>
    internal static string RootOf(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>
    /// List one directory (size/mtime/ChangeTime/FileId straight off the listing). A failure is retried once
    /// after a short pause (a transient SMB error); null means the directory is genuinely unreadable and must
    /// be counted. The ROOT failing is fatal — there is no tree to compare.
    /// </summary>
    private static List<DirRow>? ListWithRetry(string dir, bool isRoot)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return DirectoryLister.List(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0) Thread.Sleep(75);
            }
        }
        return isRoot ? throw new IOException($"cannot list tree root: {dir}") : null;
    }
}
