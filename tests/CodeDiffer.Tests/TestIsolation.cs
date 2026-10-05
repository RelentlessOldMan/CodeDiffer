using System.Runtime.CompilerServices;

namespace CodeDiffer.Tests;

/// <summary>
/// The hash cache is ON by default, so without this every compare test would write ledgers into the real
/// %LOCALAPPDATA%\CodeDiffer and read the user's real CodeCompass ledgers. Point both at a per-run temp dir.
/// </summary>
internal static class TestIsolation
{
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cd-tests-cache-" + Environment.ProcessId);
        Environment.SetEnvironmentVariable("CODEDIFFER_CACHE_DIR", Path.Combine(dir, "differ"));
        Environment.SetEnvironmentVariable("CODECOMPASS_CACHE_DIR", Path.Combine(dir, "compass"));
    }
}
