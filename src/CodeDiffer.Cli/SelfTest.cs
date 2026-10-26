using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;

namespace CodeDiffer.Cli;

/// <summary>
/// <c>codediffer selftest</c>: every feature run on two or three real trees the user names, each result checked against
/// the trees themselves or against another way of getting it, and an HTML report of what was tested and what it cost.
/// There is no planted truth on a real tree, so the checks are self-consistency: a second compare gives the same
/// verdicts and reads less; the MCP server's answers are the CLI's; sampled verdicts hold when the harness reads the
/// files itself; a patch, applied by a small applier of its own, turns left into right; apply finds the left→right
/// change set applicable onto left and already there on right; the merge overlay holds v2's bytes for v2's one-sided
/// changes. It runs this exe and the MCP server next to it as a user and an agent would (child processes, saved
/// results under its own folder, the shared hash cache), and never writes inside a compared tree.
/// </summary>
internal static class SelfTest
{
    public const string Usage =
        "usage: codediffer selftest <left> <right> [<v2>] [--name NAME] [--out DIR] [--sample N] [--threads N] [--cold]";

    public static int Run(string[] args)
    {
        var trees = new List<string>();
        string? name = null, outRoot = null;
        int sample = 25;
        int? threads = null;
        bool cold = false;
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--name" or "--out" or "--sample" or "--threads")
            {
                if (i + 1 >= args.Length) { Console.Error.WriteLine($"error: {a} needs a value\n{Usage}"); return 64; }
                string v = args[++i];
                switch (a)
                {
                    case "--name": name = v; break;
                    case "--out": outRoot = v; break;
                    case "--sample" when int.TryParse(v, out var n) && n >= 1: sample = n; break;
                    case "--threads" when int.TryParse(v, out var t) && t >= 1: threads = t; break;
                    default: Console.Error.WriteLine($"error: {a} needs a positive integer\n{Usage}"); return 64;
                }
            }
            else if (a == "--cold") cold = true;
            else if (a.StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine($"error: unknown option {a}\n{Usage}"); return 64; }
            else trees.Add(a);
        }
        if (trees.Count is not (2 or 3)) { Console.Error.WriteLine(Usage); return 64; }
        for (int i = 0; i < trees.Count; i++)
        {
            trees[i] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trees[i]));
            if (!Directory.Exists(trees[i])) { Console.Error.WriteLine($"error: not a directory: {trees[i]}"); return 2; }
        }
        name ??= Path.GetFileName(trees[0]);
        var slug = Regex.Replace(name, @"[^A-Za-z0-9._-]+", "_").Trim('_', '.');
        if (slug.Length == 0) slug = "repo";
        outRoot = Path.GetFullPath(outRoot ?? Path.Combine(Path.GetDirectoryName(ResultStore.DefaultRoot)!, "selftest"));
        var runDir = Path.Combine(outRoot, slug, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        foreach (var t in trees)
            if (Inside(runDir, t))
            {
                Console.Error.WriteLine($"error: the selftest output ({runDir}) is inside a compared tree ({t}); give --out elsewhere");
                return 2;
            }
        var cli = Environment.ProcessPath!;
        var mcp = Path.Combine(AppContext.BaseDirectory, "CodeDiffer.Mcp.exe");
        return new Runner(cli, File.Exists(mcp) ? mcp : null, trees, name, Path.Combine(outRoot, slug), runDir, sample, threads, cold).Go();
    }

    internal static bool Inside(string path, string tree)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        tree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tree));
        return path.Equals(tree, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(tree + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal enum Verdict { Pass, Fail, Warn, Skip, Info }

    internal sealed record Check(string Area, string Name, Verdict Verdict, string Detail);

    internal sealed record Step(int N, string Label, string Command, int Exit, double Seconds, string Log, string Head);

    internal sealed record Metric(string Label, string Id, string Kind, string Ended, double Seconds, double ReadMB, double PeakMB,
        double AllocatedMB, double CpuSeconds, int Threads, string Cache, long CacheSides, long Listed, double? MergeSeconds,
        double? MergeAllocatedMB, long? FilesMerged, string Summary);

    internal sealed record Exec(int Exit, string Out, string Err, double Seconds);

    private sealed class Runner(string cli, string? mcpExe, List<string> trees, string name, string repoDir, string runDir, int sample,
        int? threads, bool cold)
    {
        private readonly List<Check> _checks = [];
        private readonly List<Step> _steps = [];
        private readonly List<(string Label, string Id)> _compares = [];
        private readonly Dictionary<string, string> _facts = new(StringComparer.Ordinal);
        private readonly string _results = Path.Combine(runDir, "results");
        private readonly string _work = Path.Combine(runDir, "work");
        private readonly string _logs = Path.Combine(runDir, "logs");
        private readonly DateTime _started = DateTime.Now;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Mcp? _mcp;
        private string _version = "?";

        public int Go()
        {
            foreach (var d in new[] { _results, _work, _logs }) Directory.CreateDirectory(d);
            Console.WriteLine($"selftest {name}: {string.Join("  ·  ", trees)}");
            Console.WriteLine($"  output {runDir}");
            Section("setup", Setup);
            Section("2-way", () => TwoWay(trees[0], trees[1]));
            if (trees.Count == 3) Section("3-way", () => ThreeWay(trees[0], trees[1], trees[2]));
            Section("tools", Tools);
            _mcp?.Dispose();
            var metrics = Metrics();
            var seconds = _clock.Elapsed.TotalSeconds;
            var history = History(metrics, seconds);
            WriteJson(metrics, seconds);
            var html = Path.Combine(runDir, "index.html");
            File.WriteAllText(html, SelfTestReport.Html(name, trees, _version, _started, seconds, _checks, metrics, _steps, history, _facts,
                Path.GetRelativePath(runDir, _work)), new UTF8Encoding(false));
            int fails = _checks.Count(c => c.Verdict == Verdict.Fail), warns = _checks.Count(c => c.Verdict == Verdict.Warn);
            Console.WriteLine();
            Console.WriteLine($"selftest {(fails == 0 ? "PASSED" : "FAILED")}: {_checks.Count(c => c.Verdict == Verdict.Pass)} pass · {fails} fail · " +
                              $"{warns} warn · {_checks.Count(c => c.Verdict == Verdict.Skip)} skipped · {_checks.Count(c => c.Verdict == Verdict.Info)} info · " +
                              $"{TimeSpan.FromSeconds(seconds):h\\:mm\\:ss}");
            Console.WriteLine($"report: {html}");
            return fails == 0 ? 0 : 1;
        }

        private void Section(string area, Action body)
        {
            try { body(); }
            catch (Exception ex) { Add(area, "ran to the end", Verdict.Fail, $"stopped by {ex.GetType().Name}: {ex.Message}"); }
        }

        private void Add(string area, string check, Verdict v, string detail)
        {
            _checks.Add(new Check(area, check, v, detail));
            Console.WriteLine($"  {v.ToString().ToUpperInvariant(),-4}  {area} · {check}: {detail}");
        }

        private void Expect(string area, string check, bool ok, string detail) => Add(area, check, ok ? Verdict.Pass : Verdict.Fail, detail);

        // ---- running the exes ----

        private Exec Run(string label, string exe, params string[] args) => Run(label, exe, true, args);

        private Exec Run(string label, string exe, bool log, params string[] args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
                CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Isolate(psi);
            var clock = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            var r = new Exec(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(), clock.Elapsed.TotalSeconds);
            if (log)
            {
                int n = _steps.Count + 1;
                var file = Path.Combine(_logs, $"{n:00}-{Regex.Replace(label, @"[^A-Za-z0-9]+", "-").Trim('-')}.txt");
                var command = $"{Path.GetFileName(exe)} {string.Join(' ', args.Select(Quote))}";
                File.WriteAllText(file, $"{command}\nexit {r.Exit} after {r.Seconds:0.0}s\n\n--- stdout\n{r.Out}\n--- stderr\n{r.Err}", new UTF8Encoding(false));
                _steps.Add(new Step(n, label, command, r.Exit, r.Seconds, Path.GetRelativePath(runDir, file), Head(r.Out.Length > 0 ? r.Out : r.Err, 40)));
            }
            return r;
        }

        private void Isolate(ProcessStartInfo psi)
        {
            psi.Environment["CODEDIFFER_RESULTS_DIR"] = _results; // its compares are its own; the hash cache is the user's
            psi.Environment["CODEDIFFER_OUT_DIR"] = Path.Combine(_work, "out");
        }

        private static string Quote(string a) => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a;

        private static string Head(string text, int lines)
        {
            var all = text.Replace("\r\n", "\n").Split('\n');
            return all.Length <= lines ? text.TrimEnd() : string.Join('\n', all.Take(lines)) + $"\n… {all.Length - lines} more line(s) in the log";
        }

        /// <summary>A check's detail from command output: its first non-empty lines on one line.</summary>
        private static string Brief(string text, int lines)
            => string.Join(" · ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(lines));

        private string[] ThreadArgs => threads is { } t ? ["--threads", t.ToString()] : [];

        private static string? IdOf(string output)
            => Regex.Match(output, @"\(id ([0-9a-f]+)\)") is { Success: true } m ? m.Groups[1].Value
                : Regex.Match(output, @"by id ([0-9a-f]+)\)") is { Success: true } m2 ? m2.Groups[1].Value
                : Regex.Match(output, @"^compare3? ([0-9a-f]+) ", RegexOptions.Multiline) is { Success: true } m3 ? m3.Groups[1].Value : null;

        private Session Load(string id) => ResultStore.Load(ResultStore.Find(_results, id) ?? throw new InvalidDataException($"compare {id} was not saved"));

        // ---- setup ----

        private void Setup()
        {
            var v = Run("version", cli, "version");
            _version = v.Out.Trim().Replace("codediffer ", "");
            Expect("setup", "CLI answers", v.Exit == 0, $"codediffer {_version}");
            _facts["CodeDiffer"] = _version;
            _facts["machine"] = $"{Environment.ProcessorCount} cores · {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1L << 30)} GB";
            _facts["threads"] = threads?.ToString() ?? "default";
            _facts["first compare"] = cold ? "--rehash (every byte read)" : "with the hash cache as it is";
            _facts["sample"] = $"{sample} files per check";
            if (mcpExe is null)
            {
                Add("setup", "MCP server answers", Verdict.Skip, "CodeDiffer.Mcp.exe is not next to this exe: the MCP checks are skipped");
                return;
            }
            _mcp = new Mcp(mcpExe, Isolate, Path.Combine(_logs, "mcp-server-stderr.txt"));
            var tools = _mcp.Tools();
            Expect("setup", "MCP server answers", _mcp.Version.Split('+')[0] == _version.Split('+')[0],
                $"server {_mcp.Version}, CLI {_version} · {tools.Count} tools");
            string[] used = ["start_compare", "start_compare3", "get_summary", "list_files", "get_file_diff", "get_stats", "export_changeset",
                             "apply_changeset", "write_merge", "write_report", "list_compares", "get_run_telemetry"];
            var missing = used.Where(u => !tools.Contains(u)).ToList();
            Expect("setup", "MCP tools listed", missing.Count == 0, missing.Count == 0 ? string.Join(", ", tools) : "missing: " + string.Join(", ", missing));
        }

        // ---- 2-way ----

        private CompareReport? Compare(string label, string l, string r, params string[] extra)
        {
            var c = Run(label, cli, ["compare", l, r, .. ThreadArgs, .. extra]);
            var id = IdOf(c.Out);
            if (c.Exit is not (0 or 3) || id is null)
            {
                Add("2-way", label + " finishes", Verdict.Fail, $"exit {c.Exit}: {Brief(c.Err, 3)}");
                return null;
            }
            _compares.Add((label, id));
            var rep = ((CompareSession)Load(id)).Report!;
            Add("2-way", label + " finishes", c.Exit == 0 ? Verdict.Pass : Verdict.Warn,
                (c.Exit == 3 ? "INCOMPLETE (exit 3: a directory unlisted or a file unread) · " : "") + $"{c.Seconds:0.0}s · {Counts(rep)}");
            return rep;
        }

        private static string Counts(CompareReport r)
            => $"{r.Total:N0} files: identical {r.Count(ChangeStatus.Identical):N0} · modified {r.Count(ChangeStatus.Modified):N0} · " +
               $"added {r.Count(ChangeStatus.Added):N0} · removed {r.Count(ChangeStatus.Removed):N0} · renamed {r.Count(ChangeStatus.Renamed):N0}";

        private static string Key(FileChange c)
            => $"{c.Status}|{c.Reason}|{c.RenamedFrom}|{c.SimilarityMilli}|{c.EditedRename}|{c.RenameHeld is not null}|{c.Unreadable is not null}|{c.Noise}";

        /// <summary>Paths whose verdicts differ between two compares of the same trees (at most a few shown).</summary>
        private static List<string> Differ(CompareReport a, CompareReport b)
        {
            var x = a.Changes.ToDictionary(c => c.RelativePath, Key, StringComparer.Ordinal);
            var y = b.Changes.ToDictionary(c => c.RelativePath, Key, StringComparer.Ordinal);
            var bad = x.Where(kv => !y.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key)
                .Concat(y.Keys.Where(k => !x.ContainsKey(k))).Distinct().ToList();
            if (a.Count(ChangeStatus.Identical) != b.Count(ChangeStatus.Identical)) bad.Insert(0, "(identical count)");
            return bad;
        }

        private static string Few(IEnumerable<string> paths, int n = 5)
        {
            var l = paths.ToList();
            return string.Join(", ", l.Take(n)) + (l.Count > n ? $" … {l.Count - n} more" : "");
        }

        private (double ReadMB, long CacheSides) ReadCost(string id)
        {
            var dir = ResultStore.Find(_results, id);
            var run = dir is null ? null : Path.Combine(dir, "run.json");
            if (run is null || !File.Exists(run)) return (double.NaN, 0);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(run));
            long bytes = 0, sides = 0;
            if (doc.RootElement.TryGetProperty("compares", out var cs))
                foreach (var c in cs.EnumerateArray())
                {
                    bytes += Long(c, "bytesRead");
                    sides += Long(c, "cacheSides");
                }
            return (bytes / 1048576.0, sides);
        }

        private void TwoWay(string l, string r)
        {
            const string A = "2-way";
            var first = Compare("compare (first)", l, r, cold ? ["--rehash"] : []);
            if (first is null) return;
            _facts["2-way trees"] = $"{l}  →  {r}";
            _facts["2-way changes"] = Counts(first);
            var touched = first.Changes.Where(c => c.Status != ChangeStatus.Identical).ToList();
            _facts["changes by kind"] = string.Join(" · ", touched.GroupBy(c => c.Type).OrderBy(g => g.Key).Select(g => $"{g.Key.ToString().ToLowerInvariant()} {g.Count()}"));
            _facts["modified by reason"] = string.Join(" · ", touched.Where(c => c.Reason is not null).GroupBy(c => c.ReasonLabel).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
            var warm = Compare("compare (warm)", l, r);
            if (warm is not null)
            {
                var bad = Differ(first, warm);
                Expect(A, "warm compare gives the same verdicts", bad.Count == 0,
                    bad.Count == 0 ? $"{first.Changes.Count} verdicts alike" : $"{bad.Count} differ: {Few(bad)}");
                var (r1, s1) = ReadCost(_compares[0].Id);
                var (r2, s2) = ReadCost(_compares[^1].Id);
                Expect(A, "warm compare reads no more", !(r2 > r1 + 1), $"first {r1:0.0} MB read ({s1} sides from the cache), warm {r2:0.0} MB ({s2} from the cache)");
            }

            // The verdicts, checked by reading the files here.
            var changes = first.Changes.Where(c => c.Unreadable is null && !c.BehindLink).ToList();
            var same = Pick(changes.Where(c => c.Status == ChangeStatus.Identical).ToList(), sample);
            if (same.Count == 0) Add(A, "identical files are byte-equal", Verdict.Skip, "no identical file listed");
            else
            {
                var res = same.Select(c => (c, eq: SameBytes(Path.Combine(l, c.RelativePath), Path.Combine(r, c.RelativePath)))).ToList();
                var bad = res.Where(x => x.eq == false).Select(x => x.c.RelativePath).ToList();
                Expect(A, "identical files are byte-equal", bad.Count == 0,
                    $"{res.Count(x => x.eq == true)} of {same.Count} sampled read equal" + (res.Any(x => x.eq is null) ? $" ({res.Count(x => x.eq is null)} over 64 MB not read)" : "") +
                    (bad.Count > 0 ? $"; differ: {Few(bad)}" : ""));
            }
            var modified = changes.Where(c => c.Status == ChangeStatus.Modified && c.Reason is not null).ToList();
            var picked = Pick(modified, sample);
            if (picked.Count == 0) Add(A, "modified files differ as their reason says", Verdict.Skip, "no modified file");
            else
            {
                var bad = new List<string>();
                foreach (var c in picked)
                    if (ReasonHolds(Path.Combine(l, c.RelativePath), Path.Combine(r, c.RelativePath), c.Reason!.Value) is { } why) bad.Add($"{c.RelativePath} ({why})");
                var reasons = string.Join(" · ", picked.GroupBy(c => c.ReasonLabel).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
                Expect(A, "modified files differ as their reason says", bad.Count == 0, $"{picked.Count - bad.Count} of {picked.Count} sampled ({reasons})" + (bad.Count > 0 ? "; wrong: " + Few(bad) : ""));
            }
            var sides = changes.Where(c => c.Status is ChangeStatus.Added or ChangeStatus.Removed or ChangeStatus.Renamed).ToList();
            if (sides.Count == 0) Add(A, "adds, removes and renames are where they say", Verdict.Skip, "none");
            else
            {
                var bad = new List<string>();
                foreach (var c in Pick(sides, Math.Max(sample * 4, 100)))
                {
                    bool ok = c.Status switch
                    {
                        ChangeStatus.Added => File.Exists(Path.Combine(r, c.RelativePath)) && !File.Exists(Path.Combine(l, c.RelativePath)),
                        ChangeStatus.Removed => File.Exists(Path.Combine(l, c.RelativePath)) && !File.Exists(Path.Combine(r, c.RelativePath)),
                        _ => File.Exists(Path.Combine(l, c.RenamedFrom!)) && File.Exists(Path.Combine(r, c.RelativePath)) &&
                             (c.RenamedFrom!.Equals(c.RelativePath, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(r, c.RenamedFrom!)))
                             && (!c.PureRename || SameBytes(Path.Combine(l, c.RenamedFrom!), Path.Combine(r, c.RelativePath)) != false),
                    };
                    if (!ok) bad.Add(c.RelativePath);
                }
                Expect(A, "adds, removes and renames are where they say", bad.Count == 0,
                    $"{Math.Min(sides.Count, Math.Max(sample * 4, 100)) - bad.Count} checked on disk" + (bad.Count > 0 ? "; wrong: " + Few(bad) : ""));
            }

            // The patches: `diff` per file, applied by the applier here, gives right's bytes.
            var text = Pick(changes.Where(c => (c.Status == ChangeStatus.Modified && c.Reason is ChangeReason.Content or ChangeReason.Whitespace or ChangeReason.Eol ||
                                                c.Status == ChangeStatus.Renamed && !c.PureRename && c.RenameHeld is null) &&
                                               c.Type != FileType.Binary && Math.Max(c.LeftSize, c.RightSize) <= 8 << 20).ToList(), sample);
            var hunks = new Dictionary<string, int>(StringComparer.Ordinal);
            if (text.Count == 0) Add(A, "diff patches turn left into right", Verdict.Skip, "no text file changed");
            else
            {
                var bad = new List<string>();
                int skipped = 0;
                foreach (var c in text)
                {
                    string lf = Path.Combine(l, c.RenamedFrom ?? c.RelativePath), rf = Path.Combine(r, c.RelativePath);
                    var lt = Utf8(lf);
                    var rt = Utf8(rf);
                    if (lt is null || rt is null) { skipped++; continue; }
                    var d = Run("diff", cli, false, "diff", lf, rf);
                    var lines = PatchApplier.Lines(d.Out);
                    hunks[c.RelativePath] = lines.Count(x => x.StartsWith("@@ ", StringComparison.Ordinal));
                    var got = PatchApplier.Apply(lt, lines, out var why);
                    if (got != rt) bad.Add($"{c.RelativePath} ({why ?? "result differs from right"})");
                }
                Expect(A, "diff patches turn left into right", bad.Count == 0,
                    $"{text.Count - skipped - bad.Count} of {text.Count - skipped} sampled round-trip byte-exact" + (skipped > 0 ? $" ({skipped} not UTF-8, not tried)" : "") +
                    (bad.Count > 0 ? "; wrong: " + Few(bad) : ""));
            }

            // Porting: the left→right change set applies cleanly onto left and is all there already on right.
            var onL = Run("apply onto left (dry run)", cli, ["apply", l, r, l, .. ThreadArgs]);
            var onR = Run("apply onto right (dry run)", cli, ["apply", l, r, r, .. ThreadArgs]);
            var al = ApplyCounts(onL.Out);
            var ar = ApplyCounts(onR.Out);
            Expect(A, "apply onto left: every file applies", al is { Conflict: 0 } && onL.Exit == 0,
                al is { } x ? $"{x.Would} would apply · {x.Already} already · {x.Conflict} conflict" : $"exit {onL.Exit}: {Brief(onL.Out + onL.Err, 3)}");
            Expect(A, "apply onto right: everything already there", ar is { Would: 0, Conflict: 0 },
                ar is { } y ? $"{y.Would} would apply · {y.Already} already · {y.Conflict} conflict" : $"exit {onR.Exit}: {Brief(onR.Out + onR.Err, 3)}");

            var rep = Run("report (2-way)", cli, "report", _compares[0].Id, "--out", Path.Combine(_work, "report-2way"));
            var index = Path.Combine(_work, "report-2way", "index.html");
            Expect(A, "HTML report written", rep.Exit == 0 && File.Exists(index),
                File.Exists(index) ? $"{Path.GetRelativePath(runDir, index)} · {new FileInfo(index).Length / 1024} KB" : $"exit {rep.Exit}: {Brief(rep.Err, 3)}");

            var giant = modified.Where(c => Math.Min(c.LeftSize, c.RightSize) >= 16 << 20).MaxBy(c => c.RightSize);
            if (giant is null) Add(A, "blockdiff of a file over 16 MB", Verdict.Skip, "no modified file over 16 MB");
            else
            {
                var b = Run("blockdiff", cli, "blockdiff", Path.Combine(l, giant.RelativePath), Path.Combine(r, giant.RelativePath));
                Expect(A, "blockdiff of a file over 16 MB", b.Exit == 0, $"{giant.RelativePath} ({giant.RightSize / 1048576.0:0} MB) · {b.Seconds:0.0}s · {Brief(b.Out, 1)}");
            }

            if (_mcp is not null) McpTwoWay(l, r, first, text, hunks, ar);
        }

        private void McpTwoWay(string l, string r, CompareReport cliReport, List<FileChange> text, Dictionary<string, int> hunks,
            (int Would, int Already, int Conflict)? cliOnRight)
        {
            const string A = "2-way MCP";
            var start = _mcp!.Call("start_compare", Args(("left", l), ("right", r), ("wait_seconds", 60), ("threads", threads)));
            var id = IdOf(start.Text);
            if (id is null || start.Error) { Add(A, "start_compare finishes", Verdict.Fail, Brief(start.Text, 3)); return; }
            var summary = WaitDone(id);
            _compares.Add(("compare (MCP)", id));
            var rep = ((CompareSession)Load(id)).Report!;
            var bad = Differ(cliReport, rep);
            Expect(A, "start_compare gives the CLI's verdicts", bad.Count == 0, bad.Count == 0 ? Counts(rep) : $"{bad.Count} differ: {Few(bad)}");
            Expect(A, "get_summary counts are the CLI's", Has(summary, "modified", rep.Count(ChangeStatus.Modified)) && Has(summary, "added", rep.Count(ChangeStatus.Added)) &&
                                                        Has(summary, "removed", rep.Count(ChangeStatus.Removed)) && Has(summary, "renamed", rep.Count(ChangeStatus.Renamed)),
                Brief(summary, 1));

            // list_files: every changed file once over the pages, and its filters count what the report holds.
            int changed = rep.Changes.Count(c => c.Status != ChangeStatus.Identical);
            int rows = 0, pages = 0;
            for (int page = 1; ; page++)
            {
                var lf = _mcp.Call("list_files", Args(("compare_id", id), ("page", page), ("page_size", 500)));
                pages++;
                rows += PatchApplier.Lines(lf.Text).Count(x => Regex.IsMatch(x, "^[AMDR] "));
                if (!lf.Text.Contains($"page={page + 1}") || page >= 400) break;
            }
            Expect(A, "list_files pages through every change", rows == changed, $"{rows} rows over {pages} page(s) · {changed} changed");
            var filters = new List<string>();
            bool filtersOk = true;
            foreach (var (arg, value, expect) in new (string, string, int)[]
                     {
                         ("status", "modified", rep.Count(ChangeStatus.Modified)),
                         ("status", "added", rep.Count(ChangeStatus.Added)),
                         ("type", "code", rep.Changes.Count(c => c.Status != ChangeStatus.Identical && c.Type == FileType.Code)),
                         ("type", "binary", rep.Changes.Count(c => c.Status != ChangeStatus.Identical && c.Type == FileType.Binary)),
                     })
            {
                var lf = _mcp.Call("list_files", Args(("compare_id", id), (arg, value), ("page_size", 1)));
                int got = Regex.Match(lf.Text, @"([\d,]+) file\(s\) match") is { Success: true } m ? Num(m.Groups[1].Value) : lf.Text.Contains("no file") ? 0 : -1;
                filters.Add($"{arg}={value} {got}");
                filtersOk &= got == expect || got == -1 && expect == 0;
            }
            Expect(A, "list_files filters count the report's files", filtersOk, string.Join(" · ", filters));
            var noise = _mcp.Call("list_files", Args(("compare_id", id), ("hide_noise", "all"), ("page_size", 1)));
            Add(A, "list_files hide_noise=all", Verdict.Info, Regex.Match(noise.Text, @"[\d,]+ file\(s\) match").Value is { Length: > 0 } s ? $"{s} of {changed} once noise is hidden" : Brief(noise.Text, 1));

            var stats = _mcp.Call("get_stats", Args(("compare_id", id)));
            bool statsOk = new[] { ChangeStatus.Identical, ChangeStatus.Added, ChangeStatus.Removed, ChangeStatus.Modified, ChangeStatus.Renamed }
                .All(st => Regex.IsMatch(stats.Text, $@"^\s+{st.ToString().ToLowerInvariant()}\s+{Count(rep.Count(st))}\s", RegexOptions.Multiline));
            Expect(A, "get_stats totals are the report's", statsOk && !stats.Error, Brief(stats.Text.Split('\n').FirstOrDefault(x => x.StartsWith("reasons", StringComparison.Ordinal)) ?? "", 1));

            var diffs = text.Where(c => hunks.ContainsKey(c.RelativePath) && c.Reason != ChangeReason.Eol).Take(8).ToList(); // an eol-only file is a note there, not hunks
            if (diffs.Count == 0) Add(A, "get_file_diff hunks are the CLI diff's", Verdict.Skip, "no text file diffed");
            else
            {
                var wrong = new List<string>();
                foreach (var c in diffs)
                {
                    var d = _mcp.Call("get_file_diff", Args(("compare_id", id), ("path", c.RelativePath), ("max_lines", 5)));
                    int got = Regex.Match(d.Text, @"in ([\d,]+) hunk\(s\)") is { Success: true } m ? Num(m.Groups[1].Value) : -1;
                    if (got != hunks[c.RelativePath]) wrong.Add($"{c.RelativePath} ({got} vs {hunks[c.RelativePath]})");
                }
                Expect(A, "get_file_diff hunks are the CLI diff's", wrong.Count == 0, $"{diffs.Count - wrong.Count} of {diffs.Count}" + (wrong.Count > 0 ? "; differ: " + Few(wrong) : ""));
            }

            var patch = Path.Combine(_work, "changeset.patch");
            var ex = _mcp.Call("export_changeset", Args(("compare_id", id), ("out_path", patch), ("literal", true)));
            if (ex.Error || !File.Exists(patch)) Add(A, "export_changeset turns left into right", Verdict.Fail, Brief(ex.Text, 3));
            else
            {
                var sections = PatchApplier.Sections(patch, text.Select(c => $"a/{c.RenamedFrom ?? c.RelativePath} b/{c.RelativePath}").ToHashSet(StringComparer.Ordinal));
                var wrong = new List<string>();
                int tried = 0;
                foreach (var c in text)
                {
                    var lt = Utf8(Path.Combine(l, c.RenamedFrom ?? c.RelativePath));
                    var rt = Utf8(Path.Combine(r, c.RelativePath));
                    if (lt is null || rt is null) continue;
                    tried++;
                    if (!sections.TryGetValue($"a/{c.RenamedFrom ?? c.RelativePath} b/{c.RelativePath}", out var sec)) { wrong.Add($"{c.RelativePath} (not in the patch)"); continue; }
                    if (PatchApplier.Apply(lt, sec, out var why) != rt) wrong.Add($"{c.RelativePath} ({why ?? "result differs"})");
                }
                Expect(A, "export_changeset turns left into right", wrong.Count == 0,
                    $"{tried - wrong.Count} of {tried} sampled files byte-exact · patch {new FileInfo(patch).Length / 1024.0:0.#} KB · {Brief(ex.Text.Split('\n').ElementAtOrDefault(1)?.Trim() ?? "", 1)}" +
                    (wrong.Count > 0 ? "; wrong: " + Few(wrong) : ""));
            }

            var ap = _mcp.Call("apply_changeset", Args(("compare_id", id), ("target", r)));
            var mr = ApplyCounts(ap.Text);
            Expect(A, "apply_changeset onto right is the CLI's", mr is not null && mr == cliOnRight,
                mr is { } x ? $"{x.Would} would apply · {x.Already} already · {x.Conflict} conflict" : Brief(ap.Text, 3));
            var lc = _mcp.Call("list_compares", Args());
            Expect(A, "list_compares lists it", lc.Text.Contains(id), Brief(lc.Text, 1));
        }

        private string WaitDone(string id)
        {
            for (int i = 0; ; i++)
            {
                var s = _mcp!.Call("get_summary", Args(("compare_id", id), ("wait_seconds", 300)));
                var saved = ResultStore.List(_results, 200).FirstOrDefault(c => c.Id == id);
                if (saved is not null && saved.State != "running" || i > 2000) return s.Text;
            }
        }

        private static bool Has(string summary, string label, int n) => Regex.IsMatch(summary, $@"^\s+{label}\s+{Count(n)}\b", RegexOptions.Multiline);

        /// <summary>A count as the tools print it: 1,989 or 1989.</summary>
        private static int Num(string s) => int.Parse(s.Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>A pattern for a count either way it is printed.</summary>
        private static string Count(int n) => $"(?:{n}|{n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)})";

        private static (int Would, int Already, int Conflict)? ApplyCounts(string output)
            => Regex.Match(output, @"files: ([\d,]+) would apply \S ([\d,]+) already there \S ([\d,]+) conflict") is { Success: true } m
                ? (Num(m.Groups[1].Value), Num(m.Groups[2].Value), Num(m.Groups[3].Value)) : null;

        // ---- 3-way ----

        private void ThreeWay(string b, string v1, string v2)
        {
            const string A = "3-way";
            _facts["3-way trees"] = $"base {b}  ·  v1 {v1}  ·  v2 {v2}";
            var merge = Path.Combine(_work, "merge");
            var c = Run("compare3", cli, ["compare3", b, v1, v2, "--merge-out", merge, .. ThreadArgs]);
            var id = IdOf(c.Out);
            if (c.Exit is not (0 or 1 or 3) || id is null) { Add(A, "compare3 finishes", Verdict.Fail, $"exit {c.Exit}: {Brief(c.Err, 3)}"); return; }
            _compares.Add(("compare3", id));
            var rep = ((Compare3Session)Load(id)).Report!;
            var outcomes = string.Join(" · ", Enum.GetValues<Merge3Outcome>().Select(o => $"{o} {rep.Count(o)}"));
            Add(A, "compare3 finishes", c.Exit == 3 ? Verdict.Warn : Verdict.Pass, (c.Exit == 3 ? "INCOMPLETE · " : "") + $"{c.Seconds:0.0}s · {rep.Entries.Count} touched: {outcomes}");
            Expect(A, "exit code says whether it conflicts", c.Exit == 3 || (c.Exit == 1) == (rep.Count(Merge3Outcome.Conflict) > 0), $"exit {c.Exit} · {rep.Count(Merge3Outcome.Conflict)} conflict(s)");

            // The overlay: v2's one-sided changes carry v2's bytes; merged files have no markers; every conflict is listed.
            var files = Path.Combine(merge, "files");
            bool written = Directory.Exists(merge) && !File.Exists(Path.Combine(merge, "INCOMPLETE.txt"));
            Expect(A, "merge overlay written", written, written ? $"{(Directory.Exists(files) ? Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories).Count() : 0)} file(s) in files\\" : "no finished overlay");
            if (written)
            {
                var wrong = new List<string>();
                var fromV2 = Pick(rep.Entries.Where(e => e.Outcome == Merge3Outcome.V2Only && e.V2 is { Status: not ChangeStatus.Removed, Unreadable: null } && e.MergedPath is not null).ToList(), sample);
                foreach (var e in fromV2)
                    if (SameBytes(Path.Combine(files, e.MergedPath!), Path.Combine(v2, e.V2!.RelativePath)) == false) wrong.Add(e.Path);
                var merged = Pick(rep.Entries.Where(e => e.Outcome == Merge3Outcome.Merged && e.MergedPath is not null).ToList(), sample);
                foreach (var e in merged)
                {
                    var f = Path.Combine(files, e.MergedPath!);
                    if (!File.Exists(f) || File.ReadLines(f).Any(x => x.StartsWith("<<<<<<< ", StringComparison.Ordinal))) wrong.Add(e.Path + " (merged)");
                }
                var listed = File.Exists(Path.Combine(merge, "conflicts.txt")) ? File.ReadAllText(Path.Combine(merge, "conflicts.txt")) : "";
                var conflicts = rep.Entries.Where(e => e.Outcome == Merge3Outcome.Conflict).ToList();
                var unlisted = conflicts.Where(e => !listed.Contains(e.Path) && !(e.MergedPath is { } mp && listed.Contains(mp))).Select(e => e.Path).ToList();
                Expect(A, "overlay holds what the merge says", wrong.Count == 0 && unlisted.Count == 0,
                    $"{fromV2.Count} v2-only file(s) are v2's bytes · {merged.Count} merged file(s) without markers · {conflicts.Count - unlisted.Count} of {conflicts.Count} conflict(s) in conflicts.txt" +
                    (wrong.Count + unlisted.Count > 0 ? "; wrong: " + Few(wrong.Concat(unlisted)) : ""));
                var ao = Run("apply-overlay (dry run)", cli, ["apply-overlay", merge, v1, .. ThreadArgs]);
                Expect(A, "apply-overlay onto v1 (dry run)", ao.Exit == 0, $"exit {ao.Exit} · {Brief(ao.Out, 2).Replace('\n', ' ')}");
            }

            var port = Run("apply base→v2 onto v1 (dry run)", cli, ["apply", b, v2, v1, .. ThreadArgs]);
            var pc = ApplyCounts(port.Out);
            Add(A, "apply base→v2 onto v1 (the merge by porting)", pc is null ? Verdict.Fail : Verdict.Info,
                pc is { } p ? $"{p.Would} would apply · {p.Already} already · {p.Conflict} conflict (compare3: {rep.Count(Merge3Outcome.Conflict)} conflict(s); the two count differently: apply per file of v2's changes)" : Brief(port.Out + port.Err, 3));

            var html = Run("report (3-way)", cli, "report", id, "--out", Path.Combine(_work, "report-3way"));
            var index = Path.Combine(_work, "report-3way", "index.html");
            Expect(A, "HTML report written", html.Exit == 0 && File.Exists(index),
                File.Exists(index) ? $"{Path.GetRelativePath(runDir, index)} · {new FileInfo(index).Length / 1024} KB" : $"exit {html.Exit}: {Brief(html.Err, 3)}");

            if (_mcp is null) return;
            const string M = "3-way MCP";
            var start = _mcp.Call("start_compare3", Args(("base", b), ("v1", v1), ("v2", v2), ("wait_seconds", 60), ("threads", threads)));
            var mid = IdOf(start.Text);
            if (mid is null || start.Error) { Add(M, "start_compare3 finishes", Verdict.Fail, Brief(start.Text, 3)); return; }
            WaitDone(mid);
            _compares.Add(("compare3 (MCP)", mid));
            var mrep = ((Compare3Session)Load(mid)).Report!;
            static string K(Merge3Entry e) => $"{e.Outcome}|{e.ConflictKind}|{e.MergedPath}|{e.ConflictRegions}|{e.CleanRegions}";
            var x = rep.Entries.ToDictionary(e => e.Path, K, StringComparer.Ordinal);
            var bad = mrep.Entries.Where(e => !x.TryGetValue(e.Path, out var k) || k != K(e)).Select(e => e.Path).ToList();
            if (mrep.Entries.Count != rep.Entries.Count) bad.Insert(0, $"({mrep.Entries.Count} vs {rep.Entries.Count} entries)");
            Expect(M, "start_compare3 gives the CLI's merge", bad.Count == 0, bad.Count == 0 ? $"{mrep.Entries.Count} entries alike" : $"{bad.Count} differ: {Few(bad)}");

            var lc = _mcp.Call("list_files", Args(("compare_id", mid), ("status", "conflict"), ("page_size", 1)));
            int got = Regex.Match(lc.Text, @"([\d,]+) path\(s\) match") is { Success: true } m ? Num(m.Groups[1].Value) : lc.Text.Contains("no ") ? 0 : -1;
            Expect(M, "list_files status=conflict counts the conflicts", got == mrep.Count(Merge3Outcome.Conflict) || got == -1 && mrep.Count(Merge3Outcome.Conflict) == 0,
                $"{got} listed · {mrep.Count(Merge3Outcome.Conflict)} conflict(s)");
            if (mrep.Entries.FirstOrDefault(e => e.Outcome == Merge3Outcome.Conflict && e.ConflictKind == "content") is { } ce)
            {
                var d = _mcp.Call("get_file_diff", Args(("compare_id", mid), ("path", ce.Path), ("max_lines", 4000)));
                Expect(M, "get_file_diff shows a conflict's markers", d.Text.Contains("<<<<<<<") || d.Text.Contains("capped"), $"{ce.Path} · {Brief(d.Text, 1)}");
            }
            else Add(M, "get_file_diff shows a conflict's markers", Verdict.Skip, "no content conflict");

            var mm = Path.Combine(_work, "merge-mcp");
            var wm = _mcp.Call("write_merge", Args(("compare_id", mid), ("out_dir", mm)));
            if (!written || wm.Error || !Directory.Exists(mm)) Add(M, "write_merge is the CLI's overlay", Verdict.Fail, Brief(wm.Text, 3));
            else
            {
                var diff = DirDiff(merge, mm);
                Expect(M, "write_merge is the CLI's overlay", diff.Count == 0, diff.Count == 0 ? $"{Directory.EnumerateFiles(mm, "*", SearchOption.AllDirectories).Count()} file(s) byte-equal" : "differ: " + Few(diff));
            }
            var wr = _mcp.Call("write_report", Args(("compare_id", mid)));
            var rx = Regex.Match(wr.Text, @"report: (.+index\.html)");
            Expect(M, "write_report writes the HTML report", rx.Success && File.Exists(rx.Groups[1].Value.Trim()), Brief(wr.Text, 1));
        }

        /// <summary>Relative paths that differ between two directories (missing on a side, or other bytes); OVERLAY.txt
        /// is left out (it names its own directory).</summary>
        private static List<string> DirDiff(string a, string b)
        {
            static Dictionary<string, string> Files(string d) => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(d, f), f => f, StringComparer.OrdinalIgnoreCase);
            var x = Files(a);
            var y = Files(b);
            return x.Keys.Union(y.Keys, StringComparer.OrdinalIgnoreCase).Where(k => !k.Equals("OVERLAY.txt", StringComparison.OrdinalIgnoreCase))
                .Where(k => !x.ContainsKey(k) || !y.ContainsKey(k) || SameBytes(x[k], y[k]) == false).Order(StringComparer.Ordinal).ToList();
        }

        // ---- the other commands ----

        private void Tools()
        {
            const string A = "tools";
            var sc = Run("statcheck", cli, "statcheck", trees[0], "--max", "200");
            Expect(A, "statcheck", sc.Exit == 0, $"exit {sc.Exit} · {Brief(sc.Out, 2).Replace('\n', ' ')}");
            var rs = Run("results", cli, "results", "--max", "50");
            var missing = _compares.Where(c => !rs.Out.Contains(c.Id)).Select(c => c.Id).ToList();
            Expect(A, "results lists every compare", rs.Exit == 0 && missing.Count == 0, missing.Count == 0 ? $"{_compares.Count} listed" : "missing " + Few(missing));
            var rn = Run("runs", cli, "runs", "--max", "50");
            missing = _compares.Where(c => !rn.Out.Contains(c.Id)).Select(c => c.Id).ToList();
            Expect(A, "runs records every compare", rn.Exit == 0 && missing.Count == 0, missing.Count == 0 ? $"{_compares.Count} run records" : "missing " + Few(missing));
            if (_mcp is not null)
            {
                var t = _mcp.Call("get_run_telemetry", Args(("max", 50)));
                missing = _compares.Where(c => !t.Text.Contains(c.Id)).Select(c => c.Id).ToList();
                Expect(A, "get_run_telemetry records every compare", missing.Count == 0, missing.Count == 0 ? $"{_compares.Count} run records" : "missing " + Few(missing));
            }
        }

        // ---- metrics, history, json ----

        private List<Metric> Metrics()
        {
            var list = new List<Metric>();
            var saved = ResultStore.List(_results, 500);
            foreach (var (label, id) in _compares)
            {
                var dir = ResultStore.Find(_results, id);
                var run = dir is null ? null : Path.Combine(dir, "run.json");
                if (run is null || !File.Exists(run)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllBytes(run));
                var m = doc.RootElement;
                var p = m.TryGetProperty("process", out var pp) ? pp : default;
                var o = m.TryGetProperty("options", out var oo) ? oo : default;
                long read = 0, sides = 0, listed = 0;
                if (m.TryGetProperty("compares", out var cs))
                    foreach (var c in cs.EnumerateArray())
                    {
                        read += Long(c, "bytesRead");
                        sides += Long(c, "cacheSides");
                        listed = Math.Max(listed, Math.Max(Long(c, "listedLeft"), Long(c, "listedRight")));
                    }
                var mg = m.TryGetProperty("merge", out var mm) && mm.ValueKind == JsonValueKind.Object ? mm : default;
                var s = saved.FirstOrDefault(c => c.Id == id);
                list.Add(new Metric(label, id, Str(m, "kind"), Str(m, "ended"), Dbl(m, "elapsedSeconds"), read / 1048576.0, Dbl(p, "peakWorkingSetMB"),
                    Dbl(p, "allocatedMB"), Dbl(p, "cpuSeconds"), (int)Long(o, "threads"), Str(o, "cache"), sides, listed,
                    mg.ValueKind == JsonValueKind.Object ? Dbl(mg, "seconds") : null, mg.ValueKind == JsonValueKind.Object ? Dbl(mg, "allocatedMB") : null,
                    mg.ValueKind == JsonValueKind.Object && mg.TryGetProperty("filesMerged", out _) ? Long(mg, "filesMerged") : null,
                    s is null ? "" : RunSummary.Format(s)));
            }
            return list;
        }

        private List<JsonElement> History(List<Metric> metrics, double seconds)
        {
            var file = Path.Combine(repoDir, "history.jsonl");
            var line = JsonSerializer.Serialize(new
            {
                started = _started.ToString("yyyy-MM-dd HH:mm"), run = Path.GetFileName(runDir), tool = _version, trees = trees.Count,
                pass = _checks.Count(c => c.Verdict == Verdict.Pass), fail = _checks.Count(c => c.Verdict == Verdict.Fail),
                warn = _checks.Count(c => c.Verdict == Verdict.Warn), seconds = Math.Round(seconds, 1),
                compares = metrics.Select(m => new { m.Label, seconds = Math.Round(m.Seconds, 1), readMB = Math.Round(m.ReadMB, 1), peakMB = m.PeakMB, allocatedMB = m.AllocatedMB }),
            });
            File.AppendAllText(file, line + "\n", new UTF8Encoding(false));
            var all = new List<JsonElement>();
            foreach (var l in File.ReadLines(file).TakeLast(30))
                try { all.Add(JsonDocument.Parse(l).RootElement.Clone()); }
                catch (JsonException) { }
            return all;
        }

        private void WriteJson(List<Metric> metrics, double seconds)
        {
            var json = JsonSerializer.Serialize(new
            {
                format = "codediffer-selftest", version = 1, tool = _version, name, trees, started = _started.ToString("o"), seconds = Math.Round(seconds, 1),
                checks = _checks.Select(c => new { c.Area, c.Name, verdict = c.Verdict.ToString().ToLowerInvariant(), c.Detail }),
                metrics = metrics.Select(m => new { m.Label, m.Id, m.Kind, m.Ended, m.Seconds, m.ReadMB, m.PeakMB, m.AllocatedMB, m.CpuSeconds, m.Threads, m.Cache, m.CacheSides, m.MergeSeconds, m.MergeAllocatedMB }),
                steps = _steps.Select(s => new { s.Label, s.Command, s.Exit, s.Seconds, s.Log }),
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(runDir, "selftest.json"), json, new UTF8Encoding(false));
        }

        private static Dictionary<string, object?> Args(params (string Key, object? Value)[] args)
            => args.Where(a => a.Value is not null).ToDictionary(a => a.Key, a => a.Value);
    }

    private static long Long(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static double Dbl(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    /// <summary><paramref name="n"/> items spread evenly over the list (sorted by path, so the same files each run).</summary>
    internal static List<T> Pick<T>(IReadOnlyList<T> all, int n)
        => all.Count <= n ? [.. all] : [.. Enumerable.Range(0, n).Select(i => all[(int)((long)i * all.Count / n)])];

    /// <summary>Whether two files hold the same bytes; null when one is over 64 MB (not read) or can't be read.</summary>
    internal static bool? SameBytes(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists) return false;
            if (fa.Length != fb.Length) return false;
            if (fa.Length > 64 << 20) return null;
            using var sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var x = new byte[1 << 16];
            var y = new byte[1 << 16];
            while (true)
            {
                int n = sa.ReadAtLeast(x, x.Length, throwOnEndOfStream: false);
                int m = sb.ReadAtLeast(y, y.Length, throwOnEndOfStream: false);
                if (n != m || !x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, m))) return false;
                if (n == 0) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Why a modified file's bytes don't bear out its reason, or null when they do: they differ, and an eol change
    /// is alike without its CRs, a whitespace one alike without its whitespace, an encoding one alike as text.</summary>
    internal static string? ReasonHolds(string l, string r, ChangeReason reason)
    {
        if (SameBytes(l, r) == true) return "bytes are equal";
        if (Math.Max(new FileInfo(l).Length, new FileInfo(r).Length) > 64 << 20 || reason is ChangeReason.Content or ChangeReason.Binary or ChangeReason.Metadata) return null;
        byte[] a = File.ReadAllBytes(l), b = File.ReadAllBytes(r);
        return reason switch
        {
            ChangeReason.Eol => a.Where(x => x != '\r').SequenceEqual(b.Where(x => x != '\r')) ? null : "differs beyond line endings",
            ChangeReason.Whitespace => Strip(Decode(a)) == Strip(Decode(b)) ? null : "differs beyond whitespace",
            ChangeReason.Encoding => Decode(a) == Decode(b) || Decode(a).Replace("\r\n", "\n") == Decode(b).Replace("\r\n", "\n") ? null : "text differs",
            _ => null,
        };

        static string Strip(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    /// <summary>Text by its BOM (UTF-8, UTF-16 LE/BE), else UTF-8, without the BOM.</summary>
    private static string Decode(byte[] b)
        => b is [0xEF, 0xBB, 0xBF, ..] ? Encoding.UTF8.GetString(b, 3, b.Length - 3)
            : b is [0xFF, 0xFE, ..] ? Encoding.Unicode.GetString(b, 2, b.Length - 2)
            : b is [0xFE, 0xFF, ..] ? Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2)
            : Encoding.UTF8.GetString(b);

    private static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>A file as UTF-8 text (a BOM kept as U+FEFF), or null when it isn't valid UTF-8.</summary>
    internal static string? Utf8(string path)
    {
        try { return Strict.GetString(File.ReadAllBytes(path)); }
        catch (DecoderFallbackException) { return null; }
    }

    /// <summary>A stdio JSON-RPC client of the MCP server, as an agent's host runs it.</summary>
    private sealed class Mcp : IDisposable
    {
        private readonly Process _p;
        private readonly Task _stderr;
        private int _id;

        public string Version { get; }

        public Mcp(string exe, Action<ProcessStartInfo> isolate, string stderrLog)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            isolate(psi);
            _p = Process.Start(psi)!;
            var err = _p.StandardError;
            _stderr = Task.Run(() =>
            {
                using var w = new StreamWriter(stderrLog, false, new UTF8Encoding(false));
                while (err.ReadLine() is { } l) w.WriteLine(l); // drained, or the server blocks once the pipe fills
            });
            var init = Send("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "codediffer-selftest", version = "1" } });
            Version = init.GetProperty("serverInfo").GetProperty("version").GetString() ?? "?";
            _p.StandardInput.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
            _p.StandardInput.Flush();
        }

        public List<string> Tools()
            => [.. Send("tools/list", new { }).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!)];

        public (string Text, bool Error) Call(string tool, object args)
        {
            var r = Send("tools/call", new { name = tool, arguments = args });
            var text = string.Concat(r.GetProperty("content").EnumerateArray()
                .Where(c => c.GetProperty("type").GetString() == "text").Select(c => c.GetProperty("text").GetString()));
            return (text, r.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True);
        }

        private JsonElement Send(string method, object @params)
        {
            int id = ++_id;
            _p.StandardInput.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params }));
            _p.StandardInput.Flush();
            while (true)
            {
                var line = _p.StandardOutput.ReadLine() ?? throw new IOException($"the MCP server ended (exit {(_p.HasExited ? _p.ExitCode : -1)})");
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var rid) || rid.ValueKind != JsonValueKind.Number || rid.GetInt32() != id) continue;
                if (root.TryGetProperty("error", out var err)) throw new InvalidOperationException($"{method}: {err}");
                return root.GetProperty("result").Clone();
            }
        }

        public void Dispose()
        {
            try { _p.StandardInput.Close(); } catch (IOException) { }
            if (!_p.WaitForExit(15_000)) try { _p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _stderr.Wait(5_000);
            _p.Dispose();
        }
    }
}

/// <summary>
/// A unified-diff applier of the selftest's own, so a patch CodeDiffer wrote is checked by something that isn't
/// CodeDiffer: each hunk's context and removed lines must be left's lines exactly (line endings included), and the
/// result must be right's text exactly. No fuzz, no offsets.
/// </summary>
public static class PatchApplier
{
    private static readonly Regex Header = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

    /// <summary>The text's lines, split after each '\n' only (a '\r' stays in its line); no trailing empty line.</summary>
    public static List<string> Lines(string text)
    {
        var l = text.Split('\n').ToList();
        if (l.Count > 0 && l[^1].Length == 0) l.RemoveAt(l.Count - 1);
        return l;
    }

    /// <summary>Apply one file's patch lines (headers before the first hunk are skipped) to <paramref name="left"/>; null
    /// with <paramref name="why"/> when a hunk doesn't fit.</summary>
    public static string? Apply(string left, IReadOnlyList<string> patch, out string? why)
    {
        why = null;
        var src = new List<string>();
        for (int s = 0; s < left.Length;)
        {
            int nl = left.IndexOf('\n', s);
            int end = nl < 0 ? left.Length : nl + 1;
            src.Add(left[s..end]);
            s = end;
        }
        var outp = new StringBuilder(left.Length + 1024);
        int pos = 0, i = 0;
        while (i < patch.Count && !patch[i].StartsWith("@@ ", StringComparison.Ordinal)) i++;
        while (i < patch.Count)
        {
            var m = Header.Match(patch[i]);
            if (!m.Success) { why = $"bad hunk header '{patch[i]}'"; return null; }
            int oldStart = int.Parse(m.Groups[1].Value), oldLen = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
            int start = oldLen == 0 ? oldStart : oldStart - 1;
            if (start < pos || start > src.Count) { why = $"hunk at line {oldStart} is out of order or past the end"; return null; }
            for (; pos < start; pos++) outp.Append(src[pos]);
            for (i++; i < patch.Count && !patch[i].StartsWith("@@ ", StringComparison.Ordinal); i++)
            {
                var l = patch[i];
                if (l.StartsWith('\\')) continue;
                char op = l.Length > 0 ? l[0] : ' ';
                var full = (l.Length > 0 ? l[1..] : "") + (i + 1 < patch.Count && patch[i + 1].StartsWith('\\') ? "" : "\n");
                switch (op)
                {
                    case ' ' or '-':
                        if (pos >= src.Count || src[pos] != full) { why = $"line {pos + 1} is not the patch's"; return null; }
                        if (op == ' ') outp.Append(src[pos]);
                        pos++;
                        break;
                    case '+':
                        outp.Append(full);
                        break;
                    default:
                        why = $"unexpected patch line '{(l.Length > 40 ? l[..40] : l)}'";
                        return null;
                }
            }
        }
        for (; pos < src.Count; pos++) outp.Append(src[pos]);
        return outp.ToString();
    }

    /// <summary>The sections of a multi-file patch whose <c>diff --git</c> header names one of <paramref name="wanted"/>
    /// ("a/x b/y"), each as its lines; read a line at a time ('\n' only), so a large patch isn't held.</summary>
    public static Dictionary<string, List<string>> Sections(string patchFile, IReadOnlySet<string> wanted)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? cur = null;
        using var s = new FileStream(patchFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var line = new MemoryStream();
        int b;
        void End()
        {
            var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            line.SetLength(0);
            if (text.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var key = text["diff --git ".Length..];
                cur = wanted.Contains(key) ? found[key] = [] : null;
            }
            else if (text.StartsWith('#')) cur = null;
            else cur?.Add(text);
        }
        while ((b = s.ReadByte()) >= 0)
        {
            if (b == '\n') End();
            else line.WriteByte((byte)b);
        }
        if (line.Length > 0) End();
        return found;
    }
}
