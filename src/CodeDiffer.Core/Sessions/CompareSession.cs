using System.Collections.Concurrent;
using System.Diagnostics;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Sessions;

/// <summary>Per-file line counts from a rendered patch section (computed on demand, then remembered).</summary>
public readonly record struct FileDiffInfo(int Hunks, int AddedLines, int RemovedLines, string Kind);

/// <summary>
/// One compare, run in the background: the agent gets an id at once and queries it while (and after) it
/// runs — the output is a session, not a document (docs/OUTPUT.md §1). Results stay in memory for the life
/// of the server; per-file diffs are rendered lazily and their line counts remembered.
/// </summary>
public sealed class CompareSession
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _finished;

    public string Id { get; }
    public string Left { get; }
    public string Right { get; }
    public CompareOptions Options { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public Task<CompareReport> Task { get; }

    /// <summary>Line counts for files whose diff has been rendered (get_file_diff / list_files lines=true).</summary>
    public ConcurrentDictionary<string, FileDiffInfo> DiffInfo { get; } = new(StringComparer.Ordinal);

    internal CompareSession(string id, string left, string right, CompareOptions options)
    {
        Id = id;
        Left = left;
        Right = right;
        Options = options;
        Task = System.Threading.Tasks.Task.Run(() =>
        {
            try { return new DirectoryComparer(options).Compare(left, right); }
            finally { _finished = _clock.Elapsed; }
        });
    }

    public bool IsDone => Task.IsCompleted;
    public TimeSpan Elapsed => _finished ?? _clock.Elapsed;

    /// <summary>The report when finished successfully, else null.</summary>
    public CompareReport? Report => Task.IsCompletedSuccessfully ? Task.Result : null;

    /// <summary>The failure message when the compare threw, else null.</summary>
    public string? Error => Task.IsFaulted ? (Task.Exception!.InnerException ?? Task.Exception).Message : null;

    /// <summary>Wait up to <paramref name="timeout"/> for the compare to finish; true when it has.</summary>
    public bool Wait(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) return IsDone;
        try { return Task.Wait(timeout); }
        catch (AggregateException) { return true; } // finished, with an error the views report
    }
}

/// <summary>
/// The server's compares, by id. Keeps the most recent few (a finished report holds every path of both
/// trees, so an unbounded store would grow with each compare of a 50k-file tree).
/// </summary>
public sealed class SessionStore
{
    public const int DefaultCapacity = 8;

    private readonly object _gate = new();
    private readonly List<CompareSession> _sessions = [];
    private readonly int _capacity;

    public SessionStore(int capacity = DefaultCapacity) => _capacity = Math.Max(1, capacity);

    /// <summary>Validate both roots (a loud error, never a silent all-added "success"), then start in the background.</summary>
    public CompareSession Start(string left, string right, CompareOptions? options = null)
    {
        left = Path.GetFullPath(left);
        right = Path.GetFullPath(right);
        InputValidation.ValidateTrees(left, right);
        lock (_gate)
        {
            string id;
            do id = Convert.ToHexString(BitConverter.GetBytes(Random.Shared.Next())).ToLowerInvariant()[..4];
            while (_sessions.Any(s => s.Id == id));
            var session = new CompareSession(id, left, right, options ?? new CompareOptions());
            _sessions.Add(session);
            // Evict the oldest FINISHED compares past capacity; a running one is never dropped.
            while (_sessions.Count > _capacity && _sessions.FirstOrDefault(s => s.IsDone && s != session) is { } old)
                _sessions.Remove(old);
            return session;
        }
    }

    /// <summary>The compare with this id, or the most recent one when id is null/empty.</summary>
    public CompareSession? Get(string? id)
    {
        lock (_gate)
            return string.IsNullOrWhiteSpace(id)
                ? _sessions.LastOrDefault()
                : _sessions.FirstOrDefault(s => string.Equals(s.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<CompareSession> All()
    {
        lock (_gate) return [.. _sessions];
    }
}
