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
