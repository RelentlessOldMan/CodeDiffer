using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Port;
using CodeDiffer.Core.Sessions;

namespace CodeDiffer.Core.ThreeWay;

/// <summary>What <see cref="OverlayApplier.Run"/> did (or, on a dry run, would do).</summary>
public sealed record OverlayApplyResult(string Overlay, string Target, string? MadeForV1, bool Written, bool Cancelled,
    int Deleted, int AlreadyGone, int Copied, int AlreadyThere, int RemovedDirectories, int NotReached, long Bytes,
    IReadOnlyList<string> Deletes, IReadOnlyList<(string Path, string Why)> Failed);

/// <summary>
/// Applies a merge overlay (<see cref="MergeOverlay"/>) to a tree — v1, or better a copy of it — the documented way:
/// delete the paths in <c>deletes.txt</c>, then remove the directories those deletes left empty (so a file v2 put
/// where a directory was can land), then copy <c>files\</c> over it. Dry run unless <c>write</c>.
/// <para>
/// Like <see cref="ChangePorter"/> it is safe to stop: a cancel stops between files, each file is written whole
/// (via <c>&lt;file&gt;.codediffer.tmp</c> and a rename), and running it again finishes the job — a path already
/// deleted, or already holding the overlay's bytes, comes out "already". While it writes, the overlay holds
/// <c>APPLYING.txt</c>; when it finishes with nothing failed, <c>APPLIED.txt</c> (target and time). Applying the same overlay to the
/// same target again is refused unless <c>again</c>, since it would overwrite conflicts resolved since.
/// </para>
/// It refuses, before touching anything, an overlay still being written (<c>INCOMPLETE.txt</c>), a target inside
/// the overlay (or the reverse), and a <c>deletes.txt</c> line that is not a plain path inside the target.
/// It deletes files only, never a directory that is in a deletes.txt path's place.
/// </summary>
public static class OverlayApplier
{
    public const string AppliedName = "APPLIED.txt", ApplyingName = "APPLYING.txt";

    public static OverlayApplyResult Run(string overlay, string target, bool write, bool again = false, int parallelism = 8,
        CancellationToken ct = default, PortProgress? progress = null)
    {
        overlay = Path.TrimEndingDirectorySeparator(Path.GetFullPath(overlay));
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var files = Path.Combine(overlay, "files");
        var deletesFile = Path.Combine(overlay, "deletes.txt");
        if (!File.Exists(Path.Combine(overlay, "OVERLAY.txt")) || !File.Exists(deletesFile) || !Directory.Exists(files))
            throw new ArgumentException($"{overlay} is not a merge overlay (no OVERLAY.txt, deletes.txt and files\\)");
        if (File.Exists(Path.Combine(overlay, "INCOMPLETE.txt")))
            throw new ArgumentException($"{overlay} was not finished (INCOMPLETE.txt): write the overlay again, don't apply this one");
        // Written from a compare that couldn't list a directory (CodeDiffer 1.0.36 and before still wrote one): its
        // deletes may be listing failures.
        if (File.ReadAllText(Path.Combine(overlay, "OVERLAY.txt")).Contains("INCOMPLETE COMPARE", StringComparison.Ordinal))
            throw new ArgumentException($"{overlay} was written from an incomplete compare (see OVERLAY.txt): its deletes may be listing " +
                                        "failures — compare again and write a new overlay");
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException($"target directory not found: {target}");
        if (ResultStore.IsUnder(target, overlay) || ResultStore.IsUnder(overlay, target))
            throw new ArgumentException($"the target {target} and the overlay {overlay} must not be inside each other");
        var madeFor = MadeForV1(overlay);
        if (write && !again && File.Exists(Path.Combine(overlay, AppliedName))
            && File.ReadAllText(Path.Combine(overlay, AppliedName)).Contains(target + "\n", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"this overlay was already applied to {target} (see {AppliedName}); applying it again would overwrite " +
                                        "any conflict resolved since — pass again to do it anyway");

        // Every delete is checked before anything is touched: a bad line refuses the whole run.
        var deletes = new List<string>();
        foreach (var line in File.ReadAllLines(deletesFile, Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            var full = Path.GetFullPath(Path.Combine(target, line.Replace('/', Path.DirectorySeparatorChar)));
            if (Path.IsPathRooted(line) || line.Split('/', '\\').Any(p => p is "" or "." or "..") || !ResultStore.IsUnder(full, target) || full == target)
                throw new ArgumentException($"deletes.txt has a line that is not a plain relative path: \"{line}\"");
            deletes.Add(line);
        }
        var copies = Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(files, f)).Order(StringComparer.Ordinal).ToList();
        if (progress is not null) progress.Total = deletes.Count + copies.Count;

        var applying = Path.Combine(overlay, ApplyingName);
        if (write) File.WriteAllText(applying, $"being applied to\n{target}\n(if this stays, the apply was stopped or something failed: run it again to finish)\n");

        var failed = new System.Collections.Concurrent.ConcurrentBag<(string, string)>();
        int deleted = 0, gone = 0, copied = 0, there = 0, notReached = 0;
        long bytes = 0;
        var emptied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<string>(); // the deletes that find a file
        void Step()
        {
            if (progress is null) return;
            progress.Step();
            progress.AfterFile?.Invoke(progress.Done);
        }

        // A case-only rename (Same.c -> same.c) deletes the old name and writes the new one. On a case-insensitive
        // target the deleted name still "exists" once the new one is there: it is already done only when the file's
        // name on disk is the new spelling, else a re-run would delete the merged file every time.
        var copyByCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in copies) copyByCase.TryAdd(c.Replace('\\', '/'), c); // two names differing in case only: the first
        bool RenamedAlready(string d, string p) =>
            copyByCase.TryGetValue(d, out var to) && !string.Equals(to.Replace('\\', '/'), d, StringComparison.Ordinal)
            && NameOnDisk(p) == Path.GetFileName(to);

        var links = new CodeDiffer.Core.Walk.LinkGuard(target);
        string? Link(string full) => links.LinkOnTheWay(full) is { } l ? $"the target has a symlink/junction at {l} — never written or deleted through" : null;

        // 1. Deletes first, so a file v2 turned into a directory, or a case-only rename, lands right.
        foreach (var d in deletes)
        {
            if (ct.IsCancellationRequested) { notReached++; continue; }
            var p = Full(target, d);
            try
            {
                if (Link(p) is { } why) { failed.Add((d, why)); Step(); continue; }
                // A directory where files\ has one too: v2 turned the file into a directory, and a run already did it.
                if (Directory.Exists(p) && Directory.Exists(Full(files, d))) gone++;
                else if (Directory.Exists(p)) failed.Add((d, "to delete, but the target has a directory there; left alone"));
                else if (!File.Exists(p) || RenamedAlready(d, p))
                {
                    gone++;
                    if (write) emptied.Add(Path.GetDirectoryName(p)!); // a stopped run may have deleted it and not the directory
                }
                else
                {
                    if (write)
                    {
                        File.SetAttributes(p, File.GetAttributes(p) & ~FileAttributes.ReadOnly);
                        File.Delete(p);
                        emptied.Add(Path.GetDirectoryName(p)!);
                    }
                    deleted++;
                    hits.Add(d);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add((d, $"not deleted: {ex.Message}")); }
            Step();
        }

        // 2. Remove the directories the deletes left empty (walking up, never the target itself) — before the copies,
        //    so a file v2 put where a directory was lands in its place.
        int removedDirs = 0;
        if (write && !ct.IsCancellationRequested)
            foreach (var start in emptied.OrderByDescending(d => d.Length))
                for (var dir = start; dir.Length > target.Length && ResultStore.IsUnder(dir, target); dir = Path.GetDirectoryName(dir)!)
                {
                    try
                    {
                        if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
                        Directory.Delete(dir);
                        removedDirs++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
                }

        // 3. Copy files\ over the target (concurrently: over SMB the reads and writes must overlap).
        var hitSet = hits.Select(h => Full(target, h)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(copies, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) }, rel =>
        {
            if (ct.IsCancellationRequested) { Interlocked.Increment(ref notReached); return; }
            var src = Path.Combine(files, rel);
            var dest = Path.Combine(target, rel);
            var tmp = dest + ChangePorter.TempSuffix;
            try
            {
                if (Link(dest) is { } why) throw new IOException(why);
                // A directory still there is one the deletes did not empty (on a dry run: one they would not empty).
                if (Directory.Exists(dest) && (write || !Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).All(hitSet.Contains)))
                    throw new IOException("the target has a directory there that the deletes do not empty");
                long len = new FileInfo(src).Length;
                if (File.Exists(dest) && new FileInfo(dest).Length == len && TextInspector.SameBytes(dest, src))
                    Interlocked.Increment(ref there);
                else
                {
                    if (write)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(src, tmp, overwrite: true);
                        if (File.Exists(dest)) File.SetAttributes(dest, File.GetAttributes(dest) & ~FileAttributes.ReadOnly);
                        File.Move(tmp, dest, overwrite: true);
                    }
                    Interlocked.Increment(ref copied);
                    Interlocked.Add(ref bytes, len);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
                failed.Add((rel.Replace('\\', '/'), $"not written: {ex.Message}"));
            }
            Step();
        });

        bool cancelled = notReached > 0;
        var fails = failed.OrderBy(f => f.Item1, StringComparer.Ordinal).ToList();
        // Only a run that did everything is "applied": after a failure APPLYING.txt stays, so running it again (once
        // the cause is fixed) is not refused — what already landed comes out "already".
        if (write && !cancelled && fails.Count == 0)
        {
            File.AppendAllText(Path.Combine(overlay, AppliedName),
                $"applied {DateTime.Now:yyyy-MM-dd HH:mm:ss} to\n{target}\n  {deleted:N0} deleted · {copied:N0} written\n");
            File.Delete(applying);
        }
        return new OverlayApplyResult(overlay, target, madeFor, write, cancelled, deleted, gone, copied, there, removedDirs, notReached, bytes,
            hits, fails);
    }

    /// <summary>The v1 the overlay was written for (its OVERLAY.txt "v1" line), or null.</summary>
    private static string? MadeForV1(string overlay)
    {
        foreach (var line in File.ReadLines(Path.Combine(overlay, "OVERLAY.txt")).Take(10))
            if (line.TrimStart().StartsWith("v1 ", StringComparison.Ordinal)) return line.TrimStart()[3..].Trim();
        return null;
    }

    /// <summary>The bounded report (the CLI prints it).</summary>
    public static string Text(OverlayApplyResult r, int maxList = 20)
    {
        var o = new StringBuilder();
        o.Append($"{(r.Cancelled ? "CANCELLED " : "")}{(r.Written ? r.Cancelled ? "part applied" : "APPLIED" : "dry run")} · overlay {r.Overlay} onto {r.Target}\n");
        if (r.MadeForV1 is { } v1 && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(v1)), r.Target, StringComparison.OrdinalIgnoreCase))
            o.Append($"  (the overlay was written for v1 {v1}; this target is another tree — fine if it is a copy of that v1)\n");
        string w = r.Written ? "" : "would be ";
        o.Append($"deletes: {r.Deleted:N0} {w}deleted · {r.AlreadyGone:N0} already gone\n");
        o.Append($"files:   {r.Copied:N0} {w}written ({AgentViews.Bytes(r.Bytes)}) · {r.AlreadyThere:N0} already there\n");
        if (r.Written && r.RemovedDirectories > 0) o.Append($"removed {r.RemovedDirectories:N0} director(ies) the deletes left empty\n");
        if (r.Cancelled) o.Append($"{r.NotReached:N0} not reached" +
                                  (r.Written ? " — every file written is whole; run the same apply-overlay again to finish\n" : "\n"));
        if (r.Failed.Count > 0)
        {
            o.Append($"FAILED: {r.Failed.Count:N0}\n");
            foreach (var (p, why) in r.Failed.Take(maxList)) o.Append($"  {p} — {why}\n");
            if (r.Failed.Count > maxList) o.Append($"  ... {r.Failed.Count - maxList:N0} more\n");
            if (r.Written) o.Append("not marked applied: fix what failed and run the same apply-overlay again to finish\n");
        }
        if (!r.Written && r.Deleted > 0)
        {
            o.Append("to delete:\n");
            foreach (var d in r.Deletes.Take(maxList)) o.Append($"  {d}\n");
            if (r.Deletes.Count > maxList) o.Append($"  ... {r.Deletes.Count - maxList:N0} more (deletes.txt)\n");
        }
        o.Append(r.Written ? "conflicts not merged keep the target's version: see conflicts.txt in the overlay\n"
                           : "nothing was changed — run again with --write to apply it\n");
        return o.ToString();
    }

    /// <summary>The file's name as the directory spells it (a case-insensitive file system answers any spelling).</summary>
    private static string? NameOnDisk(string path)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
        var name = Path.GetFileName(path);
        var names = dir.EnumerateFiles().Select(f => f.Name).Where(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return names.Contains(name, StringComparer.Ordinal) ? name : names.FirstOrDefault(); // case-sensitive: both may exist
    }

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
}
