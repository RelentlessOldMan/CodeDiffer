using CodeDiffer.Core.Compare;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>The default thread count: more when a tree is on a share, where a file is mostly a wait on the server.</summary>
public sealed class ThreadDefaultTests
{
    private static readonly string Local = Path.GetTempPath();

    [Fact]
    public void LocalTrees_GetTheLocalCount()
    {
        Assert.Equal(Math.Min(Environment.ProcessorCount, 8), CompareOptions.DefaultThreadsFor(Local, Local));
        Assert.Equal(CompareOptions.LocalThreads, new CompareOptions().Parallelism);
    }

    [Fact]
    public void AnyTreeOnAShare_GetsTheShareCount()
    {
        if (!OperatingSystem.IsWindows()) return; // a UNC path is a share only there
        Assert.True(CompareOptions.ShareThreads > CompareOptions.LocalThreads);
        Assert.Equal(CompareOptions.ShareThreads, CompareOptions.DefaultThreadsFor(Local, @"\\nosuchserver\share\tree"));
        Assert.Equal(CompareOptions.ShareThreads, CompareOptions.DefaultThreadsFor(@"\\nosuchserver\share\base", Local, Local));
    }

    [Fact]
    public void APathThatDoesNotParse_CountsAsLocal_AndDoesNotThrow()
        => Assert.Equal(CompareOptions.LocalThreads, CompareOptions.DefaultThreadsFor(Local, "bad\0path"));
}
