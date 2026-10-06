using CodeDiffer.Core.Compare;

namespace CodeDiffer.Core.Sessions;

/// <summary>The one-line text of a running compare's progress (the agent views and the CLI's progress line).</summary>
public static class ProgressView
{
    /// <param name="found">Append how many differences are found so far.</param>
    public static string Line(CompareProgress p, bool found = true)
    {
        var now = p.Phase switch
        {
            ComparePhase.Starting => "starting",
            ComparePhase.Walking => $"listing files · {p.ListedLeft:N0} left · {p.ListedRight:N0} right",
            ComparePhase.Pairing => "pairing files by path",
            ComparePhase.Contents => Contents(p),
            ComparePhase.Renames => "matching renames",
            ComparePhase.Saving => "saving the hash cache",
            _ => "finishing",
        };
        return !found || p.Phase < ComparePhase.Pairing ? now : $"{now} · {p.FoundCount:N0} difference(s) found so far";
    }

    private static string Contents(CompareProgress p)
    {
        var s = $"checking contents · {p.PairsDone:N0}/{p.SameSizePairs:N0} same-size files";
        double secs = p.ContentElapsed.TotalSeconds;
        long read = p.BytesRead;
        // Bytes checked only mean something while files are being read: biggest go first, and from the cache
        // they finish at once, so a warm run would show "94 of 94 GB" with most files still to go.
        if (read > 0) s += $" · {AgentViews.Bytes(p.BytesDone)} of {AgentViews.Bytes(p.SameSizeBytes)}";
        s += $" · {AgentViews.Bytes(read)} read";
        if (read > 0 && secs >= 1) s += $" at {read / secs / (1024 * 1024):F0} MB/s";
        if (p.CacheSides > 0) s += $" · {p.CacheSides:N0} from cache";
        if (p.Remaining() is { } left) s += $" · about {Clock(left)} left";
        return s;
    }

    /// <summary>m:ss, or h:mm:ss with the TOTAL hours (a TimeSpan's "h" would drop whole days).</summary>
    public static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(long)t.TotalHours}:{t:mm\\:ss}" : t.ToString(@"m\:ss");
}
