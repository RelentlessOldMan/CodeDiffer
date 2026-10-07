using System.Text;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Sessions;

namespace CodeDiffer.Core.ThreeWay;

/// <summary>What <see cref="MergeOverlay.Write"/> wrote. <paramref name="Failed"/>: files it could not write (I/O errors,
/// or a tree changed since the compare), counted in <paramref name="Unresolved"/> too.</summary>
public sealed record OverlayResult(string Dir, int FromV2, int Merged, int Markers, int Moved, int Deletes, int Unresolved, long Bytes,
    int Failed = 0, int DroppedDirectories = 0);

/// <summary>
/// Writes a compare3 merge as an OVERLAY on v1: only what turns v1 into the merged tree, never the whole tree (death
/// is 94 GB; its merge touches a few thousand files). The directory holds
/// <list type="bullet">
/// <item><c>files\</c> — each file the merge changes in v1, at its merged path: v2's one-sided changes, clean merges,
///   and text conflicts written with diff3 markers (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; v1</c> … <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; v2</c>).</item>
/// <item><c>deletes.txt</c> — v1 paths to delete (removed by v2, or moved by a rename), one per line.</item>
/// <item><c>conflicts.txt</c> — the conflicts: those in <c>files\</c> with markers, and those that can't be written
///   as one file (binary, large, modify/delete, rename/rename, path collision, encoding, line endings) with each side's file to choose from.</item>
/// <item><c>OVERLAY.txt</c> — what this is and how to apply it.</item>
/// </list>
/// Applying it: delete the paths in <c>deletes.txt</c> from v1, remove the directories that left empty, then copy
/// <c>files\</c> over v1 (deletes first, so a file v2 turned into a directory, a directory v2 turned into a file, or a
/// case-only rename lands right). Paths v1 alone changed need nothing. The
/// unresolved conflicts keep v1's version until someone decides. While it is being written the directory holds
/// <c>INCOMPLETE.txt</c>; one still there means the overlay was not finished and must not be applied.
/// </summary>
public static class MergeOverlay
{
    private enum Kind { FromV2, Merged, Markers, Moved }

    private const string Incomplete = "INCOMPLETE.txt";

    /// <summary>Refuse a directory inside any of the three trees, or one that isn't new or empty (checked before a
    /// long compare too, so a bad directory fails at once).</summary>
    public static void CheckDir(string dir, string baseDir, string v1, string v2)
    {
        dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        foreach (var (name, root) in new[] { ("base", baseDir), ("v1", v1), ("v2", v2) })
            if (ResultStore.IsUnder(dir, root))
                throw new ArgumentException($"the overlay directory {dir} is inside the {name} tree {root}; write it somewhere else");
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new ArgumentException($"{dir} is not empty; the overlay needs a new or empty directory");
    }

    /// <param name="dir">A new or empty directory, not inside any of the three trees.</param>
    public static OverlayResult Write(ThreeWayReport r, string baseDir, string v1, string v2, string dir, int parallelism = 8)
    {
        dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        CheckDir(dir, baseDir, v1, v2);
        // A directory that couldn't be listed makes every file under it look deleted (or added) by that side: the
        // overlay would delete v1's real files. Refused, never written.
        if (r.DroppedDirectories > 0)
            throw new ArgumentException($"the compare is incomplete ({r.DroppedDirectories} director(ies) could not be listed): files under them " +
                                        "would look deleted — refusing to write an overlay from it; compare again");
        Directory.CreateDirectory(dir);
        // Claim the directory: a second writer racing past the empty check fails here instead of mixing two overlays.
        try
        {
            using var claim = new FileStream(Path.Combine(dir, Incomplete), FileMode.CreateNew, FileAccess.Write);
            claim.Write(Encoding.UTF8.GetBytes("This overlay is being written (or its writer stopped part way). Do not apply it.\n"));
        }
        catch (IOException)
        {
            throw new ArgumentException($"{dir} is not empty; the overlay needs a new or empty directory");
        }
        var files = Path.Combine(dir, "files");
        Directory.CreateDirectory(files);
        var filesRoot = files + Path.DirectorySeparatorChar;

        // Decide each entry first (cheap), then write the files concurrently (reads over SMB must overlap).
        var writes = new List<(string Dest, Kind Kind, Merge3Entry E, string? AlsoDelete)>(); // AlsoDelete: v1's old path, once moved
        var deletes = new SortedSet<string>(StringComparer.Ordinal);
        var unresolved = new List<(Merge3Entry E, string Why)>();
        foreach (var e in r.Entries)
        {
            string? inV1 = e.V1 is null ? e.Path : e.V1.Status == ChangeStatus.Removed ? null : e.V1.RelativePath; // where v1 has it
            switch (e.Outcome)
            {
                case Merge3Outcome.V1Only:
                    break; // v1 already is the merge
                case Merge3Outcome.V2Only:
                    if (e.V2!.Status == ChangeStatus.Removed) { deletes.Add(e.Path); break; }
                    writes.Add((e.V2.RelativePath, Kind.FromV2, e, e.V2.Status == ChangeStatus.Renamed ? e.Path : null));
                    break;
                case Merge3Outcome.Agreed:
                    // Same bytes on both sides; only a rename on v2's side alone moves it.
                    if (e.MergedPath is { } to && inV1 is not null && to != inV1)
                    {
                        writes.Add((to, Kind.Moved, e, inV1));
                    }
                    break;
                case Merge3Outcome.Merged:
                    writes.Add((e.MergedPath!, Kind.Merged, e, inV1 != e.MergedPath ? inV1 : null));
                    break;
                default:
                    if (e.ConflictKind is "content" or "add/add" && e.MergedPath is not null)
                    {
                        writes.Add((e.MergedPath, Kind.Markers, e, inV1 != e.MergedPath ? inV1 : null));
                    }
                    else unresolved.Add((e, e.ConflictKind ?? "conflict"));
                    break;
            }
        }

        long bytes = 0;
        var failed = new System.Collections.Concurrent.ConcurrentBag<(Merge3Entry E, string Why)>();
        var written = new System.Collections.Concurrent.ConcurrentBag<(string Dest, Kind Kind, Merge3Entry E, string? AlsoDelete)>();
        Parallel.ForEach(writes, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) }, w =>
        {
            var target = Path.GetFullPath(Full(files, w.Dest));
            var temp = target + ".codediffer-tmp";
            try
            {
                if (!target.StartsWith(filesRoot, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"{w.Dest} is not a path inside the tree");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Written beside its name and moved into place, so a failure never leaves a truncated file in files\.
                switch (w.Kind)
                {
                    case Kind.FromV2:
                        File.Copy(Full(v2, w.Dest), temp, overwrite: true);
                        break;
                    case Kind.Moved:
                        File.Copy(Full(v1, w.E.V1?.RelativePath ?? w.E.Path), temp, overwrite: true);
                        break;
                    default:
                        // Re-merged from the trees as they are now: it must still be what the compare found.
                        var merged = TreeMerger.MergedBytes(w.E, baseDir, v1, v2, out int now)
                                     ?? throw new IOException("a side is no longer readable text (changed since the compare? compare again)");
                        int expected = w.Kind == Kind.Merged ? 0 : w.E.ConflictRegions;
                        if (now != expected)
                            throw new IOException($"changed since the compare: it now merges with {now} conflict region(s), not {expected}; compare again");
                        File.WriteAllBytes(temp, merged);
                        break;
                }
                File.Move(temp, target);
                Interlocked.Add(ref bytes, new FileInfo(target).Length);
                written.Add(w);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try { File.Delete(temp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
                failed.Add((w.E, $"not written: {ex.Message}"));
            }
        });
        unresolved.AddRange(failed);
        unresolved.Sort((a, b) => string.CompareOrdinal(a.E.Path, b.E.Path));
        var done = written.OrderBy(w => w.Dest, StringComparer.Ordinal).ToList();
        // A move's old path goes only once its new file is written (a failed write must not lose v1's copy). A path
        // the overlay writes needn't be deleted too (deletes run first, so a case-only rename still lands right).
        foreach (var w in done) if (w.AlsoDelete is { } old) deletes.Add(old);
        deletes.ExceptWith(done.Select(w => w.Dest));

        File.WriteAllText(Path.Combine(dir, "deletes.txt"), string.Concat(deletes.Select(d => d + "\n")), new UTF8Encoding(false));
        var c = new StringBuilder();
        var markers = done.Where(w => w.Kind == Kind.Markers).ToList();
        c.Append($"# {markers.Count} conflict(s) written to files\\ with diff3 markers (<<<<<<< v1 ... ||||||| base ... ======= ... >>>>>>> v2):\n");
        foreach (var w in markers)
            c.Append($"markers  {w.Dest}  ({w.E.ConflictRegions} region(s))").Append(w.E.Note is { } n ? $"  — {n}" : "").Append('\n');
        c.Append($"# {unresolved.Count - failed.Count} conflict(s) not written (v1's version stays until decided)" +
                 (failed.IsEmpty ? "" : $" and {failed.Count} file(s) that FAILED to write") + "; each side's file to choose from:\n");
        foreach (var (e, why) in unresolved)
        {
            c.Append($"{why}  {e.Path}").Append(e.Note is { } n ? $"  — {n}" : "").Append('\n');
            c.Append($"    v1: {Side(e.V1, v1, e.Path)}\n");
            c.Append($"    v2: {Side(e.V2, v2, e.Path)}\n");
        }
        File.WriteAllText(Path.Combine(dir, "conflicts.txt"), c.ToString(), new UTF8Encoding(false));

        var result = new OverlayResult(dir, done.Count(w => w.Kind == Kind.FromV2), done.Count(w => w.Kind == Kind.Merged),
            markers.Count, done.Count(w => w.Kind == Kind.Moved), deletes.Count, unresolved.Count, bytes, failed.Count, r.DroppedDirectories);
        File.WriteAllText(Path.Combine(dir, "OVERLAY.txt"), $"""
            CodeDiffer merge overlay — what turns v1 into the 3-way merge of
              base {baseDir}
              v1   {v1}
              v2   {v2}

            To apply, in this order:
              1. delete the v1 paths listed in deletes.txt (one per line, relative to v1, '/' separated, UTF-8);
              2. remove the directories the deletes left empty (needed where v2 put a file in a directory's place);
              3. copy files\ over v1.
            Or let CodeDiffer do all three (dry run first; safe to stop and run again):
              codediffer apply-overlay "{dir}" <v1 or a copy of it> --write
            Paths only v1 changed need nothing (v1 already has them).

              files\       {result.FromV2:N0} from v2 (only v2 changed them) · {result.Merged:N0} merged cleanly · {result.Markers:N0} with conflict markers · {result.Moved:N0} moved by v2's rename ({Bytes(bytes)})
              deletes.txt  {result.Deletes:N0} path(s)
              conflicts.txt {result.Markers:N0} with markers in files\ · {result.Unresolved - result.Failed:N0} not merged (binary, large, delete, rename, encoding or line-ending conflicts){(result.Failed > 0 ? $" · {result.Failed:N0} FAILED to write" : "")}: v1's version stays until decided
            {(result.DroppedDirectories > 0 ? $"\nINCOMPLETE COMPARE: {result.DroppedDirectories:N0} director(ies) could not be read, so changes under them are not in this overlay.\n" : "")}
            Written from the trees as they were when the overlay was written; if they changed after the compare, compare again first.
            """.Replace("\r\n", "\n"), new UTF8Encoding(false));
        File.Delete(Path.Combine(dir, Incomplete));
        return result;
    }

    /// <summary>One side of an unresolved conflict: its file, "(deleted)" when it removed it, or — when that side
    /// didn't touch the path — its unchanged file if it has one.</summary>
    private static string Side(FileChange? c, string root, string basePath)
    {
        if (c is null) return File.Exists(Full(root, basePath)) ? $"{Full(root, basePath)} (unchanged)" : "(not present)";
        return c.Status == ChangeStatus.Removed ? "(deleted)" : Full(root, c.RelativePath);
    }

    private static string Bytes(long b) => AgentViews.Bytes(b);

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
}
