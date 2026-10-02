using System.Reflection;
using CodeDiffer.Core.Compare;

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

    try
    {
        InputValidation.ValidateTrees(args[1], args[2]);
    }
    catch (Exception ex)
    {
        // Honesty contract: a bad input is a loud error, never a silent all-added/all-deleted "success".
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }

    Console.WriteLine($"compare: {args[1]}  vs  {args[2]}");
    Console.WriteLine("engine not yet implemented (scaffold). See DESIGN.txt / docs/OUTPUT.md.");
    return 0;
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
