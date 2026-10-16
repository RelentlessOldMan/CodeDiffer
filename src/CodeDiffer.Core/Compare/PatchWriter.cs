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
    public long MaxTextBytes { get; init; } = TextInspector.WholeSniffBytes;

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
    /// carry its bytes. <see cref="UnreadableFiles"/>: a side could not be read, or is behind a link.
    /// <see cref="CaseFiles"/>: a path another change's path differs from only in case. <see cref="NameFiles"/>: a
    /// path with an unpaired UTF-16 surrogate, which UTF-8 can't spell (git would look for another name).</summary>
    public int TextFiles, BinaryFiles, GiantFiles, NoteFiles, CoarseFiles, OtherFiles, UnreadableFiles, CaseFiles, NameFiles;

    /// <summary>Written with no context lines: plain <c>git apply</c> refuses such hunks (it wants
    /// <c>--unidiff-zero</c>, which has no context to check a hunk's place by: it can apply one at the wrong place,
    /// unchecked).</summary>
    public bool ZeroContext;

    /// <summary>What a zero-context patch needs said wherever it is written.</summary>
    public const string ZeroContextNote = "no context lines: `git apply` needs `--unidiff-zero`, which has no context to check a hunk's " +
                                          "place by and can apply it at the wrong place without a word; for an exact apply, use context 1 or more, " +
                                          "or `codediffer apply`";

    /// <summary>Files the patch describes in '#' comments but does not carry: copy them by hand.</summary>
    public int NotCarried => BinaryFiles + GiantFiles + OtherFiles + UnreadableFiles + CaseFiles + NameFiles;

    internal void Add(PatchStats o)
    {
        TextFiles += o.TextFiles;
        BinaryFiles += o.BinaryFiles;
        GiantFiles += o.GiantFiles;
        NoteFiles += o.NoteFiles;
        CoarseFiles += o.CoarseFiles;
        OtherFiles += o.OtherFiles;
        UnreadableFiles += o.UnreadableFiles;
        CaseFiles += o.CaseFiles;
        NameFiles += o.NameFiles;
    }

    /// <summary>The one-line summary of a written patch.</summary>
    public string Summary(bool literal) =>
        $"{TextFiles:N0} text · {NoteFiles:N0} eol/encoding note(s)" + (CoarseFiles > 0 ? $" · {CoarseFiles:N0} coarse (too many changes to align exactly: wider than the change)" : "") +
        (NotCarried > 0 ? $" · NOT CARRIED (described in '#' lines; copy them by hand): {BinaryFiles:N0} binary · {GiantFiles:N0} large · " +
                          $"{OtherFiles:N0} non-UTF-8 text · {UnreadableFiles:N0} unreadable" +
                          (CaseFiles > 0 ? $" · {CaseFiles:N0} case-only path change(s)" : "") +
                          (NameFiles > 0 ? $" · {NameFiles:N0} path(s) UTF-8 can't spell" : "") : "") +
        (NoteFiles > 0 && !literal ? " · eol/encoding-only files are notes, not hunks (--literal / literal=true carries them)" : "") +
        (ZeroContext ? $" · {ZeroContextNote}" : "");
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
/// UTF-8 BOM is kept, so its first line matches). So are a path behind a link (never written or deleted through)
/// and paths that differ only in case (on Windows git refuses the whole patch over one of them). An incomplete
/// compare is refused: its adds and removes under an unlisted directory may be listing failures. One file's section for a reader (<see cref="WriteChange"/>) shows
/// them in place instead: git's "Binary files … differ", legacy text decoded as Latin-1.
/// </summary>
public static class PatchWriter
{
    /// <exception cref="ArgumentException">The compare is incomplete (a directory could not be listed).</exception>
    public static PatchStats Write(TextWriter w, CompareReport report, string leftRoot, string rightRoot, PatchOptions? options = null)
    {
        if (report.LeftDroppedDirectories + report.RightDroppedDirectories > 0)
            throw new ArgumentException($"the compare is incomplete ({report.LeftDroppedDirectories} left / {report.RightDroppedDirectories} right " +
                                        "director(ies) could not be listed): its adds and removes there may be listing failures — " +
                                        "refusing to write it as a patch; compare again");
        var opt = options ?? new PatchOptions();
        var stats = new PatchStats();
        ZeroContextNote(w, opt, stats);
        var changed = report.Changes.Where(c => c.Status != ChangeStatus.Identical).ToList();
        var caseOnly = CaseOnly(changed);

        // Render sections concurrently (each is a few opens + reads — over SMB they must overlap), written in report
        // order as soon as every one before is: a section waits to start while too many, or too much text, is ahead of
        // what is written (32 sections of 16 MB files were a GB held). The next one to write never waits.
        int window = Math.Max(1, opt.Parallelism * 4);
        const long budgetChars = 64L << 20;
        var sections = new StringBuilder?[changed.Count]; // written as built: no second copy of each section
        var local = new PatchStats[changed.Count];
        var gate = new object();
        int claimed = -1, written = 0;
        long held = 0;
        Exception? failed = null;
        StringBuilder Render(int i)
        {
            var sw = new StringWriter { NewLine = "\n" };
            local[i] = new PatchStats();
            var c = changed[i];
            try
            {
                if (Sessions.ResultStore.HasLoneSurrogate(c.RelativePath) || (c.RenamedFrom is { } r && Sessions.ResultStore.HasLoneSurrogate(r)))
                {
                    // The patch is UTF-8: the name would come out with U+FFFD in it, git would look for that file and
                    // refuse the whole patch.
                    local[i].NameFiles = 1;
                    sw.Write($"# {Name(c)}: the path has a character UTF-8 can't spell (an unpaired surrogate; shown as \uFFFD) — " +
                             "not in this patch; do it by hand\n");
                }
                else if (caseOnly.Contains(c.RelativePath) || (c.RenamedFrom is { } f && caseOnly.Contains(f)))
                {
                    local[i].CaseFiles = 1;
                    sw.Write($"# {Name(c)}: another path in this patch differs from it only in case (one file on Windows, " +
                             "where git apply would refuse the whole patch) — not in this patch; do it by hand\n");
                }
                else Section(sw, c, leftRoot, rightRoot, opt, local[i], forGit: true);
                return sw.GetStringBuilder();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked, or gone since the compare: say so and keep going — one file must not cost the patch.
                local[i] = new PatchStats { UnreadableFiles = 1 };
                return new StringBuilder($"# {Name(c)}: could not be read ({OneLine(ex.Message)}) — not in this patch\n");
            }
        }
        var workers = Enumerable.Range(0, Math.Min(Math.Max(1, opt.Parallelism), changed.Count)).Select(_ => Task.Run(() =>
        {
            try
            {
                for (int i; (i = Interlocked.Increment(ref claimed)) < changed.Count;)
                {
                    lock (gate)
                        while (failed is null && i > written && (i - written >= window || held > budgetChars)) Monitor.Wait(gate);
                    if (failed is not null) return;
                    var section = Render(i);
                    lock (gate) // published with its size at once: the writer may take it the moment it is there
                    {
                        sections[i] = section;
                        held += section.Length;
                        Monitor.PulseAll(gate);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    failed ??= ex;
                    Monitor.PulseAll(gate);
                }
            }
        })).ToArray();
        try
        {
            for (int i = 0; i < changed.Count; i++)
            {
                StringBuilder section;
                lock (gate)
                {
                    while (sections[i] is null && failed is null) Monitor.Wait(gate);
                    if (failed is not null) break;
                    section = sections[i]!;
                }
                w.Write(section);
                stats.Add(local[i]);
                lock (gate)
                {
                    sections[i] = null;
                    held -= section.Length;
                    written = i + 1;
                    Monitor.PulseAll(gate);
                }
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                failed ??= ex; // the writer failed (a full disk): the renderers stop
                Monitor.PulseAll(gate);
            }
        }
        Task.WaitAll(workers);
        if (failed is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed).Throw();
        return stats;
    }

    /// <summary>
    /// The paths of the changes that share a path with another spelling, ignoring case: a case-only rename (Foo.c →
    /// foo.c, or every file under a directory renamed so), or a remove and an add like that. git on Windows sees the
    /// new path as already there and rejects the whole patch.
    /// </summary>
    private static HashSet<string> CaseOnly(List<FileChange> changed)
    {
        // Each path's first spelling, ignoring case: one seen again spelled otherwise puts both spellings in.
        var first = new Dictionary<string, string>(changed.Count, StringComparer.OrdinalIgnoreCase);
        var both = new HashSet<string>(StringComparer.Ordinal);
        void See(string p)
        {
            if (!first.TryAdd(p, p) && first[p] is var f && !string.Equals(f, p, StringComparison.Ordinal))
            {
                both.Add(f);
                both.Add(p);
            }
        }
        foreach (var c in changed)
        {
            See(c.RelativePath);
            if (c.RenamedFrom is { } r) See(r);
        }
        return both;
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
            w.Write(sw.GetStringBuilder()); // whole or not at all (a read failing half way), with no second copy
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
        if (c.BehindLink)
        {
            // The other tree has a link there: the file is unknown, and a patch would write or delete through the link.
            w.Write(forGit ? $"# {Name(c)}: {OneLine(c.Unreadable ?? "behind a link")} — not in this patch\n" : $"{Name(c)}: {OneLine(c.Unreadable ?? "behind a link")}\n");
            stats.UnreadableFiles++;
            return;
        }
        switch (c.Status)
        {
            case ChangeStatus.Added:
                WriteFile(w, c.RelativePath, c.RelativePath, null, Full(rightRoot, c.RelativePath), null, opt, stats, forGit);
                break;
            case ChangeStatus.Removed:
                WriteFile(w, c.RelativePath, c.RelativePath, Full(leftRoot, c.RelativePath), null, null, opt, stats, forGit);
                break;
            case ChangeStatus.Renamed:
                // git keeps 100% for a byte-identical rename: an edited one (a file reordered) is at most 99%.
                int similarity = c.PureRename ? 100 : Math.Min(99, (c.SimilarityMilli ?? 0) / 10);
                var header = $"similarity index {similarity}%\nrename from {c.RenamedFrom}\nrename to {c.RelativePath}\n";
                if (c.PureRename)
                {
                    // Byte-identical: the header alone moves it — for binary and large files too, and nothing is read.
                    w.Write($"diff --git a/{c.RenamedFrom} b/{c.RelativePath}\n{header}");
                    stats.TextFiles++;
                    break;
                }
                // Shown as decoded text, a rename whose only edit is its encoding (a BOM, cp1252 -> UTF-8) would be a
                // bare header, like a pure rename: the note says what changed.
                WriteFile(w, c.RenamedFrom!, c.RelativePath, Full(leftRoot, c.RenamedFrom!), Full(rightRoot, c.RelativePath), header, opt, stats, forGit,
                    encodingNote: !forGit);
                break;
            case ChangeStatus.Modified:
                if (!opt.Literal && c.Reason is ChangeReason.Eol or ChangeReason.Encoding)
                {
                    var (l, r) = (Describe(Full(leftRoot, c.RelativePath)), Describe(Full(rightRoot, c.RelativePath)));
                    // The reason is the text's: line endings changed with the encoding are "eol", and the note says both.
                    var note = c.Reason == ChangeReason.Eol
                        ? $"Line endings differ: a/{c.RelativePath} ({l.Eol}) b/{c.RelativePath} ({r.Eol}{(l.Eol == r.Eol ? ", at other lines" : "")})" +
                          (l.Enc != r.Enc ? $"; encoding too: a ({l.Enc}) b ({r.Enc})" : "") + "\n"
                        : $"Encoding differs: a/{c.RelativePath} ({l.Enc}) b/{c.RelativePath} ({r.Enc})\n";
                    if (forGit) w.Write($"# {note.TrimEnd('\n')} — a note, not a hunk (--literal carries it)\n");
                    else w.Write($"diff --git a/{c.RelativePath} b/{c.RelativePath}\n{note}");
                    stats.NoteFiles++;
                    break;
                }
                // A shown diff is of decoded text: an encoding change under it would be invisible.
                WriteFile(w, c.RelativePath, c.RelativePath, Full(leftRoot, c.RelativePath), Full(rightRoot, c.RelativePath), null, opt, stats, forGit,
                    encodingNote: !forGit && c.Reason is ChangeReason.Whitespace or ChangeReason.Content);
                break;
        }
    }

    /// <summary>Diff two individual files (either may be null = absent), as a patch for <c>git apply</c>.</summary>
    public static PatchStats WriteFiles(TextWriter w, string? left, string? right, string displayLeft, string displayRight, PatchOptions? options = null)
    {
        var opt = options ?? new PatchOptions();
        var stats = new PatchStats();
        ZeroContextNote(w, opt, stats);
        WriteFile(w, displayLeft, displayRight, left, right, null, opt, stats, forGit: true);
        return stats;
    }

    /// <summary>A patch with no context says so in a '#' line before its first section (git skips it): plain
    /// <c>git apply</c> rejects a hunk without context, and nothing else in the file tells why.</summary>
    private static void ZeroContextNote(TextWriter w, PatchOptions opt, PatchStats stats)
    {
        if (opt.Context != 0) return;
        stats.ZeroContext = true;
        w.Write($"# written with no context lines (-U 0): {PatchStats.ZeroContextNote}\n");
    }

    /// <param name="encodingNote">Both sides present: add "Encoding differs too" to the header when their encodings
    /// differ (named from the bytes read here, not by reading the files again).</param>
    private static void WriteFile(TextWriter w, string oldRel, string newRel, string? oldFull, string? newFull,
        string? extraHeader, PatchOptions opt, PatchStats stats, bool forGit, bool encodingNote = false)
    {
        long oldLen = oldFull is null ? 0 : new FileInfo(oldFull).Length;
        long newLen = newFull is null ? 0 : new FileInfo(newFull).Length;
        string what = oldRel == newRel ? oldRel : $"{oldRel} -> {newRel}";

        bool giant = oldLen > opt.MaxTextBytes || newLen > opt.MaxTextBytes;
        // One open per side: small files are read whole once (sniff + decode from the same bytes).
        var oldBytes = oldFull is null ? null : giant ? TextInspector.ReadHead(oldFull) : TextInspector.ReadAll(oldFull);
        var newBytes = newFull is null ? null : giant ? TextInspector.ReadHead(newFull) : TextInspector.ReadAll(newFull);
        if (encodingNote && EncodingNote(oldFull!, newFull!, giant ? null : oldBytes, giant ? null : newBytes, oldRel, newRel) is { } note)
            extraHeader += note;
        // A small file is sniffed whole (a NUL past 8 KB is binary too: a text diff must never carry raw NULs).
        bool binary = (oldBytes is not null && TextInspector.LooksBinary(oldBytes)) || (newBytes is not null && TextInspector.LooksBinary(newBytes));
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
            if (WriteGiant(g, oldRel, newRel, oldFull, newFull, oldLen, newLen, opt)) stats.CoarseFiles++;
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
        if (UnifiedDiff.Write(w, oldRel, newRel, oldText, newText, opt.Context, extraHeader, opt.Cancellation)) stats.CoarseFiles++;
        stats.TextFiles++;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>The bytes as UTF-8 text, BOM included (null bytes = an absent side). False when they aren't UTF-8.</summary>
    private static bool Utf8(byte[]? bytes, out string? text)
    {
        text = null;
        if (bytes is null) return true;
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0x00, 0x00, 0xFE, 0xFF, ..]) return false; // UTF-16/32 BOM
        if (!System.Text.Unicode.Utf8.IsValid(bytes)) return false;
        try { text = StrictUtf8.GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private static string Side(string oldRel, string newRel, string? oldFull, string? newFull)
        => newFull is null ? $"delete a/{oldRel} by hand" : oldFull is null ? $"copy b/{newRel} by hand"
            : oldRel != newRel ? $"copy b/{newRel} by hand and delete a/{oldRel}" : $"copy b/{newRel} by hand";

    /// <returns>Whether the block diff was coarse (its regions wider than the change).</returns>
    private static bool WriteGiant(TextWriter w, string oldRel, string newRel, string? oldFull, string? newFull,
        long oldLen, long newLen, PatchOptions opt)
    {
        if (oldFull is null || newFull is null)
        {
            w.Write($"Large file {(oldFull is null ? "added" : "removed")}: {(oldFull is null ? "b/" + newRel : "a/" + oldRel)} ({Math.Max(oldLen, newLen):N0} bytes)\n");
            return false;
        }
        var r = GiantFileDiffer.Diff(oldFull, newFull, ct: opt.Cancellation);
        w.Write($"Large files a/{oldRel} and b/{newRel} differ: {r.Changes.Count} changed region(s), " +
                $"{r.ChangedOldBytes:N0} of {r.OldSize:N0} bytes ({100.0 * r.ChangedOldBytes / Math.Max(1, r.OldSize):F2}%) -> {r.NewSize:N0} bytes\n");
        if (r.Coarse)
            w.Write("  coarse: too many scattered changes to align block by block in time — the regions are wider than the change\n");
        foreach (var ch in r.Changes.Take(opt.MaxGiantRanges))
            w.Write($"  {CanonicalTokens.Token(ch.Op)} old[{ch.OldOffset:N0}, +{ch.OldLength:N0}) -> new[{ch.NewOffset:N0}, +{ch.NewLength:N0})\n");
        if (r.Changes.Count > opt.MaxGiantRanges)
            w.Write($"  ... {r.Changes.Count - opt.MaxGiantRanges} more region(s)\n");
        return r.Coarse;
    }

    /// <summary>"Encoding differs too: …" when the two files' encodings differ, else null.</summary>
    /// <param name="leftBytes">The whole file when already read (else null: it is read here).</param>
    private static string? EncodingNote(string leftFull, string rightFull, byte[]? leftBytes, byte[]? rightBytes, string oldRel, string newRel)
    {
        var (l, r) = (EncodingOf(leftFull, leftBytes), EncodingOf(rightFull, rightBytes));
        return l == r ? null : $"Encoding differs too: a/{oldRel} ({l}) b/{newRel} ({r})\n";
    }

    /// <summary><see cref="Describe"/>'s encoding name, from the whole file's bytes when they are at hand (and within
    /// the size it reads through).</summary>
    private static string EncodingOf(string path, byte[]? whole)
    {
        if (whole is null || whole.Length > TextInspector.WholeSniffBytes) return Describe(path).Enc;
        var (name, _, skip) = Bom(whole);
        if (skip <= 3 && !System.Text.Unicode.Utf8.IsValid(whole.AsSpan(skip)))
            name = skip == 3 ? "UTF-8 BOM before non-UTF-8 bytes" : "a legacy code page, not UTF-8";
        return name;
    }

    private static (string Name, Encoding Enc, int Skip) Bom(ReadOnlySpan<byte> h) => h switch
    {
        [0xFF, 0xFE, 0x00, 0x00, ..] => ("UTF-32 LE", new UTF32Encoding(false, false), 4),
        [0x00, 0x00, 0xFE, 0xFF, ..] => ("UTF-32 BE", new UTF32Encoding(true, false), 4),
        [0xFF, 0xFE, ..] => ("UTF-16 LE", new UnicodeEncoding(false, false), 2),
        [0xFE, 0xFF, ..] => ("UTF-16 BE", new UnicodeEncoding(true, false), 2),
        [0xEF, 0xBB, 0xBF, ..] => ("UTF-8 BOM", Encoding.Latin1, 3),
        _ => ("UTF-8/ASCII, no BOM", Encoding.Latin1, 0),
    };

    private static string Name(FileChange c) => c.RenamedFrom is { } f ? $"{f} -> {c.RelativePath}" : c.RelativePath;

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    private static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>A file's encoding and line-ending style, read through (streamed) up to
    /// <see cref="TextInspector.WholeSniffBytes"/>, so a note can't contradict the reason found on the whole file;
    /// past that, both say they are of its first 16 MB. Line endings are counted in its own encoding (UTF-16's
    /// CR LF is four bytes, never a lone 0x0A).</summary>
    private static (string Enc, string Eol) Describe(string path)
    {
        var (name, enc, skip) = Bom(TextInspector.ReadHead(path, 4));
        using var fs = TextInspector.OpenShared(path, 1);
        bool cut = fs.Length > TextInspector.WholeSniffBytes;
        long left = Math.Min(fs.Length, TextInspector.WholeSniffBytes) - skip;
        fs.Position = skip;
        // CR and LF are single bytes in UTF-8; the name says whether the rest is UTF-8 (a character cut at 16 MB is fine).
        var utf8 = skip <= 3 ? StrictUtf8.GetDecoder() : null;
        var decoder = enc.GetDecoder();
        var buf = new byte[1 << 16];
        var chars = new char[enc.GetMaxCharCount(buf.Length)];
        int crlf = 0, lf = 0, cr = 0, n;
        bool prevCr = false;
        while (left > 0 && (n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
        {
            left -= n;
            bool last = left == 0;
            if (utf8 is not null)
                try { utf8.GetCharCount(buf, 0, n, flush: last && !cut); }
                catch (DecoderFallbackException)
                {
                    name = skip == 3 ? "UTF-8 BOM before non-UTF-8 bytes" : "a legacy code page, not UTF-8";
                    utf8 = null;
                }
            int m = decoder.GetChars(buf, 0, n, chars, 0, flush: last);
            for (int i = 0; i < m; i++)
            {
                char c = chars[i];
                if (c == '\n') { if (prevCr) crlf++; else lf++; }
                else if (prevCr) cr++;
                prevCr = c == '\r';
            }
        }
        if (prevCr) cr++;
        int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        var eol = kinds > 1 ? "mixed" : crlf > 0 ? "CRLF" : lf > 0 ? "LF" : cr > 0 ? "CR" : "no newlines";
        const string First = " in its first 16 MB";
        return cut ? (name + First, eol + First) : (name, eol);
    }
}
