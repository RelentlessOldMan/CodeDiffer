using CodeDiffer.Core.Model;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>The file type of a path: a known name first, then the extension, then whether its bytes are binary.</summary>
public class FileTypeTests
{
    [Theory]
    [InlineData("src/engine/render.cpp", FileType.Code)]
    [InlineData("include/Thing.H", FileType.Code)]
    [InlineData("tools/setup.ps1", FileType.Code)]
    [InlineData("shaders/blur.hlsl", FileType.Code)]
    [InlineData("CMakeLists.txt", FileType.Build)]       // a .txt, but by its name a build file
    [InlineData("lib/cmakelists.TXT", FileType.Build)]
    [InlineData("py/requirements.txt", FileType.Build)]
    [InlineData("Makefile", FileType.Build)]
    [InlineData("proj/game.vcxproj.filters", FileType.Build)]
    [InlineData("cfg/settings.json", FileType.Build)]
    [InlineData(".gitignore", FileType.Build)]
    [InlineData("obj/render.o.d", FileType.Build)]        // a make dependency list, not D source
    [InlineData("obj/Debug/link.tlog", FileType.Build)]
    [InlineData("sub/.editorconfig", FileType.Build)]
    [InlineData("src/notes.txt", FileType.Docs)]          // a .txt among the .c files is still docs
    [InlineData("README", FileType.Docs)]
    [InlineData("docs/guide.md", FileType.Docs)]
    [InlineData("data/table.weird", FileType.Docs)]       // text of a kind not known
    [InlineData("noextension", FileType.Docs)]
    [InlineData("art/logo.png", FileType.Binary)]
    [InlineData("bin/tool.dll", FileType.Binary)]
    public void TypeOf_Path(string path, FileType type) => Assert.Equal(type, FileTypes.Of(path));

    [Fact]
    public void BinaryBytes_WinOverTheName()
    {
        Assert.Equal(FileType.Binary, FileTypes.Of("src/blob.c", binary: true));
        Assert.Equal(FileType.Binary, new FileChange("a/b.txt", ChangeStatus.Modified, ChangeReason.Binary, 1, 2).Type);
        Assert.Equal(FileType.Docs, new FileChange("a/b.txt", ChangeStatus.Modified, ChangeReason.Content, 1, 2).Type);
    }

    [Fact]
    public void Tokens_RoundTrip()
    {
        foreach (var t in Enum.GetValues<FileType>())
        {
            Assert.True(FileTypes.TryParse(FileTypes.Token(t).ToUpperInvariant(), out var back));
            Assert.Equal(t, back);
        }
        Assert.False(FileTypes.TryParse("source", out _));
    }
}
