using System.Text.RegularExpressions;
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
}
