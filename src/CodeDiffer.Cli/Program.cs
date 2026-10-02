using System.Reflection;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Model;
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
        Console.Error.WriteLine("usage: codediffer compare <left-tree> <right-tree>");
        return 64;
    }

    CompareReport report;
    try
    {
        report = new DirectoryComparer().Compare(args[1], args[2]);
    }
    catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException)
    {
        // Honesty contract: a bad input is a loud error, never a silent all-added/all-deleted "success".
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }

    PrintSummary(args[1], args[2], report);
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
    return v.Ok ? 0 : 1;
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

static void PrintSummary(string left, string right, CompareReport r)
{
    Console.WriteLine($"compare: {left}  vs  {right}");
    Console.WriteLine($"  files      {r.Total}");
    Console.WriteLine($"  identical  {r.Count(ChangeStatus.Identical)}   (hidden)");
    Console.WriteLine($"  added      {r.Count(ChangeStatus.Added)}");
    Console.WriteLine($"  removed    {r.Count(ChangeStatus.Removed)}");
    Console.WriteLine(
        $"  modified   {r.Count(ChangeStatus.Modified)}   " +
        $"content {r.ReasonCount(ChangeReason.Content)} · eol {r.ReasonCount(ChangeReason.Eol)} · " +
        $"whitespace {r.ReasonCount(ChangeReason.Whitespace)} · encoding {r.ReasonCount(ChangeReason.Encoding)} · " +
        $"binary {r.ReasonCount(ChangeReason.Binary)}");

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
        Console.WriteLine($"    {tag} {c.RelativePath}{reason}");
    }
}

static string Version()
    => Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
       ?? "0.0.0";

static void PrintUsage()
{
    Console.WriteLine(
        """
        codediffer — large-tree 2-/3-way diff (scaffold)

        usage:
          codediffer version                   print version
          codediffer compare <left> <right>    compare two trees (engine WIP)
          codediffer verify <delta.json> [--base <dir> --variant <dir>]
                                               reproduce a delta's diffTruthSha; with trees,
                                               also assert CodeDiffer's hunks match the manifest
          codediffer help                      this help
        """);
}
