using System.Text;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Giant;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Compare;

public sealed class PatchOptions
{
    public const int DefaultContextLines = UnifiedDiff.DefaultContext;

    /// <summary>Lines of context around each change (git's default is 3).</summary>
    public int Context { get; init; } = DefaultContextLines;

    /// <summary>
    /// Above this size (either side) a file isn't line-diffed — decoding + line arrays would cost several times
    /// its size in RAM. It gets the giant-file block diff instead: changed byte ranges by reference.
    /// </summary>
    public long MaxTextBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Show EOL-only / encoding-only changes as full line diffs instead of a one-line note.</summary>
    public bool Literal { get; init; }

    /// <summary>Most byte ranges listed for one giant file.</summary>
    public int MaxGiantRanges { get; init; } = 20;

    /// <summary>Files rendered concurrently (output order is unchanged). Default min(cores, 8).</summary>
    public int Parallelism { get; init; } = Math.Min(Environment.ProcessorCount, 8);

    /// <summary>Stops a giant file's block diff mid-read (it reads both files whole).</summary>
    public CancellationToken Cancellation { get; init; }
}

/// <summary>Counts of what a patch run rendered, for the summary line.</summary>
public sealed class PatchStats
{
    /// <summary><see cref="OtherFiles"/>: text that is not UTF-8 (UTF-16, a legacy code page) — a UTF-8 patch can't
    /// carry its bytes. <see cref="UnreadableFiles"/>: a side could not be read.</summary>
    public int TextFiles, BinaryFiles, GiantFiles, NoteFiles, CoarseFiles, OtherFiles, UnreadableFiles;

    /// <summary>Files the patch describes in '#' comments but does not carry: copy them by hand.</summary>
    public int NotCarried => BinaryFiles + GiantFiles + OtherFiles + UnreadableFiles;

    internal void Add(PatchStats o)
    {
        TextFiles += o.TextFiles;
        BinaryFiles += o.BinaryFiles;
        GiantFiles += o.GiantFiles;
        NoteFiles += o.NoteFiles;
        CoarseFiles += o.CoarseFiles;
        OtherFiles += o.OtherFiles;
        UnreadableFiles += o.UnreadableFiles;
    }

    /// <summary>The one-line summary of a written patch.</summary>
    public string Summary(bool literal) =>
        $"{TextFiles:N0} text · {NoteFiles:N0} eol/encoding note(s)" + (CoarseFiles > 0 ? $" · {CoarseFiles:N0} coarse (edit-distance budget exceeded)" : "") +
        (NotCarried > 0 ? $" · NOT CARRIED (described in '#' lines; copy them by hand): {BinaryFiles:N0} binary · {GiantFiles:N0} large · " +
                          $"{OtherFiles:N0} non-UTF-8 text · {UnreadableFiles:N0} unreadable" : "") +
        (NoteFiles > 0 && !literal ? " · eol/encoding-only files are notes, not hunks (--literal / literal=true carries them)" : "");
}

/// <summary>
/// Turns a compare report into a git-style patch, one file at a time (bounded memory: only the current
/// file's text is held). Text changes become unified diffs that <c>git apply</c> accepts; files over
/// <see cref="PatchOptions.MaxTextBytes"/> get the content-defined block diff (changed byte ranges); EOL-only and
/// encoding-only changes get a one-line note unless Literal.
///
/// A whole patch (<see cref="Write"/>, <see cref="WriteFiles"/>) is for <c>git apply</c>: everything it can't carry —
/// binary files, large files, text that isn't UTF-8, files it couldn't read, the eol/encoding notes — is described
/// in '#' comment lines outside any <c>diff --git</c> section, which git skips, so the rest still applies (a
/// UTF-8 BOM is kept, so its first line matches). One file's section for a reader (<see cref="WriteChange"/>) shows
/// them in place instead: git's "Binary files … differ", legacy text decoded as Latin-1.
/// </summary>
public static class PatchWriter
{
    public static PatchStats Write(TextWriter w, CompareReport report, string leftRoot, string rightRoot, PatchOptions? options = null)
    {
        var opt = options ?? new PatchOptions();
        var stats = new PatchStats();
        var changed = report.Changes.Where(c => c.Status != ChangeStatus.Identical).ToList();

        // Render sections concurrently (each is a few opens + reads — over SMB they must overlap), but in
        // bounded batches written in report order: at most Batch file sections are held in memory at once.
        int batch = Math.Max(1, opt.Parallelism * 4);
        for (int start = 0; start < changed.Count; start += batch)
        {
            int n = Math.Min(batch, changed.Count - start);
            var sections = new string[n];
            var local = new PatchStats[n];
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = opt.Parallelism }, i =>
            {
                var sw = new StringWriter { NewLine = "\n" };
                local[i] = new PatchStats();
                var c = changed[start + i];
                try
                {
                    Section(sw, c, leftRoot, rightRoot, opt, local[i], forGit: true);
                    sections[i] = sw.ToString();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked, or gone since the compare: say so and keep going — one file must not cost the patch.
                    local[i] = new PatchStats { UnreadableFiles = 1 };
                    sections[i] = $"# {Name(c)}: could not be read ({OneLine(ex.Message)}) — not in this patch\n";
                }
            });
            for (int i = 0; i < n; i++)
            {
                w.Write(sections[i]);
                stats.Add(local[i]);
            }
        }
        return stats;
    }

    /// <summary>One change's section, for a reader (an agent's or the report's view of one file). A side that can't
    /// be read (gone or locked since the compare) is said in one line, never thrown.</summary>
    public static void WriteChange(TextWriter w, FileChange c, string leftRoot, string rightRoot, PatchOptions opt, PatchStats stats)
    {
        var sw = new StringWriter { NewLine = "\n" };
        var local = new PatchStats();
        try
        {
            Section(sw, c, leftRoot, rightRoot, opt, local, forGit: false);
            w.Write(sw.ToString());
            stats.Add(local);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            w.Write($"{Name(c)}: could not be read ({OneLine(ex.Message)})\n");
            stats.UnreadableFiles++;
        }
    }

    /// <param name="forGit">Part of a whole patch for <c>git apply</c>: what it can't carry goes in '#' lines.</param>
    private static void Section(TextWriter w, FileChange c, string leftRoot, string rightRoot, PatchOptions opt, PatchStats stats, bool forGit)
    {
        switch (c.Status)
        {
            case ChangeStatus.Added:
                WriteFile(w, c.RelativePath, c.RelativePath, null, Full(rightRoot, c.RelativePath), null, opt, stats, forGit);
                break;
            case ChangeStatus.Removed:
                WriteFile(w, c.RelativePath, c.RelativePath, Full(leftRoot, c.RelativePath), null, null, opt, stats, forGit);
                break;
            case ChangeStatus.Renamed:
                var header = $"similarity index {(c.SimilarityMilli ?? 0) / 10}%\nrename from {c.RenamedFrom}\nrename to {c.RelativePath}\n";
                if (c.PureRename)
                {
                    // Byte-identical: the header alone moves it — for binary and large files too, and nothing is read.
                    w.Write($"diff --git a/{c.RenamedFrom} b/{c.RelativePath}\n{header}");
                    stats.TextFiles++;
                    break;
                }
                WriteFile(w, c.RenamedFrom!, c.RelativePath, Full(leftRoot, c.RenamedFrom!), Full(rightRoot, c.RelativePath), header, opt, stats, forGit);
                break;
            case ChangeStatus.Modified:
                if (!opt.Literal && c.Reason is ChangeReason.Eol or ChangeReason.Encoding)
                {
                    var note = c.Reason == ChangeReason.Eol
                        ? $"Line endings differ: a/{c.RelativePath} ({Eol(Full(leftRoot, c.RelativePath))}) b/{c.RelativePath} ({Eol(Full(rightRoot, c.RelativePath))})\n"
                        : $"Encoding differs: a/{c.RelativePath} ({Enc(Full(leftRoot, c.RelativePath))}) b/{c.RelativePath} ({Enc(Full(rightRoot, c.RelativePath))})\n";
                    if (forGit) w.Write($"# {note.TrimEnd('\n')} — a note, not a hunk (--literal carries it)\n");
                    else w.Write($"diff --git a/{c.RelativePath} b/{c.RelativePath}\n{note}");
                    stats.NoteFiles++;
                    break;
                }
                WriteFile(w, c.RelativePath, c.RelativePath, Full(leftRoot, c.RelativePath), Full(rightRoot, c.RelativePath), null, opt, stats, forGit);
                break;
        }
    }

    /// <summary>Diff two individual files (either may be null = absent), as a patch for <c>git apply</c>.</summary>
    public static PatchStats WriteFiles(TextWriter w, string? left, string? right, string displayLeft, string displayRight, PatchOptions? options = null)
    {
        var stats = new PatchStats();
        WriteFile(w, displayLeft, displayRight, left, right, null, options ?? new PatchOptions(), stats, forGit: true);
        return stats;
    }

    private static void WriteFile(TextWriter w, string oldRel, string newRel, string? oldFull, string? newFull,
        string? extraHeader, PatchOptions opt, PatchStats stats, bool forGit)
    {
        long oldLen = oldFull is null ? 0 : new FileInfo(oldFull).Length;
        long newLen = newFull is null ? 0 : new FileInfo(newFull).Length;
        string what = oldRel == newRel ? oldRel : $"{oldRel} -> {newRel}";

        bool giant = oldLen > opt.MaxTextBytes || newLen > opt.MaxTextBytes;
        // One open per side: small files are read whole once (sniff + decode from the same bytes).
        var oldBytes = oldFull is null ? null : giant ? TextInspector.ReadHead(oldFull) : TextInspector.ReadAll(oldFull);
        var newBytes = newFull is null ? null : giant ? TextInspector.ReadHead(newFull) : TextInspector.ReadAll(newFull);
        bool binary = (oldBytes is not null && TextInspector.LooksBinary(oldBytes.AsSpan(0, Math.Min(oldBytes.Length, TextInspector.HeadBytes)))) ||
                      (newBytes is not null && TextInspector.LooksBinary(newBytes.AsSpan(0, Math.Min(newBytes.Length, TextInspector.HeadBytes))));
        if (binary)
        {
            var line = $"Binary files {(oldFull is null ? "/dev/null" : "a/" + oldRel)} and {(newFull is null ? "/dev/null" : "b/" + newRel)} differ";
            if (forGit) w.Write($"# {line} — not in this patch ({Side(oldRel, newRel, oldFull, newFull)})\n");
            else w.Write($"diff --git a/{oldRel} b/{newRel}\n{extraHeader}{line}\n");
            stats.BinaryFiles++;
            return;
        }

        if (giant)
        {
            var g = new StringWriter { NewLine = "\n" };
            WriteGiant(g, oldRel, newRel, oldFull, newFull, oldLen, newLen, opt);
            if (forGit) w.Write($"# {what}: large file, not in this patch ({Side(oldRel, newRel, oldFull, newFull)}); its changed byte ranges:\n" +
                                string.Concat(g.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => $"#   {l}\n")));
            else w.Write($"diff --git a/{oldRel} b/{newRel}\n{extraHeader}{g}");
            stats.GiantFiles++;
            return;
        }

        string? oldText, newText;
        if (forGit)
        {
            // git compares bytes: a UTF-8 patch carries a file exactly only if the file is UTF-8 (a BOM kept as U+FEFF
            // at the start of line 1). Anything else would apply wrong or not at all.
            if (!Utf8(oldBytes, out oldText) || !Utf8(newBytes, out newText))
            {
                w.Write($"# {what}: text that is not UTF-8 (UTF-16 or a legacy code page) — not in this patch ({Side(oldRel, newRel, oldFull, newFull)})\n");
                stats.OtherFiles++;
                return;
            }
        }
        else
        {
            oldText = oldBytes is null ? null : TextInspector.Decode(oldBytes);
            newText = newBytes is null ? null : TextInspector.Decode(newBytes);
        }
        if (UnifiedDiff.Write(w, oldRel, newRel, oldText, newText, opt.Context, extraHeader)) stats.CoarseFiles++;
        stats.TextFiles++;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>The bytes as UTF-8 text, BOM included (null bytes = an absent side). False when they aren't UTF-8.</summary>
    private static bool Utf8(byte[]? bytes, out string? text)
    {
        text = null;
        if (bytes is null) return true;
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0x00, 0x00, 0xFE, 0xFF, ..]) return false; // UTF-16/32 BOM
        try { text = StrictUtf8.GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private static string Side(string oldRel, string newRel, string? oldFull, string? newFull)
        => newFull is null ? $"delete a/{oldRel} by hand" : oldFull is null ? $"copy b/{newRel} by hand"
            : oldRel != newRel ? $"copy b/{newRel} by hand and delete a/{oldRel}" : $"copy b/{newRel} by hand";

    private static void WriteGiant(TextWriter w, string oldRel, string newRel, string? oldFull, string? newFull,
        long oldLen, long newLen, PatchOptions opt)
    {
        if (oldFull is null || newFull is null)
        {
            w.Write($"Large file {(oldFull is null ? "added" : "removed")}: {(oldFull is null ? "b/" + newRel : "a/" + oldRel)} ({Math.Max(oldLen, newLen):N0} bytes)\n");
            return;
        }
        var r = GiantFileDiffer.Diff(oldFull, newFull, ct: opt.Cancellation);
        w.Write($"Large files a/{oldRel} and b/{newRel} differ: {r.Changes.Count} changed region(s), " +
                $"{r.ChangedOldBytes:N0} of {r.OldSize:N0} bytes ({100.0 * r.ChangedOldBytes / Math.Max(1, r.OldSize):F2}%) -> {r.NewSize:N0} bytes\n");
        foreach (var ch in r.Changes.Take(opt.MaxGiantRanges))
            w.Write($"  {CanonicalTokens.Token(ch.Op)} old[{ch.OldOffset:N0}, +{ch.OldLength:N0}) -> new[{ch.NewOffset:N0}, +{ch.NewLength:N0})\n");
        if (r.Changes.Count > opt.MaxGiantRanges)
            w.Write($"  ... {r.Changes.Count - opt.MaxGiantRanges} more region(s)\n");
    }

    private static string Name(FileChange c) => c.RenamedFrom is { } f ? $"{f} -> {c.RelativePath}" : c.RelativePath;

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string Eol(string path)
    {
        var head = TextInspector.ReadHead(path, 64 * 1024);
        int crlf = 0, lf = 0;
        for (int i = 0; i < head.Length; i++)
            if (head[i] == '\n') { if (i > 0 && head[i - 1] == '\r') crlf++; else lf++; }
        return crlf > 0 && lf > 0 ? "mixed" : crlf > 0 ? "CRLF" : lf > 0 ? "LF" : "no newlines";
    }

    private static string Enc(string path)
    {
        var h = TextInspector.ReadHead(path, 4);
        return h switch
        {
            [0xEF, 0xBB, 0xBF, ..] => "UTF-8 BOM",
            [0xFF, 0xFE, 0x00, 0x00] => "UTF-32 LE",
            [0x00, 0x00, 0xFE, 0xFF] => "UTF-32 BE",
            [0xFF, 0xFE, ..] => "UTF-16 LE",
            [0xFE, 0xFF, ..] => "UTF-16 BE",
            _ => "UTF-8/ASCII, no BOM",
        };
    }
}
