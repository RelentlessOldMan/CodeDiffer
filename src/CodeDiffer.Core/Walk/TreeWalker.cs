using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace CodeDiffer.Core.Walk;

/// <summary>What a walk skipped on purpose: a symlink / junction (never followed: it can point outside the tree or back
/// into it), or a name Windows can't open by path (ending in '.' or ' ', or a device name like "nul" — the path API
/// would open another file, or the device).</summary>
public enum SkipKind { Link, Name }

/// <summary>A path the walk skipped, relative to the root ('/' separated), file or directory.</summary>
public readonly record struct SkippedPath(string Path, SkipKind Kind, bool IsDirectory);

/// <summary>A completed walk: every file found, how many directories could not be listed, and what was skipped.</summary>
public sealed record WalkResult(IReadOnlyList<FileEntry> Files, int DroppedDirectories, IReadOnlyList<SkippedPath>? Skipped = null)
{
    public IReadOnlyList<SkippedPath> SkippedPaths => Skipped ?? [];
    public int SkippedLinks => SkippedPaths.Count(s => s.Kind == SkipKind.Link);
}

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
///
/// A link is never followed — but a server that follows a symlink itself (Samba's default) shows it as a plain
/// directory. One that lists exactly what a directory above it lists (the same names, the same file ids) is that
/// directory reached again, a loop (<c>x -> ..</c>): skipped as a link, not listed again and again until the server
/// gives up.
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
        var skipped = new ConcurrentBag<SkippedPath>();
        int dropped = 0;
        string Rel(string full) => full[(rootFull.Length + (rootFull.EndsWith(Path.DirectorySeparatorChar) ? 0 : 1))..].Replace('\\', '/');
        var walkRoot = new WalkRoot(rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar);
        // Where a name holds a backslash (not on Windows), Rel's slashes don't give the full path back: keep it whole.
        bool exact = Path.DirectorySeparatorChar == '\\';
        var frontier = new List<(string Dir, Above? Above)> { (rootFull, null) };
        while (frontier.Count > 0)
        {
            var next = new ConcurrentBag<(string, Above?)>();
            try
            {
                Parallel.ForEach(frontier, new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = ct }, item =>
                {
                    var (dir, above) = item;
                    var rows = ListWithRetry(dir, dir == rootFull);
                    if (rows is null)
                    {
                        Interlocked.Increment(ref dropped);
                        return;
                    }
                    var sig = Signature(rows);
                    for (var a = above; sig is not null && a is not null; a = a.Up)
                        if (a.Signature == sig)
                        {
                            skipped.Add(new SkippedPath(Rel(dir), SkipKind.Link, true)); // a loop the server followed
                            return;
                        }
                    var mine = new Above(sig, above);
                    int found = 0;
                    foreach (var row in rows)
                    {
                        var full = dir + (dir.EndsWith(Path.DirectorySeparatorChar) ? "" : Path.DirectorySeparatorChar.ToString()) + row.Name;
                        if (row.IsLink) { skipped.Add(new SkippedPath(Rel(full), SkipKind.Link, row.IsDirectory)); continue; } // not followed
                        if (!UsableName(row.Name)) { skipped.Add(new SkippedPath(Rel(full), SkipKind.Name, row.IsDirectory)); continue; }
                        if (row.IsDirectory)
                        {
                            if (!_ignoredDirs.Contains(row.Name)) next.Add((full, mine));
                            continue;
                        }
                        var when = new DateTime(row.LastWriteUtcTicks, DateTimeKind.Utc);
                        files.Add(exact || !full.Contains('\\')
                            ? new FileEntry(Rel(full), walkRoot, row.Length, when, row.ChangeUtcTicks, row.FileId)
                            : new FileEntry(Rel(full), full, row.Length, when, row.ChangeUtcTicks, row.FileId));
                        found++;
                    }
                    listed?.Invoke(found);
                });
            }
            catch (AggregateException ae) when (ae.InnerExceptions.Count == 1)
            {
                ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw(); // the root's own error, not a wrapper
                throw;
            }
            frontier = [.. next];
        }

        // Arrays, exactly sized: a list grown by doubling kept up to 48 B a file of spare room for the whole compare.
        var list = files.ToArray();
        Array.Sort(list, (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath)); // deterministic order
        var skips = skipped.ToArray();
        Array.Sort(skips, (a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new WalkResult(list, dropped, skips);
    }

    /// <summary>The directories above one being listed, nearest first, by what each listed.</summary>
    private sealed record Above(UInt128? Signature, Above? Up);

    /// <summary>What a directory lists, as one number: its names and their file ids. Null when the server gives no file
    /// ids — names alone can't tell a loop from a real <c>a/a/a</c>.</summary>
    internal static UInt128? Signature(List<DirRow> rows)
    {
        if (!rows.Exists(r => r.FileId != 0)) return null;
        var h = new System.IO.Hashing.XxHash128();
        Span<byte> id = stackalloc byte[8];
        foreach (var r in rows.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            h.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(r.Name.AsSpan()));
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(id, r.FileId);
            h.Append(id);
        }
        return h.GetCurrentHashAsUInt128();
    }

    /// <summary>
    /// Can the file be opened by its plain path? On Windows the path API drops a trailing '.' or ' ' ("a." opens "a"),
    /// so such a name (legal on NTFS through \\?\, common on Samba shares written from Linux) would read another file
    /// or none — and "a" next to "a." would be one path twice. A legacy device name ("nul", and on older Windows
    /// "con", "com1.txt"…) opens the device, which can't be read as a file (or takes what is written to it). Both are
    /// skipped and said instead. Which names are devices is asked of the path API itself, so it is this Windows' list.
    /// </summary>
    internal static bool UsableName(string name)
        => !OperatingSystem.IsWindows() || name.Length == 0 ||
           (name[^1] != '.' && name[^1] != ' ' && !Path.GetFullPath(@"C:\d\" + name).StartsWith(@"\\.\", StringComparison.Ordinal));

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
