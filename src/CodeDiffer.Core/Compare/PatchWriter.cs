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
}

/// <summary>Counts of what a patch run rendered, for the summary line.</summary>
public sealed class PatchStats
{
    public int TextFiles, BinaryFiles, GiantFiles, NoteFiles, CoarseFiles;

    internal void Add(PatchStats o)
    {
        TextFiles += o.TextFiles;
        BinaryFiles += o.BinaryFiles;
        GiantFiles += o.GiantFiles;
        NoteFiles += o.NoteFiles;
        CoarseFiles += o.CoarseFiles;
    }
}

/// <summary>
/// Turns a compare report into a git-style patch, one file at a time (bounded memory: only the current
/// file's text is held). Text changes become unified diffs that <c>git apply</c> accepts; binary files get
/// git's "Binary files … differ"; files over <see cref="PatchOptions.MaxTextBytes"/> get the content-defined
/// block diff (changed byte ranges); EOL-only and encoding-only changes get a one-line note unless Literal.
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
                WriteChange(sw, changed[start + i], leftRoot, rightRoot, opt, local[i]);
                sections[i] = sw.ToString();
            });
            for (int i = 0; i < n; i++)
            {
                w.Write(sections[i]);
                stats.Add(local[i]);
            }
        }
        return stats;
    }

    private static void WriteChange(TextWriter w, FileChange c, string leftRoot, string rightRoot, PatchOptions opt, PatchStats stats)
    {
        switch (c.Status)
        {
            case ChangeStatus.Added:
                WriteFile(w, c.RelativePath, c.RelativePath, null, Full(rightRoot, c.RelativePath), null, opt, stats);
                break;
            case ChangeStatus.Removed:
                WriteFile(w, c.RelativePath, c.RelativePath, Full(leftRoot, c.RelativePath), null, null, opt, stats);
                break;
            case ChangeStatus.Renamed:
                var header = $"similarity index {(c.SimilarityMilli ?? 0) / 10}%\nrename from {c.RenamedFrom}\nrename to {c.RelativePath}\n";
                WriteFile(w, c.RenamedFrom!, c.RelativePath, Full(leftRoot, c.RenamedFrom!), Full(rightRoot, c.RelativePath), header, opt, stats);
                break;
            case ChangeStatus.Modified:
                if (!opt.Literal && c.Reason is ChangeReason.Eol or ChangeReason.Encoding)
                {
                    w.Write($"diff --git a/{c.RelativePath} b/{c.RelativePath}\n");
                    w.Write(c.Reason == ChangeReason.Eol
                        ? $"Line endings differ: a/{c.RelativePath} ({Eol(Full(leftRoot, c.RelativePath))}) b/{c.RelativePath} ({Eol(Full(rightRoot, c.RelativePath))})\n"
                        : $"Encoding differs: a/{c.RelativePath} ({Enc(Full(leftRoot, c.RelativePath))}) b/{c.RelativePath} ({Enc(Full(rightRoot, c.RelativePath))})\n");
                    stats.NoteFiles++;
                    break;
                }
                WriteFile(w, c.RelativePath, c.RelativePath, Full(leftRoot, c.RelativePath), Full(rightRoot, c.RelativePath), null, opt, stats);
                break;
        }
    }

    /// <summary>Diff two individual files (either may be null = absent).</summary>
    public static PatchStats WriteFiles(TextWriter w, string? left, string? right, string displayLeft, string displayRight, PatchOptions? options = null)
    {
        var stats = new PatchStats();
        WriteFile(w, displayLeft, displayRight, left, right, null, options ?? new PatchOptions(), stats);
        return stats;
    }

    private static void WriteFile(TextWriter w, string oldRel, string newRel, string? oldFull, string? newFull,
        string? extraHeader, PatchOptions opt, PatchStats stats)
    {
        long oldLen = oldFull is null ? 0 : new FileInfo(oldFull).Length;
        long newLen = newFull is null ? 0 : new FileInfo(newFull).Length;

        bool giant = oldLen > opt.MaxTextBytes || newLen > opt.MaxTextBytes;
        // One open per side: small files are read whole once (sniff + decode from the same bytes).
        var oldBytes = oldFull is null ? null : giant ? TextInspector.ReadHead(oldFull) : File.ReadAllBytes(oldFull);
        var newBytes = newFull is null ? null : giant ? TextInspector.ReadHead(newFull) : File.ReadAllBytes(newFull);
        bool binary = (oldBytes is not null && TextInspector.LooksBinary(oldBytes.AsSpan(0, Math.Min(oldBytes.Length, TextInspector.HeadBytes)))) ||
                      (newBytes is not null && TextInspector.LooksBinary(newBytes.AsSpan(0, Math.Min(newBytes.Length, TextInspector.HeadBytes))));
        if (binary)
        {
            w.Write($"diff --git a/{oldRel} b/{newRel}\n");
            if (extraHeader is not null) w.Write(extraHeader);
            w.Write($"Binary files {(oldFull is null ? "/dev/null" : "a/" + oldRel)} and {(newFull is null ? "/dev/null" : "b/" + newRel)} differ\n");
            stats.BinaryFiles++;
            return;
        }

        if (giant)
        {
            WriteGiant(w, oldRel, newRel, oldFull, newFull, oldLen, newLen, extraHeader, opt);
            stats.GiantFiles++;
            return;
        }

        var oldText = oldBytes is null ? null : TextInspector.Decode(oldBytes);
        var newText = newBytes is null ? null : TextInspector.Decode(newBytes);
        if (UnifiedDiff.Write(w, oldRel, newRel, oldText, newText, opt.Context, extraHeader)) stats.CoarseFiles++;
        stats.TextFiles++;
    }

    private static void WriteGiant(TextWriter w, string oldRel, string newRel, string? oldFull, string? newFull,
        long oldLen, long newLen, string? extraHeader, PatchOptions opt)
    {
        w.Write($"diff --git a/{oldRel} b/{newRel}\n");
        if (extraHeader is not null) w.Write(extraHeader);
        if (oldFull is null || newFull is null)
        {
            w.Write($"Large file {(oldFull is null ? "added" : "removed")}: {(oldFull is null ? "b/" + newRel : "a/" + oldRel)} ({Math.Max(oldLen, newLen):N0} bytes)\n");
            return;
        }
        var r = GiantFileDiffer.Diff(oldFull, newFull);
        w.Write($"Large files a/{oldRel} and b/{newRel} differ: {r.Changes.Count} changed region(s), " +
                $"{r.ChangedOldBytes:N0} of {r.OldSize:N0} bytes ({100.0 * r.ChangedOldBytes / Math.Max(1, r.OldSize):F2}%) -> {r.NewSize:N0} bytes\n");
        foreach (var ch in r.Changes.Take(opt.MaxGiantRanges))
            w.Write($"  {CanonicalTokens.Token(ch.Op)} old[{ch.OldOffset:N0}, +{ch.OldLength:N0}) -> new[{ch.NewOffset:N0}, +{ch.NewLength:N0})\n");
        if (r.Changes.Count > opt.MaxGiantRanges)
            w.Write($"  ... {r.Changes.Count - opt.MaxGiantRanges} more region(s)\n");
    }

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
