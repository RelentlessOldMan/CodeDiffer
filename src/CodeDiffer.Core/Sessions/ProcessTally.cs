using System.Collections.Concurrent;

namespace CodeDiffer.Core.Sessions;

/// <summary>
/// Counts by key for the whole process, for a run record to show what a run did underneath: every exception thrown
/// (caught or not; by type and Windows error code, never its message) and which way each live stat went
/// (<see cref="Walk.FileStat"/>). A record takes a <see cref="Snapshot"/> as it starts and writes the difference, so a
/// server that runs compares one after another shows each one's own; compares that overlap each count the others' too
/// (the counts are the process's, not a compare's).
/// </summary>
public static class ProcessTally
{
    private static readonly ConcurrentDictionary<string, long> s_exceptions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> s_stats = new(StringComparer.Ordinal);
    private static int s_installed;
    [ThreadStatic] private static bool t_counting;

    /// <summary>Start counting exceptions (once per process; later calls do nothing).</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref s_installed, 1) != 0) return;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (t_counting) return; // counting threw (out of memory): never count the counting
            t_counting = true;
            try { Add(s_exceptions, Key(e.Exception)); }
            finally { t_counting = false; }
        };
    }

    /// <summary>The type, and the Windows error code when a file call's exception carries one. Only those: every
    /// ArgumentException's HResult (E_INVALIDARG) reads as Windows error 87 too, and isn't one. A Win32Exception's code is its
    /// own (its HResult is E_FAIL).</summary>
    internal static string Key(Exception ex) => ex switch
    {
        System.ComponentModel.Win32Exception w => $"{ex.GetType().Name} {w.NativeErrorCode}",
        IOException or UnauthorizedAccessException when (ex.HResult >> 16 & 0x1FFF) == 7 /* FACILITY_WIN32 */
            => $"{ex.GetType().Name} {ex.HResult & 0xFFFF}",
        _ => ex.GetType().Name,
    };

    internal static void Stat(string way) => Add(s_stats, way);

    private static void Add(ConcurrentDictionary<string, long> d, string k) => d.AddOrUpdate(k, 1, (_, v) => v + 1);

    public static (Dictionary<string, long> Exceptions, Dictionary<string, long> Stats) Snapshot()
        => (new(s_exceptions, StringComparer.Ordinal), new(s_stats, StringComparer.Ordinal));

    /// <summary>What <paramref name="now"/> counts beyond <paramref name="since"/>, the keys that grew only.</summary>
    public static IEnumerable<(string Key, long Count)> Since(Dictionary<string, long> now, Dictionary<string, long> since)
        => now.Select(kv => (kv.Key, kv.Value - since.GetValueOrDefault(kv.Key))).Where(x => x.Item2 > 0).OrderBy(x => x.Key, StringComparer.Ordinal);
}
