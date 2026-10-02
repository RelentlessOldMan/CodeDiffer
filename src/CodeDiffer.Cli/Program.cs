using System.Reflection;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Model;

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
          codediffer help                      this help
        """);
}
