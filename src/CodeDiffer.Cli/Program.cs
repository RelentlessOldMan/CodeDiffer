using System.Reflection;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Giant;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Port;
using CodeDiffer.Core.Report;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using CodeDiffer.Core.Verify;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0) { PrintUsage(); return 0; }

    switch (args[0])
    {
        case "version" or "--version" or "-v":
            Console.WriteLine($"codediffer {Version()}");
            return 0;
        case "compare":
            return Compare(args);
        case "blockdiff":
            return BlockDiff(args);
        case "diff":
            return DiffFiles(args);
        case "apply":
            return Apply(args);
        case "apply-overlay":
            return ApplyOverlay(args);
        case "compare3":
            return Compare3(args);
        case "verify":
            return Verify(args);
        case "report":
            return Report(args);
        case "results":
            return Results(args);
        case "help" or "--help" or "-h":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"unknown command: {args[0]}");
            PrintUsage();
            return 64; // EX_USAGE
    }
}

static int Compare(string[] args)
{
    const string compareUsage = "usage: codediffer compare <left-tree> <right-tree> [--threads N] [--no-cache | --rehash] [--fast-stat] " +
                                "[--patch [-U N] [--literal]] [--timings] [--no-save] [--html [--large] [--include-identical] [--max-diffs N] [--out DIR]]";
    if (args.Length < 3)
    {
        Console.Error.WriteLine(compareUsage);
        return 64;
    }
    if (!ArgsOk(args, 2, compareUsage,
            ["--no-cache", "--rehash", "--fast-stat", "--patch", "--literal", "--timings", "--no-save", "--html", "--large", "--include-identical"],
            ["--threads", "-U", "--max-diffs", "--out"]))
        return 64;

    int threads = new CompareOptions().Parallelism;
    if (FlagValue(args, "--threads") is { } t && (!int.TryParse(t, out threads) || threads < 1))
    {
        Console.Error.WriteLine("error: --threads needs a positive integer");
        return 64;
    }
    var cache = args.Contains("--no-cache") ? CacheMode.Off : args.Contains("--rehash") ? CacheMode.Rehash : CacheMode.On;
    var options = new CompareOptions { Parallelism = threads, Cache = cache, StrictStat = !args.Contains("--fast-stat") };
    // Every option is checked before a compare that may take an hour, not after it.
    bool patch = args.Contains("--patch");
    PatchOptions popt = new();
    if (patch && !TryPatchOptions(args, out popt)) return 64;
    if (args.Contains("--html") && !MaxDiffsOk(args, out _)) return 64;

    // Run as a session, like compare3 and the MCP server: the result directory says "running" while it runs (a crash
    // leaves an honest trace), and "cancelled" or "failed" if it doesn't finish.
    CompareSession session;
    try { session = new SessionStore(save: !args.Contains("--no-save")).Start(args[1], args[2], options); }
    catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or IOException or UnauthorizedAccessException)
    {
        // Honesty contract: a bad input is a loud error, never a silent all-added/all-deleted "success".
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
    WithProgress(() => session.Wait(Timeout.InfiniteTimeSpan), () => ProgressView.Line(session.Progress!), () => session.Cancel());
    if (session.Cancelled) return Cancelled(session.Elapsed, options.Cache);
    if (session.Error is { } err)
    {
        Console.Error.WriteLine($"error: {err}");
        return 2;
    }
    var report = session.Report!;

    if (args.Contains("--timings"))
        foreach (var (phase, elapsed) in report.Timings)
            Console.Error.WriteLine($"  timing     {phase,-28} {elapsed.TotalMilliseconds,9:N0} ms");

    // --patch: the patch goes to stdout (pipe it to a file or `git apply`), the summary to stderr.
    var info = patch ? Console.Error : Console.Out;
    if (patch && report.LeftDroppedDirectories + report.RightDroppedDirectories > 0)
    {
        // An unlisted directory makes its files look removed: a patch would delete them. Nothing is written.
        Console.Error.WriteLine("error: incomplete walk (unlistable directories) — refusing to write a partial change set as a patch");
    }
    else if (patch)
    {
        using var stdout = PatchStdout();
        var ps = PatchWriter.Write(stdout, report, args[1], args[2], popt);
        stdout.Flush();
        info.WriteLine($"patch: {ps.Summary(popt.Literal)}");
    }
    else
    {
        PrintChanges(report);
    }

    PrintSummary(info, args[1], args[2], report);
    info.WriteLine($"  elapsed    {session.Elapsed.ToString(@"hh\:mm\:ss")}  (threads {options.Parallelism}, cache {cache.ToString().ToLowerInvariant()})");
    info.WriteLine($"  content    {report.ComparedPairs} same-size pair(s) · {report.CacheHits} side(s) from hash cache" +
        (report.CodeCompassHits > 0 ? $" ({report.CodeCompassHits} via CodeCompass)" : "") +
        $" · {report.BytesRead / (1024.0 * 1024 * 1024):F1} GB read" +
        (report.PendingFiles > 0 ? $" · {report.PendingFiles} too recently modified to cache yet" : ""));
    foreach (var n in AgentViews.Notes(report)) Console.Error.WriteLine("note: " + n);

    int? htmlCode = null; // a failed or stopped report: its exit code, but only after the warnings below
    if (session.ResultDir is not null)
    {
        info.WriteLine(session.SaveError is { } se ? $"  saved      NOT saved: {se}" : $"  saved      {session.ResultDir}  (id {session.Id})");
        if (args.Contains("--html") && session.SaveError is null) htmlCode = WriteHtml(session, args, info);
    }
    else if (args.Contains("--html"))
        Console.Error.WriteLine("note: --html needs a saved result; drop --no-save");

    if (report.LeftDroppedDirectories + report.RightDroppedDirectories > 0)
    {
        Console.Error.WriteLine(
            $"WARNING: incomplete walk — {report.LeftDroppedDirectories} left / {report.RightDroppedDirectories} right " +
            "director(ies) could not be listed; adds/removes under them may be listing failures.");
    }
    if (report.UnreadableFiles > 0)
        Console.Error.WriteLine($"WARNING: {report.UnreadableFiles} file(s) could not be read (locked, vanished or denied) — their verdict " +
            "is unknown, listed as [unreadable] (a pair as modified, never identical); compare again once they can be read.");
    return htmlCode ?? (report.LeftDroppedDirectories + report.RightDroppedDirectories + report.UnreadableFiles > 0 ? 3 : 0);
}

/// <summary>Write the HTML report for a finished session; prints its path. Returns an exit code on failure, else null.</summary>
static int? WriteHtml(Session s, string[] args, TextWriter info)
{
    if (!MaxDiffsOk(args, out int maxDiffs)) return 64;
    var opt = new HtmlReportOptions
    {
        IncludeLarge = args.Contains("--large"),
        IncludeIdentical = args.Contains("--include-identical"),
        MaxDiffs = maxDiffs,
        OutDir = FlagValue(args, "--out"),
    };
    // The first Ctrl+C stops rendering diffs and still writes the report (the files not reached have no diff);
    // a second one ends the process.
    using var cts = new CancellationTokenSource();
    bool stopping = false;
    ConsoleCancelEventHandler onCtrlC = (_, e) =>
    {
        if (stopping) return;
        stopping = true;
        e.Cancel = true;
        Console.Error.WriteLine("\nstopping the report (Ctrl+C again to quit at once)");
        cts.Cancel();
    };
    Console.CancelKeyPress += onCtrlC;
    try
    {
        int last = -1;
        var r = HtmlReport.Write(s, opt, (done, total) =>
        {
            int pct = total == 0 ? 100 : done * 100 / total;
            if (pct / 10 != last / 10) { last = pct; Console.Error.Write($"\r  report     rendering diffs {done}/{total}"); }
        }, cts.Token);
        Console.Error.WriteLine();
        info.WriteLine($"  report     {r.IndexPath}");
        info.WriteLine($"             {r.Files:N0} file(s) · {r.Rendered:N0} diff(s) rendered" +
            (r.Capped > 0 ? $" · {r.Capped:N0} cut (whole diff linked)" : "") +
            (r.Large > 0 ? $" · {r.Large:N0} large not block-diffed (--large)" : "") +
            (r.NotRendered > 0 ? $" · {r.NotRendered:N0} past --max-diffs" : "") +
            (r.Unreadable > 0 ? $" · {r.Unreadable:N0} unreadable" : "") +
            (r.Stopped > 0 ? $" · STOPPED: {r.Stopped:N0} without a diff (write it again for the rest)" : ""));
        return r.Stopped > 0 ? 130 : null;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
    {
        Console.Error.WriteLine($"error: report: {ex.Message}");
        return 2;
    }
    finally { Console.CancelKeyPress -= onCtrlC; }
}

/// <summary>--max-diffs, when given, is a non-negative integer (else the error is printed).</summary>
static bool MaxDiffsOk(string[] args, out int maxDiffs)
{
    maxDiffs = new HtmlReportOptions().MaxDiffs;
    if (FlagValue(args, "--max-diffs") is { } md && (!int.TryParse(md, out maxDiffs) || maxDiffs < 0))
    {
        Console.Error.WriteLine("error: --max-diffs needs a non-negative integer");
        return false;
    }
    return true;
}

/// <summary>report: the HTML report for a saved compare (by id or result directory).</summary>
static int Report(string[] args)
{
    const string reportUsage = "usage: codediffer report <id|result-dir> [--large] [--include-identical] [--max-diffs N] [--out DIR]";
    if (args.Length < 2 || !ArgsOk(args, 1, reportUsage, ["--large", "--include-identical"], ["--max-diffs", "--out"]))
    {
        if (args.Length < 2) Console.Error.WriteLine(reportUsage);
        return 64;
    }
    Session? s;
    try { s = new SessionStore().Get(args[1]); }
    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
    if (s is null)
    {
        Console.Error.WriteLine($"error: no saved compare '{args[1]}' in {ResultStore.DefaultRoot} (codediffer results lists them)");
        return 2;
    }
    Console.WriteLine($"{(s is Compare3Session ? "compare3" : "compare")} {s.Id} · {s.Title}");
    return WriteHtml(s, args, Console.Out) ?? 0;
}

/// <summary>results: list saved compares, newest first; --prune deletes old ones (a dry run unless --yes).</summary>
static int Results(string[] args)
{
    if (args.Contains("--prune")) return Prune(args);
    if (!ArgsOk(args, 0, "usage: codediffer results [--max N] | results --prune [--keep N] [--older-than DAYS] [--yes]", [], ["--max"])) return 64;
    int max = 20;
    if (FlagValue(args, "--max", from: 1) is { } m && (!int.TryParse(m, out max) || max < 1))
    {
        Console.Error.WriteLine("error: --max needs a positive integer");
        return 64;
    }
    var root = ResultStore.DefaultRoot;
    var saved = ResultStore.List(root, max);
    Console.WriteLine($"{saved.Count} saved compare(s) in {root}");
    foreach (var c in saved)
    {
        var counts = c.Counts.Where(x => x.Count > 0 && x.Name != "identical").Select(x => $"{x.Name} {x.Count:N0}");
        Console.WriteLine($"  {c.Id}  {c.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {c.Kind,-8} {c.State,-7} {string.Join("  ", c.Roots.Select(r => r.Path))}");
        Console.WriteLine($"        {string.Join(", ", counts)}{(c.Error is { } e ? $"  error: {e}" : "")}");
    }
    return 0;
}

/// <summary>results --prune [--keep N] [--older-than DAYS] [--yes]: delete all but the newest N saved compares.</summary>
static int Prune(string[] args)
{
    // Strict: this deletes, so a misspelled or valueless flag is an error, never a silently wider prune.
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] is "--prune" or "--yes") continue;
        if (args[i] is "--keep" or "--older-than" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) { i++; continue; }
        Console.Error.WriteLine($"error: {(args[i] is "--keep" or "--older-than" ? $"{args[i]} needs a value" : $"unknown argument '{args[i]}'")}");
        Console.Error.WriteLine("usage: codediffer results --prune [--keep N] [--older-than DAYS] [--yes]");
        return 64;
    }
    int keep = 20;
    if (FlagValue(args, "--keep", from: 1) is { } k && (!int.TryParse(k, out keep) || keep < 0))
    {
        Console.Error.WriteLine("error: --keep needs a non-negative integer");
        return 64;
    }
    TimeSpan? olderThan = null;
    if (FlagValue(args, "--older-than", from: 1) is { } d)
    {
        if (!double.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var days)
            || !double.IsFinite(days) || days < 0 || days > 36500)
        {
            Console.Error.WriteLine("error: --older-than needs a number of days (0 to 36500)");
            return 64;
        }
        olderThan = TimeSpan.FromDays(days);
    }
    bool yes = args.Contains("--yes");
    var root = Path.GetFullPath(ResultStore.DefaultRoot);
    var doomed = ResultStore.PruneCandidates(root, keep, olderThan, DateTime.UtcNow);
    var rule = $"all but the newest {keep}" + (olderThan is { } o ? $", started over {o.TotalDays:0.###} day(s) ago" : "");
    if (doomed.Count == 0)
    {
        Console.WriteLine($"nothing to prune in {root} ({rule})");
        return 0;
    }
    long total = 0, freed = 0;
    int deleted = 0;
    Console.WriteLine($"{(yes ? "deleting" : "would delete")} {doomed.Count} saved compare(s) in {root} ({rule}):");
    foreach (var c in doomed)
    {
        long size = ResultStore.SizeOf(c.Dir);
        total += Math.Max(0, size);
        Console.WriteLine($"  {c.Id}  {c.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {c.Kind,-8} {c.State,-9} {(size < 0 ? "?" : AgentViews.Bytes(size)),9}  {Path.GetFileName(c.Dir)}");
        if (!yes) continue;
        try
        {
            ResultStore.Delete(root, c);
            deleted++;
            freed += Math.Max(0, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException
                                       or System.Text.Json.JsonException or FormatException)
        {
            Console.Error.WriteLine($"    not deleted: {ex.Message}");
        }
    }
    Console.WriteLine(yes ? $"deleted {deleted} of {doomed.Count} ({AgentViews.Bytes(freed)} freed" +
                            (deleted < doomed.Count ? $"; {AgentViews.Bytes(total - freed)} left, prune again to retry)" : ")")
                          : $"{AgentViews.Bytes(total)} in all; run again with --yes to delete them");
    return deleted == doomed.Count || !yes ? 0 : 1;
}

static int BlockDiff(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: codediffer blockdiff <left-file> <right-file>");
        return 64;
    }
    if (!ArgsOk(args, 2, "usage: codediffer blockdiff <left-file> <right-file>", [], [])) return 64;
    if (!File.Exists(args[1]) || !File.Exists(args[2]))
    {
        Console.Error.WriteLine("error: both arguments must be existing files");
        return 2;
    }

    var r = GiantFileDiffer.Diff(args[1], args[2]);
    Console.WriteLine($"blockdiff: {args[1]}  vs  {args[2]}");
    Console.WriteLine($"  size       {r.OldSize} -> {r.NewSize} bytes");
    Console.WriteLine($"  blocks     {r.OldBlocks} -> {r.NewBlocks} (content-defined)");
    if (r.Identical)
    {
        Console.WriteLine("  identical  (no blocks changed)");
        return 0;
    }

    double pct = r.OldSize == 0 ? 100 : 100.0 * r.ChangedOldBytes / r.OldSize;
    Console.WriteLine($"  changed    {r.Changes.Count} region(s) · {r.ChangedOldBytes} old / {r.ChangedNewBytes} new bytes ({pct:0.00}% of old) · {r.UnchangedBytes} bytes unchanged");
    int shown = 0;
    foreach (var c in r.Changes)
    {
        if (shown++ == 20) { Console.WriteLine($"    … {r.Changes.Count - 20} more"); break; }
        var tag = c.Op switch { HunkOp.Insert => "INS", HunkOp.Delete => "DEL", _ => "REP" };
        Console.WriteLine($"    {tag} old[{c.OldOffset},+{c.OldLength}) -> new[{c.NewOffset},+{c.NewLength})");
    }
    return 0;
}

static int Verify(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: codediffer verify <delta.json> [--base DIR --variant DIR] | <conflict.json> [--base DIR --v1 DIR --v2 DIR]");
        return 64;
    }

    // Route on deltaKind: a 3-way artifact carries conflictTruthSha, not diffTruthSha.
    string deltaKind;
    try
    {
        deltaKind = DeltaKind(args[1]);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }

    if (deltaKind == "conflict-3way")
        return VerifyConflict(args);

    // A misspelled or half-given tree flag used to skip the tree checks silently and pass on the digest alone.
    const string deltaUsage = "usage: codediffer verify <delta.json> [--base DIR --variant DIR]";
    if (!ArgsOk(args, 1, deltaUsage, [], ["--base", "--variant"])) return 64;
    if ((FlagValue(args, "--base") is null) != (FlagValue(args, "--variant") is null))
    {
        Console.Error.WriteLine("error: the tree checks need both --base and --variant");
        Console.Error.WriteLine(deltaUsage);
        return 64;
    }

    DeltaManifest manifest;
    try
    {
        manifest = DeltaManifestParser.ParseFile(args[1]);
        DeltaVerifier.AssertSupportedVersion(manifest);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException or FormatException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }

    string? baseDir = FlagValue(args, "--base");
    string? variantDir = FlagValue(args, "--variant");

    var v = DeltaVerifier.VerifyDigest(manifest);
    Console.WriteLine($"verify: {args[1]}");
    Console.WriteLine($"  manifestVersion {manifest.ManifestVersion}");
    Console.WriteLine($"  modified {manifest.Modified.Count} · added {manifest.Added.Count} · removed {manifest.Removed.Count} · renamed {manifest.Renamed.Count}");
    Console.WriteLine($"  diffTruthSha stated     {v.Stated ?? "(none)"}");
    Console.WriteLine($"  diffTruthSha recomputed {v.Recomputed}");
    Console.WriteLine(v.Ok ? "  OK — digest reproduced" : "  FAIL — digest mismatch");

    bool crossOk = true;
    if (baseDir is not null && variantDir is not null)
    {
        if (!Directory.Exists(baseDir) || !Directory.Exists(variantDir))
        {
            Console.Error.WriteLine("error: --base and --variant must both be existing directories");
            return 2;
        }

        var cc = DeltaTreeCrossCheck.Run(baseDir, variantDir, manifest);
        Console.WriteLine($"  hunk cross-check: {cc.Reconstructed}/{cc.Checked} reconstructed · exact {cc.ExactMatches}/{cc.Checked} · {cc.Skipped} skipped (binary/eol/encoding/metadata)");
        foreach (var f in cc.Files.Where(f => f.Checked && !f.Reconstructs))
            Console.WriteLine($"    MISMATCH {f.Path} ({f.Problem ?? "manifest hunks do not rebuild the variant"})");
        Console.WriteLine($"  content cross-check: {manifest.Modified.Count:N0} modified record(s), shas and sizes against the trees · " +
                          $"{cc.ContentMismatches.Count:N0} wrong");
        foreach (var m in cc.ContentMismatches.Take(20)) Console.WriteLine($"    WRONG    {m}");
        Console.WriteLine(cc.Ok ? "  OK — hunks are the canonical ones and reconstruct the variant, and the shas and sizes are the trees' files"
                                : "  FAIL — a manifest hunk set does not rebuild the variant or is not canonical, or a sha or size is not the trees' file");
        crossOk = cc.Ok;

        // CodeDiffer's own compare of the two trees (hash cache on: warm, it reads only the changed files) against
        // the manifest's file operations — the verdicts a user actually sees.
        CompareReport report;
        using var stop = new CancellationTokenSource();
        var progress = new CompareProgress();
        try
        {
            report = WithProgress(() => new DirectoryComparer(new CompareOptions()).Compare(baseDir, variantDir, progress, ct: stop.Token),
                () => ProgressView.Line(progress), stop.Cancel);
        }
        catch (OperationCanceledException) { return Cancelled(TimeSpan.Zero, CacheMode.On); }
        var fo = DeltaFileOpsCheck.Run(report, manifest, baseDir: baseDir, variantDir: variantDir);
        Console.WriteLine($"  file-ops cross-check (CodeDiffer's own compare): {fo.Matched:N0}/{fo.Expected:N0} match · " +
                          $"{fo.MissingCount:N0} missing · {fo.ExtraCount:N0} extra · {fo.WrongReasonCount:N0} wrong reason · {fo.WrongSimilarityCount:N0} wrong rename similarity or reason");
        foreach (var m in fo.Missing) Console.WriteLine($"    MISSING  {m}");
        foreach (var x in fo.Extra) Console.WriteLine($"    EXTRA    {x}");
        foreach (var w in fo.WrongReason.Concat(fo.WrongSimilarity)) Console.WriteLine($"    WRONG    {w}");
        if (report.UnreadableFiles + report.LeftDroppedDirectories + report.RightDroppedDirectories > 0)
            Console.WriteLine($"    (the compare could not read {report.UnreadableFiles:N0} file(s) / list " +
                              $"{report.LeftDroppedDirectories + report.RightDroppedDirectories:N0} director(ies))");
        Console.WriteLine(fo.Ok ? "  OK — CodeDiffer's compare reports exactly the manifest's file operations"
                                : "  FAIL — CodeDiffer's compare disagrees with the manifest");
        crossOk &= fo.Ok;
    }

    return v.Ok && crossOk ? 0 : 1;
}

static int VerifyConflict(string[] args)
{
    if (!ArgsOk(args, 1, "usage: codediffer verify <conflict.json> [--base DIR --v1 DIR --v2 DIR]", [], ["--base", "--v1", "--v2"])) return 64;
    ConflictManifest manifest;
    try
    {
        manifest = ConflictManifestParser.ParseFile(args[1]);
        DeltaVerifier.AssertSupportedVersion(manifest);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException or FormatException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }

    var v = DeltaVerifier.VerifyConflictDigest(manifest);
    Console.WriteLine($"verify: {args[1]}");
    Console.WriteLine($"  manifestVersion {manifest.ManifestVersion}  (deltaKind conflict-3way)");
    Console.WriteLine($"  conflicts {manifest.Conflicts.Count} · clean-merges {manifest.CleanMerges.Count}  (v1Tree {manifest.V1Tree ?? "?"} · v2Tree {manifest.V2Tree ?? "?"})");
    Console.WriteLine($"  conflictTruthSha stated     {v.Stated ?? "(none)"}");
    Console.WriteLine($"  conflictTruthSha recomputed {v.Recomputed}");
    Console.WriteLine(v.Ok ? "  OK — digest reproduced" : "  FAIL — digest mismatch");

    // With the three trees, run CodeDiffer's own 3-way merge and assert it reproduces the decomposition.
    string? baseDir = FlagValue(args, "--base");
    string? v1Dir = FlagValue(args, "--v1");
    string? v2Dir = FlagValue(args, "--v2");
    bool mergeOk = true;
    if (baseDir is not null || v1Dir is not null || v2Dir is not null)
    {
        if (baseDir is null || v1Dir is null || v2Dir is null ||
            !Directory.Exists(baseDir) || !Directory.Exists(v1Dir) || !Directory.Exists(v2Dir))
        {
            Console.Error.WriteLine("error: --base, --v1 and --v2 must all be existing directories");
            return 2;
        }

        var cc = ConflictTreeCrossCheck.Run(baseDir, v1Dir, v2Dir, manifest);
        Console.WriteLine($"  3-way cross-check over {cc.FilesChecked} files:");
        Console.WriteLine($"    [1] my diff3 decomposition: conflicts {cc.MyConflicts} · clean {cc.MyClean}  (manifest {cc.ManifestConflicts} · {cc.ManifestClean})");
        Console.WriteLine($"        conflictTruthSha from my merge {cc.MyDigest}");
        Console.WriteLine(cc.DecompositionMatches
            ? "        OK — my 3-way merge reproduces the exact decomposition"
            : "        (differs — expected on identical-line corpora; see reconstruction below)");
        if (!cc.DecompositionMatches)
        {
            foreach (var c in cc.ConflictsOnlyInMine) Console.WriteLine($"        + mine-only conflict {c.Path}:{c.BaseStart},{c.BaseLines}");
            foreach (var c in cc.ConflictsOnlyInManifest) Console.WriteLine($"        - manifest-only conflict {c.Path}:{c.BaseStart},{c.BaseLines}");
        }
        Console.WriteLine($"    [2] reconstruction from manifest coords: V1 {cc.V1Reconstructed}/{cc.FilesChecked} · V2 {cc.V2Reconstructed}/{cc.FilesChecked}");
        Console.WriteLine($"        conflicted files: {(cc.ConflictPathsMatch ? "my merge conflicts in exactly the manifest's files" : $"{cc.ConflictPathsOnlyInMine.Count:N0} only in mine · {cc.ConflictPathsOnlyInManifest.Count:N0} only in the manifest")}");
        foreach (var p in cc.ConflictPathsOnlyInMine.Take(20)) Console.WriteLine($"        + mine-only conflicted file {p}");
        foreach (var p in cc.ConflictPathsOnlyInManifest.Take(20)) Console.WriteLine($"        - manifest-only conflicted file {p}");
        if (!cc.DecompositionMatches)
        {
            Console.WriteLine($"        regions against the trees: {(cc.UnsoundRegions.Count == 0 ? "every conflict and clean merge is what it says" : $"{cc.UnsoundRegions.Count:N0} not what they say")}");
            foreach (var u in cc.UnsoundRegions.Take(20)) Console.WriteLine($"        ! {u}");
        }
        Console.WriteLine(cc.Ok
            ? "  OK — 3-way verified (exact decomposition, or reconstruction with the same conflicted files and sound regions)"
            : "  FAIL — my merge does not reproduce the manifest's decomposition or its conflicted files");
        mergeOk = cc.Ok;

        // compare3 itself over the whole trees (hash cache on: warm, it reads only the changed files): the verdicts a
        // user actually sees, lines with their endings, in every file.
        ThreeWayReport r3;
        using var stop = new CancellationTokenSource();
        CompareProgress p1 = new(), p2 = new();
        try
        {
            r3 = WithProgress(() => TreeMerger.Run(baseDir, v1Dir, v2Dir, new CompareOptions(), p1, p2, stop.Token),
                () => p1.Phase != ComparePhase.Done ? "base->v1: " + ProgressView.Line(p1)
                    : p2.Phase != ComparePhase.Done ? "base->v2: " + ProgressView.Line(p2)
                    : "classifying and merging the paths both sides touched", stop.Cancel);
        }
        catch (OperationCanceledException) { return Cancelled(TimeSpan.Zero, CacheMode.On); }
        var c3 = ConflictTreeCrossCheck.Compare3(r3, manifest);
        Console.WriteLine($"    [3] compare3 over the whole trees: {c3.Entries:N0} changed paths · {c3.Conflicts:N0} conflicts · " +
                          $"{c3.ConflictOnlyInCompare3.Count:N0} conflicted only in compare3 · {c3.ConflictOnlyInManifest.Count:N0} only in the manifest · " +
                          $"{c3.Unchanged.Count:N0} manifest file(s) it found unchanged · {c3.MergedNotInManifest.Count:N0} merged file(s) the manifest leaves out");
        foreach (var p in c3.ConflictOnlyInCompare3.Take(20)) Console.WriteLine($"        + compare3-only conflicted file {p}");
        foreach (var p in c3.ConflictOnlyInManifest.Take(20)) Console.WriteLine($"        - manifest-only conflicted file {p}");
        foreach (var p in c3.Unchanged.Take(20)) Console.WriteLine($"        ? unchanged in compare3 {p}");
        foreach (var p in c3.MergedNotInManifest.Take(20)) Console.WriteLine($"        - merged by compare3, not in the manifest {p}");
        if (c3.Unread > 0) Console.WriteLine($"        (compare3 could not read {c3.Unread:N0} file(s) / director(ies))");
        Console.WriteLine(c3.Ok
            ? "  OK — compare3 conflicts in exactly the manifest's files"
            : "  FAIL — compare3 disagrees with the manifest");
        mergeOk &= c3.Ok;
    }

    return v.Ok && mergeOk ? 0 : 1;
}

/// <summary>Peek at <c>_meta.deltaKind</c> so verify can route diff vs conflict-3way.</summary>
static string DeltaKind(string path)
{
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
    return doc.RootElement.TryGetProperty("_meta", out var meta)
           && meta.TryGetProperty("deltaKind", out var dk)
           && dk.ValueKind == System.Text.Json.JsonValueKind.String
        ? dk.GetString()!
        : "diff";
}

/// <summary>
/// Refuse what a command doesn't take: an unknown flag, an extra word, or a flag's missing value. A mistyped option
/// (--no-chache, --merge-dir) used to be ignored — the run went ahead without it, perhaps for an hour.
/// </summary>
/// <returns>True when the arguments are fine; else the error and the usage are printed.</returns>
static bool ArgsOk(string[] args, int positional, string usage, string[] switches, string[] valued)
{
    for (int i = 1 + positional; i < args.Length; i++)
    {
        if (switches.Contains(args[i])) continue;
        if (valued.Contains(args[i]))
        {
            if (i + 1 < args.Length && !(args[i + 1].StartsWith("--", StringComparison.Ordinal))) { i++; continue; }
            Console.Error.WriteLine($"error: {args[i]} needs a value");
        }
        else Console.Error.WriteLine($"error: unknown argument '{args[i]}'");
        Console.Error.WriteLine(usage);
        return false;
    }
    return true;
}

static string? FlagValue(string[] args, string flag, int from = 2)
{
    for (int i = from; i < args.Length - 1; i++)
        if (args[i] == flag)
            return args[i + 1];
    return null;
}

static void PrintSummary(TextWriter o, string left, string right, CompareReport r)
{
    o.WriteLine($"compare: {left}  vs  {right}");
    o.WriteLine($"  files      {r.Total}");
    o.WriteLine($"  identical  {r.Count(ChangeStatus.Identical)}   (hidden)");
    o.WriteLine($"  added      {r.Count(ChangeStatus.Added)}");
    o.WriteLine($"  removed    {r.Count(ChangeStatus.Removed)}");
    o.WriteLine($"  renamed    {r.Count(ChangeStatus.Renamed)}");
    o.WriteLine(
        $"  modified   {r.Count(ChangeStatus.Modified)}   " +
        $"content {r.ReasonCount(ChangeReason.Content)} · eol {r.ReasonCount(ChangeReason.Eol)} · " +
        $"whitespace {r.ReasonCount(ChangeReason.Whitespace)} · encoding {r.ReasonCount(ChangeReason.Encoding)} · " +
        $"binary {r.ReasonCount(ChangeReason.Binary)}");
}

/// <summary>The per-file change list (non-patch mode): one line per non-identical path.</summary>
static void PrintChanges(CompareReport r)
{
    foreach (var c in r.Changes)
    {
        if (c.Status == ChangeStatus.Identical) continue; // hide identical by default (noise on big trees)
        var tag = c.Status switch
        {
            ChangeStatus.Added => "A",
            ChangeStatus.Removed => "D",
            ChangeStatus.Modified => "M",
            ChangeStatus.Renamed => "R",
            _ => "?",
        };
        var reason = c.ReasonLabel is { } rr ? $" [{rr}]" : "";
        var from = c.RenamedFrom is { } f ? $"{f} -> " : "";
        Console.WriteLine($"    {tag} {from}{c.RelativePath}{reason}");
    }
}

static int DiffFiles(string[] args)
{
    const string diffUsage = "usage: codediffer diff <left-file> <right-file> [-U N] [--literal]   (/dev/null for an absent side)";
    if (args.Length < 3 || !ArgsOk(args, 2, diffUsage, ["--literal"], ["-U"]))
    {
        if (args.Length < 3) Console.Error.WriteLine(diffUsage);
        return 64;
    }
    if (Directory.Exists(args[1]) || Directory.Exists(args[2]))
    {
        Console.Error.WriteLine("error: diff takes two files; for trees use `codediffer compare <left> <right> --patch`");
        return 64;
    }
    // A side that isn't there is an error, never "the whole other file added": an absent side is asked for by name.
    static bool Absent(string a) => a is "/dev/null" || a.Equals("NUL", StringComparison.OrdinalIgnoreCase);
    foreach (var a in args[1..3])
        if (!Absent(a) && !File.Exists(a))
        {
            Console.Error.WriteLine($"error: {a} not found (for an added or deleted file, give /dev/null as the absent side)");
            return 2;
        }
    var left = Absent(args[1]) ? null : args[1];
    var right = Absent(args[2]) ? null : args[2];
    if (left is null && right is null)
    {
        Console.Error.WriteLine("error: both sides are absent");
        return 64;
    }
    if (!TryPatchOptions(args, out var popt)) return 64;
    using var stdout = PatchStdout();
    // One name for both sides — the file the patch changes (the right one's): two names would make git apply rename it.
    var name = Path.GetFileName(right ?? left!);
    PatchWriter.WriteFiles(stdout, left, right, name, name, popt);
    return 0;
}

static bool TryPatchOptions(string[] args, out PatchOptions options)
{
    int context = PatchOptions.DefaultContextLines;
    if (FlagValue(args, "-U") is { } u && (!int.TryParse(u, out context) || context < 0))
    {
        Console.Error.WriteLine("error: -U needs a non-negative integer");
        options = new PatchOptions();
        return false;
    }
    options = new PatchOptions { Context = context, Literal = args.Contains("--literal") };
    return true;
}

/// <summary>stdout as UTF-8 (no BOM) with '\n' line ends — a patch must be byte-exact, not console-encoded.</summary>
static StreamWriter PatchStdout()
    => new(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false), 1 << 16) { NewLine = "\n" };

static string Version()
    => Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
       ?? "0.0.0";

/// <summary>apply: port left->right onto a third tree (dry run unless --write). Exit 1 when anything conflicts.</summary>
static int Apply(string[] args)
{
    const string applyUsage = "usage: codediffer apply <left> <right> <target> [--write] [--threads N] [--no-cache]";
    if (args.Length < 4 || !ArgsOk(args, 3, applyUsage, ["--write", "--no-cache"], ["--threads"]))
    {
        if (args.Length < 4) Console.Error.WriteLine(applyUsage);
        return 64;
    }
    int threads = new CompareOptions().Parallelism;
    if (FlagValue(args, "--threads") is { } t && (!int.TryParse(t, out threads) || threads < 1))
    {
        Console.Error.WriteLine("error: --threads needs a positive integer");
        return 64;
    }
    var options = new CompareOptions { Parallelism = threads, Cache = args.Contains("--no-cache") ? CacheMode.Off : CacheMode.On };
    PortResult result;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var stop = new CancellationTokenSource();
    try
    {
        var progress = new CompareProgress();
        var report = WithProgress(() => new DirectoryComparer(options).Compare(args[1], args[2], progress, ct: stop.Token),
            () => ProgressView.Line(progress), stop.Cancel);
        if (report.LeftDroppedDirectories + report.RightDroppedDirectories > 0)
        {
            Console.Error.WriteLine("error: incomplete walk (unlistable directories) — refusing to port a partial change set");
            return 3;
        }
        if (report.UnreadableFiles > 0)
            Console.Error.WriteLine($"note: {report.UnreadableFiles} file(s) could not be read during the compare; each is tried again " +
                "below and reported if it still can't be read");
        // Ctrl+C here stops between files (each is written whole); a second one still quits at once, and even then
        // no file is torn and running the apply again finishes it.
        bool write = args.Contains("--write");
        var port = new PortProgress();
        result = WithProgress(() => ChangePorter.Run(report, args[1], args[2], args[3], write, parallelism: threads, ct: stop.Token, progress: port),
            () => $"{(write ? "applying" : "checking")}: {port.Done:N0} / {port.Total:N0} changed files", stop.Cancel);
    }
    catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
    catch (OperationCanceledException) { return Cancelled(sw.Elapsed, options.Cache); }
    Console.Write(AgentViews.PortText(result, $"{args[1]} -> {args[2]}", null, maxFiles: int.MaxValue, writeHint: "run again with --write"));
    return result.Cancelled ? 130 : result.Count(PortStatus.Conflict) > 0 ? 1 : 0;
}

/// <summary>apply-overlay: apply a compare3 --merge-out overlay to v1 (dry run unless --write). Exit 1 when a file failed.</summary>
static int ApplyOverlay(string[] args)
{
    const string overlayUsage = "usage: codediffer apply-overlay <overlay-dir> <target> [--write] [--again] [--threads N]";
    if (args.Length < 3 || args[1].StartsWith("--") || args[2].StartsWith("--"))
    {
        Console.Error.WriteLine(overlayUsage);
        return 64;
    }
    if (!ArgsOk(args, 2, overlayUsage, ["--write", "--again"], ["--threads"])) return 64;
    int threads = new CompareOptions().Parallelism;
    if (FlagValue(args, "--threads", from: 3) is { } t && (!int.TryParse(t, out threads) || threads < 1))
    {
        Console.Error.WriteLine("error: --threads needs a positive integer");
        return 64;
    }
    bool write = args.Contains("--write");
    var progress = new PortProgress();
    using var stop = new CancellationTokenSource();
    OverlayApplyResult r;
    try
    {
        r = WithProgress(() => OverlayApplier.Run(args[1], args[2], write, args.Contains("--again"), threads, stop.Token, progress),
            () => $"{(write ? "applying" : "checking")}: {progress.Done:N0} / {progress.Total:N0}", stop.Cancel);
    }
    catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
    Console.Write(OverlayApplier.Text(r));
    return r.Cancelled ? 130 : r.Failed.Count > 0 ? 1 : 0;
}

/// <summary>compare3: base vs v1 vs v2. Exit 1 when anything conflicts, 3 on an incomplete walk.</summary>
static int Compare3(string[] args)
{
    const string compare3Usage = "usage: codediffer compare3 <base> <v1> <v2> [--all] [--threads N] [--no-cache] [--merge-out DIR] [--no-save] " +
                                 "[--html [--large] [--include-identical] [--max-diffs N] [--out DIR]]";
    if (args.Length < 4 || !ArgsOk(args, 3, compare3Usage,
            ["--all", "--no-cache", "--no-save", "--html", "--large", "--include-identical"], ["--threads", "--merge-out", "--max-diffs", "--out"]))
    {
        if (args.Length < 4) Console.Error.WriteLine(compare3Usage);
        return 64;
    }
    int threads = new CompareOptions().Parallelism;
    if (FlagValue(args, "--threads") is { } t && (!int.TryParse(t, out threads) || threads < 1))
    {
        Console.Error.WriteLine("error: --threads needs a positive integer");
        return 64;
    }
    var options = new CompareOptions { Parallelism = threads, Cache = args.Contains("--no-cache") ? CacheMode.Off : CacheMode.On };
    if (args.Contains("--html") && !MaxDiffsOk(args, out _)) return 64;
    var store = new SessionStore(save: !args.Contains("--no-save"));
    var outDir = FlagValue(args, "--merge-out", from: 4);
    Compare3Session s;
    try
    {
        if (outDir is not null) MergeOverlay.CheckDir(outDir, args[1], args[2], args[3]); // before a compare that may take an hour
        s = store.Start3(args[1], args[2], args[3], options);
    }
    catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
    WithProgress(() => s.Wait(Timeout.InfiniteTimeSpan), () =>
        s.Progress1!.Phase != ComparePhase.Done ? "base->v1: " + ProgressView.Line(s.Progress1)
        : s.Progress2!.Phase != ComparePhase.Done ? "base->v2: " + ProgressView.Line(s.Progress2)
        : "classifying and merging the paths both sides touched", () => s.Cancel());
    if (s.Cancelled) return Cancelled(s.Elapsed, options.Cache);
    if (s.Error is { } err)
    {
        Console.Error.WriteLine($"error: {err}");
        return 2;
    }
    // The paths both sides touched (or every touched path with --all), then the summary.
    var r = s.Report!;
    bool all = args.Contains("--all");
    foreach (var e in r.Entries)
        if (all || e.Outcome is not (Merge3Outcome.V1Only or Merge3Outcome.V2Only))
            Console.WriteLine("  " + ThreeWayViews.Line(e));
    Console.Write(ThreeWayViews.Stats(s));
    int? failed = null;
    if (args.Contains("--html"))
    {
        if (s.ResultDir is null || s.SaveError is not null) Console.Error.WriteLine("note: --html needs a saved result; drop --no-save");
        else failed = WriteHtml(s, args, Console.Out);
    }
    if (outDir is not null)
    {
        try
        {
            var o = MergeOverlay.Write(r, s.Base, s.V1, s.V2, outDir, threads);
            Console.Write(ThreeWayViews.OverlayText(o));
            if (o.Failed > 0)
            {
                Console.Error.WriteLine($"error: merge overlay: {o.Failed:N0} file(s) could not be written (listed in conflicts.txt)");
                failed = 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: merge overlay: {ex.Message}");
            failed = 2;
        }
    }
    if (failed is { } code) return code;
    if (r.DroppedDirectories + r.UnreadableFiles > 0) return 3;
    return r.Count(Merge3Outcome.Conflict) > 0 ? 1 : 0;
}

/// <summary>
/// Run <paramref name="work"/> while showing <paramref name="line"/> on stderr: redrawn in place twice a second on a
/// console, or written as a plain line every 30 s when stderr is redirected (a log). Nothing for a quick run.
/// The first Ctrl+C calls <paramref name="cancel"/> (the work then stops after saving the hashes it read); a second
/// one ends the process at once.
/// </summary>
static T WithProgress<T>(Func<T> work, Func<string> line, Action? cancel = null)
{
    bool cancelling = false;
    ConsoleCancelEventHandler? onCtrlC = cancel is null ? null : (_, e) =>
    {
        if (cancelling) return; // second Ctrl+C: let it end the process
        cancelling = true;
        e.Cancel = true;
        Console.Error.WriteLine("\ncancelling (Ctrl+C again to quit at once)");
        cancel();
    };
    if (onCtrlC is not null) Console.CancelKeyPress += onCtrlC;
    try { return Show(Task.Run(work), line); }
    finally { if (onCtrlC is not null) Console.CancelKeyPress -= onCtrlC; }
}

static T Show<T>(Task<T> task, Func<string> line)
{
    bool console = !Console.IsErrorRedirected;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var logged = TimeSpan.Zero;
    int drawn = 0;
    // Wait on the handle, not task.Wait: that throws an AggregateException when the work fails within the first tick
    // (a bad path, a refused argument), which no caller's catch matches — a stack trace instead of the error.
    while (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(console ? 500 : 1000))
    {
        var t = sw.Elapsed;
        var text = $"{(t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"))}  {line()}";
        if (console)
        {
            int width;
            try { width = Math.Max(20, Console.WindowWidth - 1); } catch (IOException) { width = 119; }
            if (text.Length > width) text = text[..(width - 1)] + "…";
            Console.Error.Write("\r" + text.PadRight(drawn));
            drawn = text.Length;
        }
        else if (t - logged >= TimeSpan.FromSeconds(30))
        {
            Console.Error.WriteLine(text);
            logged = t;
        }
    }
    if (drawn > 0) Console.Error.Write("\r" + new string(' ', drawn) + "\r");
    return task.GetAwaiter().GetResult(); // the work's own exception, not an AggregateException
}

/// <summary>Report a run stopped by Ctrl+C; exit code 130 (the shell convention for SIGINT).</summary>
static int Cancelled(TimeSpan elapsed, CacheMode cache)
{
    Console.Error.WriteLine($"cancelled after {ProgressView.Clock(elapsed)}: " + (cache == CacheMode.Off
        ? "nothing was cached (--no-cache), so running it again reads everything again"
        : "the hashes read so far are kept, so running it again only reads the rest"));
    return 130;
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        codediffer — large-tree 2-/3-way diff (scaffold)

        usage:
          codediffer version                   print version
          codediffer compare <left> <right> [--threads N] [--no-cache | --rehash] [--fast-stat]
                                       [--patch [-U N] [--literal]]
                                       compare two trees (parallel; default min(cores,8)).
                                       Hash cache ON by default: a file whose path+size+mtime
                                       match a trusted ledger entry isn't re-read. --no-cache
                                       proves every byte; --rehash re-reads and refreshes it.
                                       Cache hits are confirmed by a live per-file stat;
                                       --fast-stat trusts the directory listing alone.
                                       --patch writes a git-style unified diff to stdout
                                       (git apply-able; summary goes to stderr).
                                       The result is saved (see `results`; --no-save skips);
                                       --html also writes the HTML report (report flags below).
          codediffer compare3 <base> <v1> <v2> [--all] [--threads N] [--no-cache] [--no-save] [--html]
                              [--merge-out DIR]
                                       3-way: v1 only | v2 only | agreed | merged | conflict
                                       --merge-out writes the merge as an overlay on v1 (new or
                                       empty DIR): deletes.txt to apply to v1 first, then files\
                                       to copy over it (text conflicts with diff3 markers),
                                       conflicts.txt, OVERLAY.txt.
          codediffer report <id|result-dir> [--large] [--include-identical] [--max-diffs N] [--out DIR]
                                       HTML report of a saved compare: folder tree, filters,
                                       each file's diff loaded on expand; opens from disk.
                                       --large also block-diffs files over 16 MB. --out must
                                       be new, empty or an earlier report (it is replaced),
                                       and outside the compared trees. Ctrl+C stops rendering
                                       and still writes the report (the rest without a diff).
          codediffer results [--max N]   saved compares, newest first
          codediffer results --prune [--keep N] [--older-than DAYS] [--yes]
                                       delete all but the newest N (default 20) saved compares,
                                       only those older than DAYS if given. Dry run unless --yes.
          Ctrl+C stops a running compare/compare3/apply; the hashes read so far are kept.
          codediffer diff <a> <b> [-U N] [--literal]
                                       unified diff of two files
          codediffer apply <left> <right> <target> [--write]
                                       port the left->right changes onto target by 3-way
                                       merge; per hunk applied|fuzzy|already|conflict.
                                       Dry run unless --write (conflicted files untouched).
                                       Ctrl+C during --write stops between files; run it
                                       again to finish (done files come out "already").
          codediffer apply-overlay <overlay-dir> <target> [--write] [--again]
                                       apply a compare3 --merge-out overlay to v1 (or a copy):
                                       deletes.txt, then the directories the deletes emptied,
                                       then files\. Dry run unless --write; Ctrl+C stops
                                       between files, run again to finish (also after a
                                       failure). Refuses a second finished apply to the same
                                       target unless --again (it would overwrite conflicts
                                       resolved since).
          codediffer blockdiff <a> <b>         content-defined block diff of two large files
                                               (bounded memory; reports changed byte ranges)
          codediffer verify <delta.json> [--base <dir> --variant <dir>]
                                               reproduce a delta's diffTruthSha; with trees,
                                               also assert CodeDiffer's hunks match the manifest
                                               and that its own compare reports exactly the
                                               manifest's files, reasons and renames
          codediffer verify <conflict.json> [--base <B> --v1 <dir> --v2 <dir>]
                                               reproduce a 3-way conflictTruthSha; with trees,
                                               also assert CodeDiffer's 3-way merge decomposition
                                               (or reconstruction + the same conflicted files)
          codediffer help                      this help

        exit codes: 0 done · 1 conflicts / a file failed / a verify gate failed · 2 error
                    3 done but incomplete (unlistable directory or unreadable file) · 64 usage · 130 Ctrl+C
        """);
}
