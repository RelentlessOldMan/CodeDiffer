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
/// the output is a session, not a document (docs/OUTPUT.md §1). When it finishes it is saved to its result
/// directory (<see cref="ResultStore"/>), so it can be reopened by id later without re-comparing.
/// </summary>
public abstract class Session
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _finished;

    protected Session(string id, CompareOptions options, string? resultDir)
    {
        Id = id;
        Options = options;
        ResultDir = resultDir;
    }

    /// <summary>A session that already finished: reopened from its result directory, or run by the CLI itself.</summary>
    protected Session(string id, CompareOptions options, string? resultDir, DateTime startedUtc, TimeSpan elapsed, bool reopened)
        : this(id, options, resultDir)
    {
        StartedUtc = startedUtc;
        _finished = elapsed;
        Reopened = reopened;
    }

    public string Id { get; }
    public CompareOptions Options { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    /// <summary>Where this compare is saved (and its patches, apply reports and HTML report go); null = not saved.</summary>
    public string? ResultDir { get; }
    /// <summary>Loaded from disk rather than run in this process: the trees may have changed since.</summary>
    public bool Reopened { get; }
    /// <summary>Why saving the result failed (the compare itself still succeeded), else null.</summary>
    public string? SaveError { get; private set; }
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

    /// <summary>Run the compare in the background; on success save it (a save failure is recorded, not thrown).</summary>
    protected Task<T> Run<T>(Func<T> work) => System.Threading.Tasks.Task.Run(() =>
    {
        T result;
        try { result = work(); }
        catch (Exception ex)
        {
            _finished = _clock.Elapsed;
            if (ResultDir is not null) ResultStore.TryMarkFailed(this, ex);
            throw;
        }
        _finished = _clock.Elapsed;
        Persist(result);
        return result;
    });

    /// <summary>Save a just-finished result (also used by the CLI for a compare it ran itself).</summary>
    internal void Persist(object result)
    {
        if (ResultDir is null) return;
        try { ResultStore.Save(this, result); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = ex.Message;
        }
    }
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

    internal CompareSession(string id, string left, string right, CompareOptions options, string? resultDir) : base(id, options, resultDir)
    {
        Left = left;
        Right = right;
        _task = Run(() => new DirectoryComparer(options).Compare(left, right));
    }

    /// <summary>A compare that already ran (reopened from disk, or run by the CLI directly).</summary>
    internal CompareSession(string id, string left, string right, CompareOptions options, CompareReport report,
        string? resultDir, DateTime startedUtc, TimeSpan elapsed, bool reopened)
        : base(id, options, resultDir, startedUtc, elapsed, reopened)
    {
        Left = left;
        Right = right;
        _task = System.Threading.Tasks.Task.FromResult(report);
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

    internal Compare3Session(string id, string baseDir, string v1, string v2, CompareOptions options, string? resultDir) : base(id, options, resultDir)
    {
        Base = baseDir;
        V1 = v1;
        V2 = v2;
        _task = Run(() => TreeMerger.Run(baseDir, v1, v2, options));
    }

    /// <summary>A finished 3-way compare reopened from its result directory.</summary>
    internal Compare3Session(string id, string baseDir, string v1, string v2, CompareOptions options, ThreeWayReport report,
        string resultDir, DateTime startedUtc, TimeSpan elapsed)
        : base(id, options, resultDir, startedUtc, elapsed, reopened: true)
    {
        Base = baseDir;
        V1 = v1;
        V2 = v2;
        _task = System.Threading.Tasks.Task.FromResult(report);
    }

    public ThreeWayReport? Report => _task.IsCompletedSuccessfully ? _task.Result : null;
}

/// <summary>
/// The server's compares, by id. Keeps the most recent few in memory (a finished report holds every path of
/// the trees, so an unbounded store would grow with each compare of a 50k-file tree); every finished compare
/// is also saved under <see cref="ResultsRoot"/>, and an id not in memory is reopened from there.
/// </summary>
public sealed class SessionStore
{
    public const int DefaultCapacity = 8;

    private readonly object _gate = new();
    private readonly List<Session> _sessions = [];
    private readonly int _capacity;

    /// <summary>Where compares are saved; null = in memory only.</summary>
    public string? ResultsRoot { get; }

    /// <param name="resultsRoot">Where to save compares (default <see cref="ResultStore.DefaultRoot"/>).</param>
    /// <param name="save">false = keep compares in memory only.</param>
    public SessionStore(int capacity = DefaultCapacity, string? resultsRoot = null, bool save = true)
    {
        _capacity = Math.Max(1, capacity);
        ResultsRoot = save ? Path.GetFullPath(resultsRoot ?? ResultStore.DefaultRoot) : null;
    }

    /// <summary>Validate both roots (a loud error, never a silent all-added "success"), then start in the background.</summary>
    public CompareSession Start(string left, string right, CompareOptions? options = null)
    {
        left = Path.GetFullPath(left);
        right = Path.GetFullPath(right);
        InputValidation.ValidateTrees(left, right);
        var opt = options ?? new CompareOptions();
        return (CompareSession)Add([left, right], (id, dir) => new CompareSession(id, left, right, opt, dir), "compare");
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
        var opt = options ?? new CompareOptions();
        return (Compare3Session)Add([baseDir, v1, v2], (id, dir) => new Compare3Session(id, baseDir, v1, v2, opt, dir), "compare3");
    }

    /// <summary>Register a compare the caller already ran (the CLI's own compare), saving it like any other.</summary>
    public CompareSession Adopt(string left, string right, CompareOptions options, CompareReport report, DateTime startedUtc, TimeSpan elapsed)
    {
        left = Path.GetFullPath(left);
        right = Path.GetFullPath(right);
        var s = (CompareSession)Add([left, right], (id, dir) => new CompareSession(id, left, right, options, report, dir, startedUtc, elapsed, reopened: false), "compare");
        s.Persist(report);
        return s;
    }

    private Session Add(string[] roots, Func<string, string?, Session> make, string kind)
    {
        lock (_gate)
        {
            string id;
            do id = Convert.ToHexString(BitConverter.GetBytes(Random.Shared.Next())).ToLowerInvariant()[..4];
            while (_sessions.Any(s => s.Id == id) || (ResultsRoot is not null && ResultStore.Find(ResultsRoot, id) is not null));
            // The result dir exists (state "running") before the compare starts: a crash leaves an honest trace.
            var dir = ResultsRoot is null ? null : ResultStore.CreateRunDir(ResultsRoot, id, kind, roots);
            var session = make(id, dir);
            _sessions.Add(session);
            // Evict the oldest FINISHED compares past capacity; a running one is never dropped (it stays on disk).
            while (_sessions.Count > _capacity && _sessions.FirstOrDefault(s => s.IsDone && s != session) is { } old)
                _sessions.Remove(old);
            return session;
        }
    }

    /// <summary>
    /// The compare with this id (in memory, else reopened from the results directory), a result directory
    /// given by path, or the most recent compare of this process when id is null/empty.
    /// </summary>
    public Session? Get(string? id)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(id)) return _sessions.LastOrDefault();
            id = id.Trim();
            var live = _sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)
                || (s.ResultDir is not null && string.Equals(s.ResultDir, Path.TrimEndingDirectorySeparator(id), StringComparison.OrdinalIgnoreCase)));
            if (live is not null) return live;

            var dir = Directory.Exists(id) ? id : ResultsRoot is null ? null : ResultStore.Find(ResultsRoot, id);
            if (dir is null) return null;
            var loaded = ResultStore.Load(dir); // throws InvalidDataException on an unfinished/corrupt result
            _sessions.Add(loaded);
            while (_sessions.Count > _capacity && _sessions.FirstOrDefault(s => s.IsDone && s != loaded) is { } old)
                _sessions.Remove(old);
            return loaded;
        }
    }

    public IReadOnlyList<Session> All()
    {
        lock (_gate) return [.. _sessions];
    }
}
