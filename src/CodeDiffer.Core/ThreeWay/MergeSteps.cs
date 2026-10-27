using System.Diagnostics;

namespace CodeDiffer.Core.ThreeWay;

/// <summary>A step of a 3-way merge's classify, as the run record counts it.</summary>
public enum MergeStep
{
    /// <summary>The classify itself: which side changed what, the checks that need no file, collisions after.</summary>
    Other,
    /// <summary>Reading the three files (and comparing a large pair in chunks).</summary>
    Read,
    /// <summary>Decoding the bytes to text, and the binary and line-ending checks.</summary>
    Decode,
    /// <summary>Splitting the text into lines.</summary>
    Split,
    /// <summary>The line diffs, base to each side.</summary>
    Diff,
    /// <summary>Lining the two diffs up into regions: edits kept apart and in place, runs cut.</summary>
    Align,
    /// <summary>A conflicting merge done again with keyword stamps bare (its diffs are <see cref="Diff"/>'s).</summary>
    Stamps,
}

/// <summary>How a line diff was made: see <see cref="Diff.LineDiffer.Diff(IReadOnlyList{string}, IReadOnlyList{string}, int, out bool, long?, CancellationToken)"/>.</summary>
public enum DiffWay
{
    /// <summary>One side the other's prefix: no search.</summary>
    Prefix,
    /// <summary>The exact pass, within its budget of changed lines.</summary>
    Exact,
    /// <summary>Past that budget: the linear-space pass.</summary>
    LinearSpace,
    /// <summary>Past the work cap too: one replace over the lot.</summary>
    Coarse,
}

/// <summary>
/// Where a merge's time and bytes go, step by step: each worker thread keeps its own tally (no lock between files), and
/// the step it is in is charged its thread's clock and allocation up to the next switch, so the steps never overlap (a
/// diff inside the stamps' merge is the diff's). Time is thread time: the steps add up to the workers' time, not the wall.
/// Outside a merge (no tally on the thread) a switch is a thread-static read and nothing more.
/// </summary>
public sealed class MergeSteps
{
    private static readonly int Steps = Enum.GetValues<MergeStep>().Length;
    private static readonly int Ways = Enum.GetValues<DiffWay>().Length;

    [ThreadStatic] private static Tally? t_tally;

    private readonly long[] _ticks = new long[Steps];
    private readonly long[] _bytes = new long[Steps];
    private readonly long[] _diffs = new long[Ways];
    private long _bytesRead, _files, _lines, _remembered, _same;

    /// <summary>Thread time spent in a step.</summary>
    public TimeSpan Time(MergeStep s) => TimeSpan.FromSeconds((double)Interlocked.Read(ref _ticks[(int)s]) / Stopwatch.Frequency);
    /// <summary>Bytes allocated in a step.</summary>
    public long Allocated(MergeStep s) => Interlocked.Read(ref _bytes[(int)s]);
    /// <summary>Line diffs made each way.</summary>
    public long Diffs(DiffWay w) => Interlocked.Read(ref _diffs[(int)w]);
    /// <summary>Bytes of the files read whole.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    /// <summary>Files merged line by line, and their lines (base and both sides).</summary>
    public long Files => Interlocked.Read(ref _files);
    public long Lines => Interlocked.Read(ref _lines);
    /// <summary>Files both modified, merged without opening them: as the merge store remembers their three versions
    /// merging, or agreed because v1 and v2 hold the same.</summary>
    public long Remembered => Interlocked.Read(ref _remembered);
    public long SameContent => Interlocked.Read(ref _same);

    /// <summary>Starts this thread's tally, in <see cref="MergeStep.Other"/>.</summary>
    internal object Begin()
    {
        var t = new Tally();
        t_tally = t;
        return t;
    }

    /// <summary>Ends this thread's tally (begun on it) and adds it in.</summary>
    internal void End(object tally)
    {
        var t = (Tally)tally;
        t.Charge();
        if (ReferenceEquals(t_tally, t)) t_tally = null;
        for (int i = 0; i < Steps; i++)
        {
            Interlocked.Add(ref _ticks[i], t.Ticks[i]);
            Interlocked.Add(ref _bytes[i], t.Bytes[i]);
        }
        for (int i = 0; i < Ways; i++) Interlocked.Add(ref _diffs[i], t.Diffs[i]);
        Interlocked.Add(ref _bytesRead, t.BytesRead);
        Interlocked.Add(ref _files, t.Files);
        Interlocked.Add(ref _lines, t.Lines);
        Interlocked.Add(ref _remembered, t.Remembered);
        Interlocked.Add(ref _same, t.Same);
    }

    /// <summary>Charges the step this thread was in, and moves it to <paramref name="s"/>: the step it was in, to switch
    /// back to after.</summary>
    internal static MergeStep Enter(MergeStep s)
    {
        if (t_tally is not { } t) return MergeStep.Other;
        t.Charge();
        var was = t.Step;
        t.Step = s;
        return was;
    }

    internal static void Diffed(DiffWay w)
    {
        if (t_tally is { } t) t.Diffs[(int)w]++;
    }

    internal static void Read(long bytes)
    {
        if (t_tally is { } t) t.BytesRead += bytes;
    }

    internal static void Merged(long lines)
    {
        if (t_tally is { } t) { t.Files++; t.Lines += lines; }
    }

    internal static void FromStore()
    {
        if (t_tally is { } t) t.Remembered++;
    }

    internal static void SameHashes()
    {
        if (t_tally is { } t) t.Same++;
    }

    private sealed class Tally
    {
        public readonly long[] Ticks = new long[Steps];
        public readonly long[] Bytes = new long[Steps];
        public readonly long[] Diffs = new long[Ways];
        public long BytesRead, Files, Lines, Remembered, Same;
        public MergeStep Step;
        private long _at = Stopwatch.GetTimestamp(), _allocated = GC.GetAllocatedBytesForCurrentThread();

        public void Charge()
        {
            long now = Stopwatch.GetTimestamp(), allocated = GC.GetAllocatedBytesForCurrentThread();
            Ticks[(int)Step] += now - _at;
            Bytes[(int)Step] += allocated - _allocated;
            _at = now;
            _allocated = allocated;
        }
    }
}
