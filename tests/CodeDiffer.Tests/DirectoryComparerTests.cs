using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// End-to-end 2-way compare tests on real temp-dir fixtures. The reason cases double as the regression
/// suite for the prototype-review bugs: an EOL-only or encoding-only change must NOT read as a content
/// change (the "modified with an empty diff" bug), and a one-sided binary flip must be caught as binary.
/// </summary>
public sealed class DirectoryComparerTests : IDisposable
{
    private readonly string _left = NewTempDir();
    private readonly string _right = NewTempDir();

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"codediffer-cmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void WriteLeft(string rel, string text) => Write(_left, rel, Encoding.UTF8.GetBytes(text));
    private void WriteRight(string rel, string text) => Write(_right, rel, Encoding.UTF8.GetBytes(text));

    private void WriteLeft(string rel, byte[] bytes) => Write(_left, rel, bytes);
    private void WriteRight(string rel, byte[] bytes) => Write(_right, rel, bytes);

    private static void Write(string root, string rel, byte[] bytes)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private CompareReport Run() => new DirectoryComparer().Compare(_left, _right);

    private FileChange Change(CompareReport r, string path) =>
        r.Changes.Single(c => c.RelativePath == path);

    public void Dispose()
    {
        try { Directory.Delete(_left, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_right, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void AddedRemovedIdentical_AreClassified()
    {
        WriteLeft("gone.txt", "bye\n");
        WriteLeft("same.txt", "steady\n");
        WriteRight("same.txt", "steady\n"); // byte-identical
        WriteRight("new.txt", "hi\n");

        var r = Run();
        Assert.Equal(ChangeStatus.Removed, Change(r, "gone.txt").Status);
        Assert.Equal(ChangeStatus.Identical, Change(r, "same.txt").Status);
        Assert.Equal(ChangeStatus.Added, Change(r, "new.txt").Status);
    }

    [Fact]
    public void ContentChange_IsModifiedContent()
    {
        WriteLeft("a.c", "line1\nline2\n");
        WriteRight("a.c", "line1\nCHANGED\n");
        var c = Change(Run(), "a.c");
        Assert.Equal(ChangeStatus.Modified, c.Status);
        Assert.Equal(ChangeReason.Content, c.Reason);
    }

    [Fact]
    public void EolOnlyChange_IsEol_NotContent()
    {
        WriteLeft("a.c", "x\ny\nz\n");
        WriteRight("a.c", "x\r\ny\r\nz\r\n"); // same text, CRLF
        var c = Change(Run(), "a.c");
        Assert.Equal(ChangeStatus.Modified, c.Status);
        Assert.Equal(ChangeReason.Eol, c.Reason); // regression: never "content with empty diff"
    }

    [Fact]
    public void WhitespaceOnlyChange_IsWhitespace()
    {
        WriteLeft("a.c", "int x=1;\n");
        WriteRight("a.c", "int  x = 1;\n"); // only spacing changed
        var c = Change(Run(), "a.c");
        Assert.Equal(ChangeReason.Whitespace, c.Reason);
    }

    [Fact]
    public void EncodingOnlyChange_IsEncoding()
    {
        const string text = "héllo wörld\nsecond line\n";
        WriteLeft("a.txt", Encoding.UTF8.GetBytes(text)); // UTF-8, no BOM

        // UTF-16 LE WITH BOM, same text.
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
        WriteRight("a.txt", utf16);

        var c = Change(Run(), "a.txt");
        Assert.Equal(ChangeStatus.Modified, c.Status);
        Assert.Equal(ChangeReason.Encoding, c.Reason);
    }

    [Fact]
    public void OneSidedBinary_IsBinary_NotTextDiff()
    {
        WriteLeft("blob.dat", "hello world\n");               // text
        WriteRight("blob.dat", new byte[] { 1, 2, 0, 3, 4 }); // contains NUL -> binary
        var c = Change(Run(), "blob.dat");
        Assert.Equal(ChangeStatus.Modified, c.Status);
        Assert.Equal(ChangeReason.Binary, c.Reason); // regression: no text diff with raw NULs
    }

    [Fact]
    public void IgnoredDirectory_IsSkipped()
    {
        WriteLeft(".git/config", "left\n");
        WriteRight(".git/config", "right\n"); // differs, but .git is ignored by default
        Assert.DoesNotContain(Run().Changes, c => c.RelativePath.StartsWith(".git/"));
    }

    [Fact]
    public void ALegacyEncodedEdit_IsContent_NotEncoding()
    {
        // cp1252 / Latin-1: 0xB0 '°' → 0xB1 '±'. Both are invalid UTF-8; decoded leniently they'd be "the same text".
        WriteLeft("temp.c", [.. "// 25"u8, 0xB0, .. "C\n"u8]);
        WriteRight("temp.c", [.. "// 25"u8, 0xB1, .. "C\n"u8]);
        // The same text in UTF-8 and in Latin-1 is an encoding change.
        WriteLeft("e.txt", "caf\u00e9\n");
        WriteRight("e.txt", [.. "caf"u8, 0xE9, (byte)'\n']);
        var r = Run();
        Assert.Equal(ChangeReason.Content, Change(r, "temp.c").Reason);
        Assert.Equal(ChangeReason.Encoding, Change(r, "e.txt").Reason);
    }

    [Fact]
    public void ALockedFile_IsUnreadable_AndTheCompareFinishes()
    {
        WriteLeft("locked.c", "same size\n");
        WriteRight("locked.c", "SAME SIZE\n");
        WriteLeft("grown.c", "short\n");
        WriteRight("grown.c", "a bit longer\n");
        WriteLeft("log.txt", "one\n");
        WriteRight("log.txt", "one\ntwo\n");
        WriteLeft("ok.c", "fine\n");
        WriteRight("ok.c", "FINE\n");
        using var locked = new FileStream(Path.Combine(_right, "locked.c"), FileMode.Open, FileAccess.Read, FileShare.None);
        using var grown = new FileStream(Path.Combine(_right, "grown.c"), FileMode.Open, FileAccess.Read, FileShare.None);
        // A log being appended to (writer allows readers and writers) must still be readable.
        using var log = new FileStream(Path.Combine(_right, "log.txt"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        var r = Run();
        var l = Change(r, "locked.c");
        Assert.Equal((ChangeStatus.Modified, null, "unreadable"), (l.Status, l.Reason, l.ReasonLabel));
        Assert.NotNull(l.Unreadable);
        Assert.Equal("unreadable", Change(r, "grown.c").ReasonLabel);
        Assert.Equal(ChangeReason.Content, Change(r, "log.txt").Reason);
        Assert.Equal(ChangeReason.Content, Change(r, "ok.c").Reason);
        Assert.Equal(2, r.UnreadableFiles);
    }

    [Fact]
    public void EmptyFiles_AreNeverPairedAsARename_AndTheSameNameWinsAmongIdenticals()
    {
        WriteLeft("pkgA/__init__.py", "");
        WriteRight("pkgZ/__init__.py", "");
        WriteLeft("src/util.c", "shared body\n");
        WriteRight("lib/aaa.c", "shared body\n");   // first by path, but another name
        WriteRight("lib/util.c", "shared body\n");  // same name: this is the rename
        var r = Run();
        Assert.Equal(ChangeStatus.Removed, Change(r, "pkgA/__init__.py").Status);
        Assert.Equal(ChangeStatus.Added, Change(r, "pkgZ/__init__.py").Status);
        Assert.Equal("src/util.c", Change(r, "lib/util.c").RenamedFrom);
        Assert.Equal(ChangeStatus.Added, Change(r, "lib/aaa.c").Status);
    }

    [Fact]
    public void ADriveRoot_KeepsItsSeparator()
    {
        var drive = Path.GetPathRoot(_left)!; // e.g. C:\ — "C:" alone would mean the current directory on C:
        Assert.Equal(drive, CodeDiffer.Core.Walk.TreeWalker.RootOf(drive));
        Assert.Equal(_left, CodeDiffer.Core.Walk.TreeWalker.RootOf(_left + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Changes_AreSortedByPathOrdinal()
    {
        WriteLeft("b.txt", "1\n");
        WriteLeft("a.txt", "1\n");
        WriteRight("c.txt", "1\n");
        var paths = Run().Changes.Select(c => c.RelativePath).ToArray();
        var sorted = paths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, paths);
    }
}
