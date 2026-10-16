using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace CodeDiffer.Tests;

/// <summary>
/// The saved-results store is ON by default (and the MCP server's store reads its root once, when the tool class is first
/// touched), so without this a test that saves — every MCP tool test — would write into the real
/// %LOCALAPPDATA%\CodeDiffer\results. Point it at a per-run temp dir before any test runs (TestIsolation does the same for
/// the hash ledgers).
/// </summary>
internal static class ResultsIsolation
{
    // A level down, so the directory holding it is not %TEMP% itself, which holds every test's trees (a test that asks
    // whether an output dir "holds the results directory" must not find it holds a compared tree first).
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "cd-tests-results-" + Environment.ProcessId);
    internal static readonly string Root = Path.Combine(Base, "results");

    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("CODEDIFFER_RESULTS_DIR", Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Base, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        };
    }
}

/// <summary>A temp directory of fixture trees, deleted at the end; paths are '/'-relative to it.</summary>
internal sealed class TempDir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cd-exe-" + Guid.NewGuid().ToString("N")[..12]);

    public TempDir() => Directory.CreateDirectory(Root);

    public string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    public string Put(string rel, string text) => PutBytes(rel, new UTF8Encoding(false).GetBytes(text));

    public string PutBytes(string rel, byte[] bytes)
    {
        var p = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    public string Read(string rel) => File.ReadAllText(P(rel));

    public string Dir(string rel)
    {
        var p = P(rel);
        Directory.CreateDirectory(p);
        return p;
    }

    public static string Lines(int from, int to, string prefix = "line") =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"{prefix} {i}\n"));

    /// <summary>Every file under a tree (relative path -> content hash): what a read-only command must leave as it was.</summary>
    public static SortedDictionary<string, string> Snapshot(string tree)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
            d[Path.GetRelativePath(tree, f).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
        foreach (var dir in Directory.EnumerateDirectories(tree, "*", SearchOption.AllDirectories))
            d[Path.GetRelativePath(tree, dir).Replace('\\', '/') + "/"] = "dir";
        return d;
    }

    /// <summary>Copy a tree (for a target to apply onto, or a git apply check).</summary>
    public static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
    }

    public void Dispose()
    {
        for (int i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}

internal sealed record ExeResult(int ExitCode, string Stdout, string Stderr)
{
    public override string ToString() => $"exit {ExitCode}\n--- stdout ---\n{Stdout}\n--- stderr ---\n{Stderr}";
}

/// <summary>Runs an exe built next to the test dll as a child process, with its own isolated state dirs.</summary>
internal static class ExeRunner
{
    public static string Exe(string name)
    {
        var p = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(p), $"{name} is not next to the test dll ({AppContext.BaseDirectory}); build the solution");
        return p;
    }

    /// <summary>The environment every child gets: results, ledgers and overflow files all under <paramref name="state"/>.</summary>
    public static void Isolate(ProcessStartInfo psi, string state)
    {
        psi.Environment["CODEDIFFER_RESULTS_DIR"] = Path.Combine(state, "results");
        psi.Environment["CODEDIFFER_CACHE_DIR"] = Path.Combine(state, "cache");
        psi.Environment["CODECOMPASS_CACHE_DIR"] = Path.Combine(state, "compass");
        psi.Environment["CODEDIFFER_OUT_DIR"] = Path.Combine(state, "out");
    }

    public static ExeResult Run(string exe, string workDir, string state, IEnumerable<string> args, int timeoutMs = 120_000,
        Action<ProcessStartInfo>? configure = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Isolate(psi, state);
        configure?.Invoke(psi);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Assert.Fail($"{Path.GetFileName(exe)} {string.Join(' ', args)} did not finish in {timeoutMs} ms");
        }
        p.WaitForExit();
        return new ExeResult(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    /// <summary>git, if it is on PATH (else null: the test that needs it says so and passes).</summary>
    public static string? Git()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), OperatingSystem.IsWindows() ? "git.exe" : "git");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
