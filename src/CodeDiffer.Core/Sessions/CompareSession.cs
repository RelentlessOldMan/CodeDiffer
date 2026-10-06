using System.Collections.Concurrent;
using System.Diagnostics;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.ThreeWay;

namespace CodeDiffer.Core.Sessions;

/// <summary>Per-file line counts from a rendered patch section (computed on demand, then remembered).</summary>
public readonly record struct FileDiffInfo(int Hunks, int AddedLines, int RemovedLines, string Kind);

/// <summary>
/// A compare run in the background: the agent gets an id at once and queries it while (and after) it runs —
/// the output is a session, not a document (docs/OUTPUT.md §1). Results stay in memory for the life of the
/// server.
/// </summary>
public abstract class Session
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _finished;

    protected Session(string id, CompareOptions options)
    {
        Id = id;
        Options = options;
    }

    public string Id { get; }
    public CompareOptions Options { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public abstract Task Task { get; }

    /// <summary>One line naming what is compared (for summaries and error messages).</summary>
    public abstract string Title { get; }

    public bool IsDone => Task.IsCompleted;
    public TimeSpan Elapsed => _finished ?? _clock.Elapsed;

    /// <summary>The failure message when the compare threw, else null.</summary>
    public string? Error => Task.IsFaulted ? (Task.Exception!.InnerException ?? Task.Exception).Message : null;

    /// <summary>Wait up to <paramref name="timeout"/> for the compare to finish; true when it has.</summary>
    public bool Wait(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) return IsDone;
        try { return Task.Wait(timeout); }
        catch (AggregateException) { return true; } // finished, with an error the views report
    }

    protected Task<T> Run<T>(Func<T> work) => System.Threading.Tasks.Task.Run(() =>
    {
        try { return work(); }
        finally { _finished = _clock.Elapsed; }
    });
}

/// <summary>A 2-way compare (left vs right). Per-file diffs are rendered lazily and their line counts remembered.</summary>
public sealed class CompareSession : Session
{
    private readonly Task<CompareReport> _task;

    public string Left { get; }
    public string Right { get; }
    public override Task Task => _task;
    public override string Title => $"{Left}  vs  {Right}";

    /// <summary>Line counts for files whose diff has been rendered (get_file_diff / list_files lines=true).</summary>
    public ConcurrentDictionary<string, FileDiffInfo> DiffInfo { get; } = new(StringComparer.Ordinal);

    internal CompareSession(string id, string left, string right, CompareOptions options) : base(id, options)
    {
        Left = left;
        Right = right;
        _task = Run(() => new DirectoryComparer(options).Compare(left, right));
    }

    /// <summary>The report when finished successfully, else null.</summary>
    public CompareReport? Report => _task.IsCompletedSuccessfully ? _task.Result : null;
}

/// <summary>A 3-way compare (base, v1, v2): what each side changed, and whether the two merge.</summary>
public sealed class Compare3Session : Session
{
    private readonly Task<ThreeWayReport> _task;

    public string Base { get; }
    public string V1 { get; }
    public string V2 { get; }
    public override Task Task => _task;
    public override string Title => $"base {Base}  ·  v1 {V1}  ·  v2 {V2}";

    internal Compare3Session(string id, string baseDir, string v1, string v2, CompareOptions options) : base(id, options)
    {
        Base = baseDir;
        V1 = v1;
        V2 = v2;
        _task = Run(() => TreeMerger.Run(baseDir, v1, v2, options));
    }

    public ThreeWayReport? Report => _task.IsCompletedSuccessfully ? _task.Result : null;
}

/// <summary>
/// The server's compares, by id. Keeps the most recent few (a finished report holds every path of the
/// trees, so an unbounded store would grow with each compare of a 50k-file tree).
/// </summary>
public sealed class SessionStore
{
    public const int DefaultCapacity = 8;

    private readonly object _gate = new();
    private readonly List<Session> _sessions = [];
    private readonly int _capacity;

    public SessionStore(int capacity = DefaultCapacity) => _capacity = Math.Max(1, capacity);

    /// <summary>Validate both roots (a loud error, never a silent all-added "success"), then start in the background.</summary>
    public CompareSession Start(string left, string right, CompareOptions? options = null)
    {
        left = Path.GetFullPath(left);
        right = Path.GetFullPath(right);
        InputValidation.ValidateTrees(left, right);
        return (CompareSession)Add(id => new CompareSession(id, left, right, options ?? new CompareOptions()));
    }

    /// <summary>Start a 3-way compare; all three roots are validated first.</summary>
    public Compare3Session Start3(string baseDir, string v1, string v2, CompareOptions? options = null)
    {
        baseDir = Path.GetFullPath(baseDir);
        v1 = Path.GetFullPath(v1);
        v2 = Path.GetFullPath(v2);
        InputValidation.RequireExistingDirectory(baseDir, "base");
        InputValidation.RequireExistingDirectory(v1, "v1");
        InputValidation.RequireExistingDirectory(v2, "v2");
        return (Compare3Session)Add(id => new Compare3Session(id, baseDir, v1, v2, options ?? new CompareOptions()));
    }

    private Session Add(Func<string, Session> make)
    {
        lock (_gate)
        {
            string id;
            do id = Convert.ToHexString(BitConverter.GetBytes(Random.Shared.Next())).ToLowerInvariant()[..4];
            while (_sessions.Any(s => s.Id == id));
            var session = make(id);
            _sessions.Add(session);
            // Evict the oldest FINISHED compares past capacity; a running one is never dropped.
            while (_sessions.Count > _capacity && _sessions.FirstOrDefault(s => s.IsDone && s != session) is { } old)
                _sessions.Remove(old);
            return session;
        }
    }

    /// <summary>The compare with this id, or the most recent one when id is null/empty.</summary>
    public Session? Get(string? id)
    {
        lock (_gate)
            return string.IsNullOrWhiteSpace(id)
                ? _sessions.LastOrDefault()
                : _sessions.FirstOrDefault(s => string.Equals(s.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<Session> All()
    {
        lock (_gate) return [.. _sessions];
    }
}
