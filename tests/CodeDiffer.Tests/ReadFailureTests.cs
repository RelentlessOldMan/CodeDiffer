using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>Why a file couldn't be read, counted by side and cause with no names: what a warning can say about it.</summary>
public sealed class ReadFailureTests
{
    private static IOException Win32(int code) => new("translated message") { HResult = unchecked((int)0x80070000) | code };

    [Fact]
    public void Causes_ComeFromTheWindowsCode_NotTheMessage()
    {
        Assert.Equal("denied", ReadFailure.Cause(new UnauthorizedAccessException("x")));
        Assert.Equal("in use", ReadFailure.Cause(Win32(32)));
        Assert.Equal("in use", ReadFailure.Cause(Win32(33)));
        Assert.Equal("missing", ReadFailure.Cause(new FileNotFoundException("x")));
        Assert.Equal("missing", ReadFailure.Cause(Win32(3)));
        Assert.Equal("path too long", ReadFailure.Cause(new PathTooLongException("x")));
        Assert.Equal("offline", ReadFailure.Cause(Win32(4350)));
        Assert.Equal("offline", ReadFailure.Cause(Win32(362)));
        Assert.Equal("missing", ReadFailure.Cause(Win32(2)));
        Assert.Equal("Windows error 359", ReadFailure.Cause(Win32(359))); // a device in maintenance, not a cloud file
        // CodeDiffer's own stat and listing errors carry their code too.
        Assert.Equal("network", ReadFailure.Cause(CodeDiffer.Core.Walk.DirectoryLister.Win32IOException("cannot stat x (win32 error 64)", 64)));
        Assert.Equal("network", ReadFailure.Cause(Win32(64)));
        Assert.Equal("disk error", ReadFailure.Cause(Win32(1117)));
        Assert.Equal("Windows error 1920", ReadFailure.Cause(Win32(1920)));
        Assert.Equal("IOException", ReadFailure.Cause(new IOException("no code")));
    }

    [Fact]
    public void ANote_CarriesTheCauseAndSide_ThenTheMessage()
    {
        Assert.Equal("[left, in use] translated message", ReadFailure.Note(Win32(32), "left"));
        Assert.Equal("[denied] x", ReadFailure.Note(new UnauthorizedAccessException("x")));
    }

    [Fact]
    public void APairNote_FindsTheSideThatFails()
    {
        using var t = new TempDir();
        t.Put("ok.txt", "a");
        Assert.StartsWith("[right, missing] ", ReadFailure.PairNote(t.P("ok.txt"), t.P("gone.txt"), Win32(2)));
        Assert.StartsWith("[left, missing] ", ReadFailure.PairNote(t.P("gone.txt"), t.P("ok.txt"), Win32(2)));
        // Both open now (it was a moment's lock): the first error's cause, no side.
        Assert.Equal("[in use] translated message", ReadFailure.PairNote(t.P("ok.txt"), t.P("ok.txt"), Win32(32)));
    }

    [Fact]
    public void TheTally_NamesTheSides_AndCountsEachCause()
    {
        FileChange Row(string path, ChangeStatus s, string note, bool link = false) => new(path, s, null, 1, 1, Unreadable: note, BehindLink: link);
        var r = new CompareReport([
            Row("a", ChangeStatus.Modified, "[right, denied] x"),
            Row("b", ChangeStatus.Modified, "[right, denied] x"),
            Row("c", ChangeStatus.Removed, "[in use] x"),    // a remove is the left tree's file
            Row("d", ChangeStatus.Added, "[offline] x"),     // an add the right's
            Row("e", ChangeStatus.Modified, "[network] x"),  // a pair that opened again: side unknown
            Row("f", ChangeStatus.Added, "behind a symlink", link: true),
            Row("g", ChangeStatus.Modified, "saved before causes were"),
            new("h", ChangeStatus.Modified, ChangeReason.Content, 1, 2),
        ]);
        Assert.Equal("v2 denied 2 · a side network 1 · base in use 1 · cause not recorded 1 · " +
                     "not followed (link, or a name Windows can't open) 1 · v2 offline 1",
            ReadFailure.Line(ReadFailure.Tally(r, "base", "v2")));
        Assert.Equal("", ReadFailure.Line(ReadFailure.Tally(new CompareReport([]))));
    }
}
