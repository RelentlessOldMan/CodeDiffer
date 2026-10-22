using System.Text.Json;
using System.Text.RegularExpressions;
using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>Nothing CodeDiffer ships reaches the internet: no network calls or launched programs in its source, no web
/// links in its report page, and only the libraries it has always had (a new one could bring its own).</summary>
public sealed class NoInternetTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CodeDiffer.slnx"))) return d.FullName;
        throw new InvalidOperationException("repo root (CodeDiffer.slnx) not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> Shipped(params string[] patterns)
        => patterns.SelectMany(p => Directory.EnumerateFiles(Path.Combine(Root, "src"), p, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    [Fact]
    public void TheSource_HasNoNetworkCallsOrLaunchedPrograms()
    {
        var banned = new Regex(@"System\.Net\b|HttpClient|WebClient|WebRequest|\bSocket\b|TcpClient|UdpClient|\bDns\.|ProcessStartInfo|Process\.Start|https?://");
        var hits = Shipped("*.cs").SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
            .Where(t => banned.IsMatch(t.line)).Select(t => $"{Path.GetRelativePath(Root, t.f)}:{t.i + 1}: {t.line.Trim()}").ToList();
        Assert.True(hits.Count == 0, "network code in the shipped source:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void TheReportPage_LoadsOnlyItsOwnFiles()
    {
        var page = Shipped("*.html").Single();
        var outside = new Regex(@"https?://|//[a-z0-9-]+\.[a-z]|@import|fetch\(|XMLHttpRequest|WebSocket|sendBeacon", RegexOptions.IgnoreCase);
        var hits = File.ReadLines(page).Where(l => outside.IsMatch(l)).ToList();
        Assert.True(hits.Count == 0, "the report page reaches outside its folder:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void OnlyTheKnownLibraries_AreReferenced()
    {
        var packages = Shipped("*.csproj").SelectMany(f => Regex.Matches(File.ReadAllText(f), "PackageReference Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Microsoft.Extensions.Hosting", "ModelContextProtocol", "System.IO.Hashing"], packages);
    }

    /// <summary>Every package that ships, the ones those pull in too: a new one anywhere is looked at before it ships
    /// (Carver's internet lookups came from a library, not its own code).</summary>
    [Fact]
    public void EveryLibraryPulledIn_IsAKnownOne()
    {
        var pulled = new[] { "CodeDiffer.Core", "CodeDiffer.Cli", "CodeDiffer.Mcp" }.SelectMany(p =>
            {
                using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "src", p, "obj", "project.assets.json")));
                return assets.RootElement.GetProperty("libraries").EnumerateObject()
                    .Where(l => l.Value.GetProperty("type").GetString() == "package").Select(l => l.Name.Split('/')[0]).ToList();
            })
            .Distinct().Order(StringComparer.Ordinal).ToList();
        string[] known =
        [
            "Microsoft.Extensions.AI.Abstractions", "Microsoft.Extensions.Caching.Abstractions",
            "Microsoft.Extensions.Configuration", "Microsoft.Extensions.Configuration.Abstractions", "Microsoft.Extensions.Configuration.Binder",
            "Microsoft.Extensions.Configuration.CommandLine", "Microsoft.Extensions.Configuration.EnvironmentVariables",
            "Microsoft.Extensions.Configuration.FileExtensions", "Microsoft.Extensions.Configuration.Json", "Microsoft.Extensions.Configuration.UserSecrets",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Diagnostics", "Microsoft.Extensions.Diagnostics.Abstractions",
            "Microsoft.Extensions.FileProviders.Abstractions", "Microsoft.Extensions.FileProviders.Physical", "Microsoft.Extensions.FileSystemGlobbing",
            "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Hosting.Abstractions",
            "Microsoft.Extensions.Logging", "Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.Logging.Configuration",
            "Microsoft.Extensions.Logging.Console", "Microsoft.Extensions.Logging.Debug", "Microsoft.Extensions.Logging.EventLog",
            "Microsoft.Extensions.Logging.EventSource", "Microsoft.Extensions.Options", "Microsoft.Extensions.Options.ConfigurationExtensions",
            "Microsoft.Extensions.Primitives", "ModelContextProtocol", "ModelContextProtocol.Core", "System.Diagnostics.EventLog", "System.IO.Hashing",
        ];
        Assert.Equal(known.Order(StringComparer.Ordinal), pulled);
    }

    /// <summary>The real CLI, run as a user runs it — compare with an HTML report, compare3, an apply dry run — never
    /// loads .NET's networking code, so nothing in it (its own code or a library's) reached the network.</summary>
    [Fact]
    public void TheCli_NeverLoadsTheNetworkingCode()
    {
        using var t = new TempDir();
        t.Put("B/a.txt", "one\ntwo\nthree\n");
        t.Put("L/a.txt", "one\nTWO\nthree\n");
        t.Put("R/a.txt", "one\ntwo\nTHREE\n");
        t.Put("T/a.txt", "one\ntwo\nthree\n");
        foreach (var (name, args) in new[]
                 {
                     ("compare", new[] { "compare", t.P("L"), t.P("R"), "--html" }),
                     ("compare3", ["compare3", t.P("B"), t.P("L"), t.P("R")]),
                     ("apply", ["apply", t.P("B"), t.P("L"), t.P("T")]),
                 })
        {
            var list = t.P($"loaded-{name}.txt");
            var r = ExeRunner.Run(ExeRunner.Exe("CodeDiffer.Cli.exe"), t.Root, t.P("state"), args,
                configure: psi => psi.Environment[LoadedList.Variable] = list);
            Assert.True(r.ExitCode is 0 or 1, r.ToString()); // 1: compare3 / apply found something to report
            AssertNoNetworking(list, "codediffer " + name);
        }
    }

    /// <summary>The libraries in <paramref name="list"/> (written by the exe as it exited) include none that talks to
    /// a network: web requests, sockets, name lookups, TLS.</summary>
    internal static void AssertNoNetworking(string list, string what)
    {
        Assert.True(File.Exists(list), $"{what} wrote no list of the libraries it loaded");
        var loaded = File.ReadAllLines(list);
        Assert.Contains("CodeDiffer.Core", loaded); // the list is the real one
        var net = loaded.Where(n => n.StartsWith("System.Net.", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Assert.True(net.Count == 0, $"{what} loaded networking code: {string.Join(", ", net)}");
    }
}
