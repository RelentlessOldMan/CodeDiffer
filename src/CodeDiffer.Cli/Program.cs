using System.Reflection;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Giant;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Port;
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
        case "compare3":
            return Compare3(args);
        case "verify":
            return Verify(args);
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
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: codediffer compare <left-tree> <right-tree> [--threads N] [--no-cache | --rehash] [--fast-stat] [--patch [-U N] [--literal]]");
        return 64;
    }

    int threads = new CompareOptions().Parallelism;
    if (FlagValue(args, "--threads") is { } t && (!int.TryParse(t, out threads) || threads < 1))
    {
        Console.Error.WriteLine("error: --threads needs a positive integer");
        return 64;
    }
    var cache = args.Contains("--no-cache") ? CacheMode.Off : args.Contains("--rehash") ? CacheMode.Rehash : CacheMode.On;
    var options = new CompareOptions { Parallelism = threads, Cache = cache, StrictStat = !args.Contains("--fast-stat") };

    CompareReport report;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        report = new DirectoryComparer(options).Compare(args[1], args[2]);
    }
    catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException)
    {
        // Honesty contract: a bad input is a loud error, never a silent all-added/all-deleted "success".
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }

    if (args.Contains("--timings"))
        foreach (var (phase, elapsed) in report.Timings)
            Console.Error.WriteLine($"  timing     {phase,-28} {elapsed.TotalMilliseconds,9:N0} ms");

    // --patch: the patch goes to stdout (pipe it to a file or `git apply`), the summary to stderr.
    bool patch = args.Contains("--patch");
    var info = patch ? Console.Error : Console.Out;
    if (patch)
    {
        if (!TryPatchOptions(args, out var popt)) return 64;
        using var stdout = PatchStdout();
        var ps = PatchWriter.Write(stdout, report, args[1], args[2], popt);
        stdout.Flush();
        info.WriteLine($"patch: {ps.TextFiles} text · {ps.BinaryFiles} binary · {ps.GiantFiles} large (block ranges) · {ps.NoteFiles} eol/encoding note(s)" +
            (ps.CoarseFiles > 0 ? $" · {ps.CoarseFiles} coarse (edit-distance budget exceeded)" : ""));
    }
    else
    {
        PrintChanges(report);
    }

    PrintSummary(info, args[1], args[2], report);
    info.WriteLine($"  elapsed    {sw.Elapsed.ToString(@"hh\:mm\:ss")}  (threads {options.Parallelism}, cache {cache.ToString().ToLowerInvariant()})");
    info.WriteLine($"  content    {report.ComparedPairs} same-size pair(s) · {report.CacheHits} side(s) from hash cache" +
        (report.CodeCompassHits > 0 ? $" ({report.CodeCompassHits} via CodeCompass)" : "") +
        $" · {report.BytesRead / (1024.0 * 1024 * 1024):F1} GB read" +
        (report.PendingFiles > 0 ? $" · {report.PendingFiles} too recently modified to cache yet" : ""));
    if (report.UnstableFiles > 0)
        Console.Error.WriteLine($"note: {report.UnstableFiles} file(s) changed while being read (live writer) — compared as read, not cached.");
    if (report.LeftDroppedDirectories + report.RightDroppedDirectories > 0)
    {
        Console.Error.WriteLine(
            $"WARNING: incomplete walk — {report.LeftDroppedDirectories} left / {report.RightDroppedDirectories} right " +
            "director(ies) could not be listed; adds/removes under them may be listing failures.");
        return 3;
    }
    return 0;
}

static int BlockDiff(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: codediffer blockdiff <left-file> <right-file>");
        return 64;
    }
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
        Console.Error.WriteLine("usage: codediffer verify <delta.json>");
        return 64;
    }

    // Route on deltaKind: a 3-way artifact carries conflictTruthSha, not diffTruthSha.
    string deltaKind;
    try
    {
        deltaKind = DeltaKind(args[1]);
    }
    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }

    if (deltaKind == "conflict-3way")
        return VerifyConflict(args);

    DeltaManifest manifest;
    try
    {
        manifest = DeltaManifestParser.ParseFile(args[1]);
        DeltaVerifier.AssertSupportedVersion(manifest);
    }
    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or NotSupportedException or FormatException)
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
        Console.WriteLine($"  hunk cross-check: {cc.Reconstructed}/{cc.Checked} reconstructed · exact {cc.ExactMatches}/{cc.Checked} · {cc.Skipped} skipped (binary/eol/encoding/giant)");
        foreach (var f in cc.Files.Where(f => f.Checked && !f.Reconstructs))
            Console.WriteLine($"    MISMATCH {f.Path} (manifest hunks do not rebuild the variant)");
        Console.WriteLine(cc.Ok ? "  OK — hunks reconstruct the variant" : "  FAIL — a manifest hunk set does not rebuild the variant");
        crossOk = cc.Ok;
    }

    return v.Ok && crossOk ? 0 : 1;
}

static int VerifyConflict(string[] args)
{
    ConflictManifest manifest;
    try
    {
        manifest = ConflictManifestParser.ParseFile(args[1]);
    }
    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or FormatException)
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
        Console.WriteLine(cc.Ok
            ? "  OK — 3-way verified (decomposition match and/or reconstruction)"
            : "  FAIL — neither my merge nor reconstruction confirms the manifest");
        mergeOk = cc.Ok;
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

static string? FlagValue(string[] args, string flag)
{
    for (int i = 2; i < args.Length - 1; i++)
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
        var reason = c.Reason is { } rr ? $" [{CanonicalTokens.Token(rr)}]" : "";
        var from = c.RenamedFrom is { } f ? $"{f} -> " : "";
        Console.WriteLine($"    {tag} {from}{c.RelativePath}{reason}");
    }
}

static int DiffFiles(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: codediffer diff <left-file> <right-file> [-U N] [--literal]");
        return 64;
    }
    if (Directory.Exists(args[1]) || Directory.Exists(args[2]))
    {
        Console.Error.WriteLine("error: diff takes two files; for trees use `codediffer compare <left> <right> --patch`");
        return 64;
    }
    var left = File.Exists(args[1]) ? args[1] : null;
    var right = File.Exists(args[2]) ? args[2] : null;
    if (left is null && right is null)
    {
        Console.Error.WriteLine($"error: neither {args[1]} nor {args[2]} exists");
        return 2;
    }
    if (!TryPatchOptions(args, out var popt)) return 64;
    using var stdout = PatchStdout();
    PatchWriter.WriteFiles(stdout, left, right, Path.GetFileName(args[1]), Path.GetFileName(args[2]), popt);
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
    if (args.Length < 4)
    {
        Console.Error.WriteLine("usage: codediffer apply <left> <right> <target> [--write] [--threads N] [--no-cache]");
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
    try
    {
        var report = new DirectoryComparer(options).Compare(args[1], args[2]);
        if (report.LeftDroppedDirectories + report.RightDroppedDirectories > 0)
        {
            Console.Error.WriteLine("error: incomplete walk (unlistable directories) — refusing to port a partial change set");
            return 3;
        }
        result = ChangePorter.Run(report, args[1], args[2], args[3], args.Contains("--write"), parallelism: threads);
    }
    catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
    Console.Write(AgentViews.PortText(result, $"{args[1]} -> {args[2]}", null, maxFiles: int.MaxValue));
    return result.Count(PortStatus.Conflict) > 0 ? 1 : 0;
}

/// <summary>compare3: base vs v1 vs v2. Exit 1 when anything conflicts, 3 on an incomplete walk.</summary>
static int Compare3(string[] args)
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("usage: codediffer compare3 <base> <v1> <v2> [--all] [--threads N] [--no-cache]");
        return 64;
    }
    int threads = new CompareOptions().Parallelism;
    if (FlagValue(args, "--threads") is { } t && (!int.TryParse(t, out threads) || threads < 1))
    {
        Console.Error.WriteLine("error: --threads needs a positive integer");
        return 64;
    }
    var options = new CompareOptions { Parallelism = threads, Cache = args.Contains("--no-cache") ? CacheMode.Off : CacheMode.On };
    var store = new SessionStore();
    Compare3Session s;
    try { s = store.Start3(args[1], args[2], args[3], options); }
    catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
    s.Wait(Timeout.InfiniteTimeSpan);
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
    if (r.DroppedDirectories > 0) return 3;
    return r.Count(Merge3Outcome.Conflict) > 0 ? 1 : 0;
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
          codediffer diff <a> <b> [-U N] [--literal]
                                       unified diff of two files
          codediffer apply <left> <right> <target> [--write]
                                       port the left->right changes onto target by 3-way
                                       merge; per hunk applied|fuzzy|already|conflict.
                                       Dry run unless --write (conflicted files untouched).
          codediffer blockdiff <a> <b>         content-defined block diff of two large files
                                               (bounded memory; reports changed byte ranges)
          codediffer verify <delta.json> [--base <dir> --variant <dir>]
                                               reproduce a delta's diffTruthSha; with trees,
                                               also assert CodeDiffer's hunks match the manifest
          codediffer verify <conflict.json> [--base <B> --v1 <dir> --v2 <dir>]
                                               reproduce a 3-way conflictTruthSha; with trees,
                                               also assert CodeDiffer's 3-way merge decomposition
          codediffer help                      this help
        """);
}
