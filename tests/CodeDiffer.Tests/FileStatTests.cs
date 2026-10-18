using CodeDiffer.Core.Walk;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The live stat: by name (no handle) and on an open handle give the same size, times and id; both follow an edit;
/// a path past MAX_PATH works; a missing file still says why.
/// </summary>
public class FileStatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-stat-" + Guid.NewGuid().ToString("N"));

    public FileStatTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(DirectoryLister.LongPath(_dir), true); } catch { } }

    private static FileStat ByHandle(string path)
    {
        using var h = File.OpenHandle(DirectoryLister.LongPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return FileStat.Of(h, path);
    }

    [Fact]
    public void ByName_MatchesTheHandle_AndFollowsAnEdit()
    {
        var p = Path.Combine(_dir, "a.txt");
        File.WriteAllText(p, "one\n");
        var first = FileStat.OfPath(p);
        Assert.Equal(ByHandle(p), first);
        Assert.Equal(4, first.Length);
        Assert.Equal(new FileInfo(p).LastWriteTimeUtc.Ticks, first.LastWriteUtcTicks);
        if (OperatingSystem.IsWindows()) { Assert.NotEqual(0, first.ChangeUtcTicks); Assert.NotEqual(0, first.FileId); }

        File.SetLastWriteTimeUtc(p, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.AppendAllText(p, "two\n");
        File.SetLastWriteTimeUtc(p, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var after = FileStat.OfPath(p);
        Assert.Equal(ByHandle(p), after);
        Assert.Equal(8, after.Length);
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks, after.LastWriteUtcTicks);
        Assert.Equal(first.FileId, after.FileId);
    }

    [Fact]
    public void ALongPath_IsStatted()
    {
        var deep = _dir;
        while (deep.Length < 300) deep = Path.Combine(deep, new string('d', 40));
        Directory.CreateDirectory(DirectoryLister.LongPath(deep));
        var p = Path.Combine(deep, "long.txt");
        File.WriteAllText(DirectoryLister.LongPath(p), "x");
        Assert.Equal(1, FileStat.OfPath(p).Length);
        Assert.Equal(ByHandle(p), FileStat.OfPath(p));
    }

    [Fact]
    public void AMissingFile_SaysWhy()
        => Assert.Contains("cannot open", Assert.ThrowsAny<IOException>(() => FileStat.OfPath(Path.Combine(_dir, "gone.txt"))).Message);
}
