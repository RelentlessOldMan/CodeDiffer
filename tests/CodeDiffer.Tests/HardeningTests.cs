using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Model;
using CodeDiffer.Core.Sessions;
using CodeDiffer.Core.ThreeWay;
using CodeDiffer.Core.Verify;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Review round 8, tier 2: a whitespace reason only for whitespace, a pure rename paired by the best match rather than
/// list order, a verify gate that checks compare3 itself, and bad input (a malformed manifest, a corrupt saved result)
/// that is an error, never a crash.
/// </summary>
public sealed class HardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-harden-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };

    public HardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string rel, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), bytes);
    }

    private void Put(string rel, string text) => Put(rel, new UTF8Encoding(false).GetBytes(text));

    public static TheoryData<string, string, string> Reasons => new()
    {
        { "return x;\n", "returnx;\n", "content" },          // a space between two tokens is not "whitespace"
        { "a\nb\n", "ab\n", "content" },                     // nor is joining two lines
        { "a\nb\n", "a\n\nb\n", "content" },                 // nor a new blank line
        { "x y\n", "x y\n", "content" },                // NBSP is a character, not whitespace
        { "  foo();  \n", "\tfoo();\n", "whitespace" },      // reindented, trailing dropped
        { "a  b\n", "a b\n", "whitespace" },                 // an inner run narrowed
        { "int x=1;\n", "int  x = 1;\n", "whitespace" },     // spacing round punctuation
        { "f(a, b);\n", "f(a,b);\n", "whitespace" },
        { "int x;\n", "intx;\n", "content" },                // but never between two word characters
        { "é b\n", "éb\n", "content" },                      // non-ASCII letters are word characters, decoded or as bytes
        { "a\n  \nb\n", "a\n\nb\n", "whitespace" },          // a blank line's spaces
        { "a \r\nb\r\n", "a\nb\n", "whitespace" },           // trailing space and line endings
    };

    [Theory]
    [MemberData(nameof(Reasons))]
    public void TheWhitespaceReason_IsOnlyForWhitespace_AndTheSameStreamedOrWhole(string left, string right, string reason)
    {
        Put("l.txt", left);
        Put("r.txt", right);
        long ll = new FileInfo(P("l.txt")).Length, rl = new FileInfo(P("r.txt")).Length;
        Assert.Equal(reason, CanonicalTokens.Token(new ReasonClassifier().Classify(P("l.txt"), ll, P("r.txt"), rl)));
        // A large file is compared streamed, byte by byte: the same rule, whatever the size.
        Assert.Equal(reason, CanonicalTokens.Token(new ReasonClassifier(maxClassifyBytes: 1).Classify(P("l.txt"), ll, P("r.txt"), rl)));
    }

    [Fact]
    public void ANelByteInASingleByteCodePage_IsNotWhitespace()
    {
        // 0x85 (cp1252 "…") would decode through Latin-1 to U+0085 NEL, which char.IsWhiteSpace calls whitespace.
        Put("l.txt", [(byte)'a', 0x85, (byte)'b', (byte)'\n', 0xE9]);
        Put("r.txt", [(byte)'a', (byte)'b', (byte)'\n', 0xE9]);
        Assert.Equal(ChangeReason.Content, new ReasonClassifier().Classify(P("l.txt"), 5, P("r.txt"), 4));
    }

    [Fact]
    public void IdenticalFiles_PairByTheBestMatch_NotByListOrder()
    {
        // a/LICENSE and b/LICENSE are identical; b/LICENSE was renamed to b/COPYING and a/ deleted.
        Put("L/a/LICENSE", "MIT\n");
        Put("L/b/LICENSE", "MIT\n");
        Put("R/b/COPYING", "MIT\n");
        var r = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        var rename = Assert.Single(r.Changes, c => c.Status == ChangeStatus.Renamed);
        Assert.Equal(("b/LICENSE", "b/COPYING"), (rename.RenamedFrom, rename.RelativePath));
        Assert.Contains(r.Changes, c => c.Status == ChangeStatus.Removed && c.RelativePath == "a/LICENSE");
    }

    [Fact]
    public void Verify_ChecksCompare3Itself_NotOnlyTheContractsLineMetric()
    {
        // Without line endings v1 is unchanged and v2 adds a line — clean. With them (what compare3 merges) v1 added the
        // final newline and v2 added it and "c": a conflict the manifest doesn't have.
        Put("B/f.txt", "a\nb");
        Put("V1/f.txt", "a\nb\n");
        Put("V2/f.txt", "a\nb\nc");
        var manifest = new ConflictManifest(1, null, null, [], [new CleanMerge("f.txt", "v2", HunkOp.Insert, 2, 0, 3, 1)], null);
        var r3 = TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache);
        var c3 = ConflictTreeCrossCheck.Compare3(r3, manifest);
        Assert.False(c3.Ok);
        Assert.Equal(["f.txt"], c3.ConflictOnlyInCompare3);

        // ... and a conflict anywhere else, in a file the manifest never names, fails it too.
        Put("B/g.txt", "1\n2\n3\n");
        Put("V1/g.txt", "1\nX\n3\n");
        Put("V2/g.txt", "1\nY\n3\n");
        Put("V1/f.txt", "a\nb");
        Put("V2/f.txt", "a\nb\nc");
        var again = ConflictTreeCrossCheck.Compare3(TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache), manifest);
        Assert.Equal(["g.txt"], again.ConflictOnlyInCompare3);

        Put("V2/g.txt", "1\n2\n3\n");
        Assert.True(ConflictTreeCrossCheck.Compare3(TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache), manifest).Ok);
    }

    [Theory]
    [InlineData("""{"_meta":{}}""")]                                                                  // no manifestVersion
    [InlineData("""{"_meta":{"manifestVersion":"1"}}""")]                                             // the wrong kind
    [InlineData("""{"_meta":{"manifestVersion":1},"conflicts":[{"path":"a","baseStart":1}]}""")]       // a field missing
    [InlineData("""{"_meta":{"manifestVersion":1},"mergedClean":[{"path":7}]}""")]
    [InlineData("""[]""")]
    public void AMalformedConflictManifest_IsAFormatError(string json)
        => Assert.Throws<FormatException>(() => ConflictManifestParser.Parse(json));

    [Theory]
    [InlineData("""{"_meta":{"manifestVersion":1},"fileOps":{"renamed":[{"from":"a"}]}}""")]
    [InlineData("""{"_meta":{"manifestVersion":1},"fileOps":{"modified":[{"path":"a","reason":"content","hunks":[{"op":"insert"}]}]}}""")]
    [InlineData("""{"_meta":{"manifestVersion":1},"fileOps":{"modified":[{"path":null}]}}""")]
    public void AMalformedDeltaManifest_IsAFormatError(string json)
        => Assert.Throws<FormatException>(() => DeltaManifestParser.Parse(json));

    [Fact]
    public void HunksOutsideTheFiles_FailTheCheck_NeverCrashIt()
    {
        string[] old = ["a", "b"], @new = ["a", "c"];
        Assert.True(HunkApplier.Rebuilds(old, @new, [new Hunk(HunkOp.Replace, 2, 1, 2, 1)]));
        Assert.False(HunkApplier.Rebuilds(old, @new, [new Hunk(HunkOp.Replace, 2, 5, 2, 1)]));   // past the old file
        Assert.False(HunkApplier.Rebuilds(old, @new, [new Hunk(HunkOp.Replace, 2, 1, 9, 1)]));   // past the new file
        Assert.False(HunkApplier.Rebuilds(old, @new, [new Hunk(HunkOp.Replace, 2, 1, 2, 1), new Hunk(HunkOp.Replace, 1, 2, 1, 2)])); // overlapping
    }

    [Fact]
    public void AManifestFileMissingFromTheTree_FailsTheCrossCheck_NeverCrashesIt()
    {
        Directory.CreateDirectory(P("B"));
        Directory.CreateDirectory(P("V"));
        var m = DeltaManifestParser.Parse("""
            {"_meta":{"manifestVersion":1},"fileOps":{"modified":[{"path":"gone.c","reason":"content","oldSha":"x","newSha":"y",
             "oldSize":1,"newSize":1,"hunks":[{"op":"replace","oldStart":1,"oldLines":1,"newStart":1,"newLines":1}]}]}}
            """);
        var cc = DeltaTreeCrossCheck.Run(P("B"), P("V"), m);
        Assert.False(cc.Ok);
        Assert.NotNull(Assert.Single(cc.Files).Problem);
    }

    [Fact]
    public void ACorruptSavedResult_IsSaidToBeCorrupt_NeverAnUnhandledError()
    {
        Put("L/f.txt", "one\n");
        Put("R/f.txt", "two\n");
        var results = P("results");
        var s = new SessionStore(resultsRoot: results).Start(P("L"), P("R"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        Assert.Null(s.Error);

        // A status that isn't one (a number would pass Enum.Parse as an undefined value).
        var changes = Path.Combine(s.ResultDir!, "changes.jsonl");
        var good = File.ReadAllText(changes);
        foreach (var status in new[] { "bogus", "7" })
        {
            File.WriteAllText(changes, good.Replace("\"status\":\"modified\"", $"\"status\":\"{status}\""));
            Assert.Throws<InvalidDataException>(() => new SessionStore(resultsRoot: results).Get(s.Id));
        }
        File.WriteAllText(changes, good);

        // A number out of range in compare.json: skipped by the list, an error to open.
        var meta = Path.Combine(s.ResultDir!, ResultStore.MetaName);
        var json = File.ReadAllText(meta);
        Assert.Contains("\"elapsedSeconds\"", json);
        var elapsed = System.Text.RegularExpressions.Regex.Replace(json, "\"elapsedSeconds\": *[0-9.eE+-]+", "\"elapsedSeconds\": 1e300");
        foreach (var bad in new[] { elapsed, json.Replace("\"started\": \"", "\"started\": \"99999-") })
        {
            File.WriteAllText(meta, bad);
            Assert.Empty(ResultStore.List(results));
            Assert.Throws<InvalidDataException>(() => ResultStore.Load(s.ResultDir!));
        }
    }
}
