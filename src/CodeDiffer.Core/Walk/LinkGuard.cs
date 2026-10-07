using System.Collections.Concurrent;

namespace CodeDiffer.Core.Walk;

/// <summary>
/// Keeps writes inside a tree: finds a symlink or junction on the way from the tree's root to a path. The walk never
/// follows one, so a change under it was never compared there — and writing or deleting through it would land outside
/// the tree (a junction to another drive, a link back into another project). Only links count: other reparse points
/// (OneDrive / cloud placeholders, deduplicated files) are ordinary directories. Answers are cached per directory.
/// </summary>
internal sealed class LinkGuard(string root)
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    private readonly ConcurrentDictionary<string, bool> _isLink = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first link (as a full path) among <paramref name="fullPath"/>'s directories under the root — or the
    /// path itself when it is a link to a directory — else null. Directories that don't exist yet are no links.</summary>
    public string? LinkOnTheWay(string fullPath)
    {
        var dirs = new List<string>();
        for (var d = Path.GetDirectoryName(fullPath); d is not null && d.Length > _root.Length; d = Path.GetDirectoryName(d))
            dirs.Add(d);
        dirs.Reverse(); // from the root down: report the outermost link
        if (Directory.Exists(fullPath)) dirs.Add(fullPath);
        foreach (var d in dirs)
            if (_isLink.GetOrAdd(d, IsLink)) return d;
        return null;
    }

    private static bool IsLink(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
