using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// A prune never deletes a result while a diff, patch, report or overlay is being written into it, and a write waits out a
/// prune already deleting it, so a write told it succeeded is in a listed result. The lock is a file opened shared or not,
/// which the system checks the same between two handles of one process as of two processes.
/// </summary>
public sealed class PruneLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-plock-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public PruneLockTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), new UTF8Encoding(false).GetBytes(text));
    }

    /// <summary>A finished, saved compare of one file whose diff is longer than 5 lines (a capped view writes it).</summary>
    private CompareSession Saved()
    {
        string Many(string tag) => string.Concat(Enumerable.Range(1, 60).Select(i => i % 3 == 0 ? $"{tag} {i}\n" : $"line {i}\n"));
        Put("L/x.txt", Many("old"));
        Put("R/x.txt", Many("new"));
        var s = new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.NotNull(s.ResultDir);
        return s;
    }

    [Fact]
    public void AHeldResult_IsNotPruned_UntilLetGo()
    {
        var s = Saved();
        var saved = Assert.Single(ResultStore.List(P("results")));
        using (ResultStore.Hold(s))
        {
            var ex = Assert.ThrowsAny<IOException>(() => ResultStore.Delete(P("results"), saved));
            Assert.Contains("in use", ex.Message);
            Assert.True(File.Exists(Path.Combine(s.ResultDir!, ResultStore.MetaName))); // untouched: refused before anything went
        }
        ResultStore.Delete(P("results"), saved);
        Assert.False(Directory.Exists(s.ResultDir));
        Assert.False(File.Exists(ResultStore.LockPath(s.ResultDir!))); // the prune's lock file goes with it
    }

    [Fact]
    public void WritersHoldOneResultTogether()
    {
        var s = Saved();
        using var a = ResultStore.Hold(s);
        using var b = ResultStore.Hold(s);
        // A capped diff written while two others hold the result: not held up by them.
        var t = Task.Run(() => AgentViews.FileDiff(s, "x.txt", maxLines: 5));
        Assert.True(t.Wait(TimeSpan.FromSeconds(10)));
        Assert.Contains("full patch:", t.Result);
    }

    [Theory]
    [InlineData("diff")]
    [InlineData("patch")]
    [InlineData("report")]
    public void AWrite_WaitsOutAPruneDeletingTheResult_AndSavesItAgain(string what)
    {
        var s = Saved();
        var lockFile = ResultStore.LockPath(s.ResultDir!);
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        Task<string> write;
        // A prune part way through deleting the result: it holds the lock unshared.
        using (new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            write = Task.Run(() => what switch
            {
                "diff" => AgentViews.FileDiff(s, "x.txt", maxLines: 5).Split("full patch: ")[1].Split('\n')[0],
                "patch" => AgentViews.Export(s).Split("changeset " + s.Id + ": ")[1].Split("  (")[0],
                _ => CodeDiffer.Core.Report.HtmlReport.Write(s).IndexPath,
            });
            Thread.Sleep(300);
            Assert.False(write.IsCompleted); // waits for the prune
            ResultStore.DeleteContents(new DirectoryInfo(s.ResultDir!), keep: null);
            Directory.Delete(s.ResultDir!);
        }
        Assert.True(write.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(File.Exists(write.Result), write.Result);
        Assert.Equal(s.Id, Assert.Single(ResultStore.List(P("results"))).Id); // saved again around it
    }

    [Fact]
    public void WritesAndPrunesAtOnce_NeverLeaveAWriteOutsideAListedResult()
    {
        var s = Saved();
        var root = P("results");
        int refused = 0, lost = 0;
        using var stop = new CancellationTokenSource();
        var pruner = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (ResultStore.List(root).FirstOrDefault() is not { } c) continue;
                try
                {
                    ResultStore.Delete(root, c);
                }
                catch (IOException ex) when (ex.Message.Contains("in use")) { refused++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
        });
        for (int i = 0; i < 300; i++)
        {
            string file;
            using (ResultStore.Hold(s))
            {
                file = AgentViews.FileDiff(s, "x.txt", maxLines: 5).Split("full patch: ")[1].Split('\n')[0];
                // Held: the prune can't take the patch, nor the result it is in, until this lets go.
                if (!File.Exists(file) || !File.Exists(Path.Combine(s.ResultDir!, ResultStore.MetaName))) lost++;
            }
        }
        stop.Cancel();
        pruner.Wait();
        Assert.Equal(0, lost);
        Assert.True(refused > 0, "the prune never met a held result");
        // Whatever the prune did last, no directory is left without compare.json (never listed, never pruned).
        if (Directory.Exists(s.ResultDir!)) Assert.True(File.Exists(Path.Combine(s.ResultDir!, ResultStore.MetaName)));
    }
}
