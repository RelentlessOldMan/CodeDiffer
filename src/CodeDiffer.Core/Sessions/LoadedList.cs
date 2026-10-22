namespace CodeDiffer.Core.Sessions;

/// <summary>
/// A test hook: with CODEDIFFER_LOADED_LIST set to a file, the process writes the names of the .NET libraries it loaded
/// there, one a line, as it exits. The tests run the real exes with it to see that a run never loaded the networking
/// code: nothing that never loads it can reach the internet, whatever a library would like to look up.
/// </summary>
public static class LoadedList
{
    public const string Variable = "CODEDIFFER_LOADED_LIST";

    public static void WriteOnExitIfAsked()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } path) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { File.WriteAllLines(path, AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "?")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // the test then finds no list and fails
        };
    }
}
