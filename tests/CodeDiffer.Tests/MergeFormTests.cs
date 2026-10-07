using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// compare3 merges a file's encoding and line endings 3-way, like its lines: a side that changed them wins (v2's
/// LF→CRLF or added BOM is not lost when v1 also edited the file), both changing them differently is a conflict,
/// and a mixed-ending file keeps each line's ending instead of being flattened.
/// </summary>
public sealed class MergeFormTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-form-" + Guid.NewGuid().ToString("N"));
    private readonly string _b, _v1, _v2;
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };
    private static readonly string Ten = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));
    private static readonly Encoding Utf8 = new UTF8Encoding(false), Utf8Bom = new UTF8Encoding(true), Utf16 = new UnicodeEncoding(false, true);

    public MergeFormTests()
    {
        _b = Path.Combine(_dir, "B");
        _v1 = Path.Combine(_dir, "V1");
        _v2 = Path.Combine(_dir, "V2");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Crlf(string s) => s.Replace("\n", "\r\n");

    private void Put(string root, string text, Encoding? enc = null)
    {
        Directory.CreateDirectory(root);
        enc ??= Utf8;
        File.WriteAllBytes(Path.Combine(root, "f.c"), [.. enc.GetPreamble(), .. enc.GetBytes(text)]);
    }

    /// <summary>Merge f.c (base, v1, v2) and return its entry and the bytes the merge writes.</summary>
    private (Merge3Entry E, byte[]? Bytes) Merge(string b, string v1, string v2, Encoding? eb = null, Encoding? e1 = null, Encoding? e2 = null)
    {
        Put(_b, b, eb);
        Put(_v1, v1, e1);
        Put(_v2, v2, e2);
        var e = TreeMerger.Run(_b, _v1, _v2, NoCache).Entries.Single(x => x.Path == "f.c");
        return (e, TreeMerger.MergedBytes(e, _b, _v1, _v2));
    }

    private static string Edit1(string s) => s.Replace("line 2", "line 2 v1");
    private static string Edit2(string s) => s.Replace("line 9", "line 9 v2");

    [Fact]
    public void V2sLineEndingConversion_IsKept_WhenV1AlsoEdited()
    {
        var (e, bytes) = Merge(Ten, Edit1(Ten), Crlf(Edit2(Ten)));
        Assert.Equal(Merge3Outcome.Merged, e.Outcome);
        Assert.Equal(Crlf(Edit2(Edit1(Ten))), Utf8.GetString(bytes!));
        Assert.Contains("v2 changed the line endings (LF → CRLF)", e.Note);
    }

    [Fact]
    public void V1sLineEndingConversion_IsKept_WhenV2AlsoEdited()
    {
        var (e, bytes) = Merge(Crlf(Ten), Edit1(Ten), Crlf(Edit2(Ten)));
        Assert.Equal(Merge3Outcome.Merged, e.Outcome);
        Assert.Equal(Edit2(Edit1(Ten)), Utf8.GetString(bytes!));
    }

    [Fact]
    public void V2sAddedBom_IsKept()
    {
        var (e, bytes) = Merge(Ten, Edit1(Ten), Edit2(Ten), e2: Utf8Bom);
        Assert.Equal(Merge3Outcome.Merged, e.Outcome);
        Assert.Equal([.. Utf8Bom.GetPreamble(), .. Utf8.GetBytes(Edit2(Edit1(Ten)))], bytes!);
        Assert.Contains("v2 changed the encoding (UTF-8 → UTF-8 with BOM)", e.Note);
    }

    [Fact]
    public void V2sSwitchToUtf16_IsKept()
    {
        var (e, bytes) = Merge(Ten, Edit1(Ten), Edit2(Ten), e2: Utf16);
        Assert.Equal(Merge3Outcome.Merged, e.Outcome);
        Assert.Equal([.. Utf16.GetPreamble(), .. Utf16.GetBytes(Edit2(Edit1(Ten)))], bytes!);
    }

    [Fact]
    public void BothChangingTheEncodingDifferently_IsAConflict()
    {
        var (e, _) = Merge(Ten, Edit1(Ten), Edit2(Ten), e1: Utf8Bom, e2: Utf16);
        Assert.Equal(Merge3Outcome.Conflict, e.Outcome);
        Assert.Equal("encoding", e.ConflictKind);
        Assert.Contains("v1 changed the encoding to UTF-8 with BOM, v2 to UTF-16", e.Note);
    }

    [Fact]
    public void BothChangingTheLineEndingsDifferently_IsAConflict()
    {
        var mixed = Crlf(Ten[..Ten.IndexOf("line 6")]) + Ten[Ten.IndexOf("line 6")..];
        var (e, _) = Merge(mixed, Edit1(Ten), Crlf(Edit2(Ten)));
        Assert.Equal(Merge3Outcome.Conflict, e.Outcome);
        Assert.Equal("line endings", e.ConflictKind);
        Assert.Equal(0, e.ConflictRegions);
    }

    [Fact]
    public void AnEncodingConflict_IsStillSaid_WhenTheLinesConflictToo()
    {
        var (e, bytes) = Merge(Ten, Ten.Replace("line 5", "v1 five"), Ten.Replace("line 5", "v2 five"), e1: Utf8Bom, e2: Utf16);
        Assert.Equal(("content", 1), (e.ConflictKind, e.ConflictRegions)); // written with markers...
        Assert.Contains("encoding conflict too", e.Note);              // ...but the encoding conflict is not lost
        Assert.Contains("v1 changed the encoding to UTF-8 with BOM, v2 to UTF-16", e.Note);
        Assert.Equal(Utf8Bom.GetPreamble(), bytes![..3]);                // in v1's form, as the note says
    }

    [Fact]
    public void BothConvertingTheSameWay_Agrees()
    {
        var (e, bytes) = Merge(Ten, Crlf(Edit1(Ten)), Crlf(Edit2(Ten)));
        Assert.Equal(Merge3Outcome.Merged, e.Outcome);
        Assert.Equal(Crlf(Edit2(Edit1(Ten))), Utf8.GetString(bytes!));
    }

    [Fact]
    public void AMixedFile_KeepsEachLinesEnding()
    {
        // All three mixed alike: merged as is, every line keeps its own ending.
        var mixed = Crlf(Ten[..Ten.IndexOf("line 6")]) + Ten[Ten.IndexOf("line 6")..];
        var (e, bytes) = Merge(mixed, Edit1(mixed), Edit2(mixed));
        Assert.Null(e.Note);
        Assert.Equal(Edit2(Edit1(mixed)), Utf8.GetString(bytes!));
    }

    [Fact]
    public void V1MakingItMixed_IsNotFlattened()
    {
        // v1 adds one CRLF line (the file becomes mixed); v2 edits elsewhere. The merge keeps v1's CRLF line and
        // v2's LF edit — it used to come out all LF.
        var v1 = Ten.Replace("line 2\n", "line 2 v1\r\n");
        var (e, bytes) = Merge(Ten, v1, Edit2(Ten));
        Assert.Equal(Merge3Outcome.Merged, e.Outcome);
        Assert.Equal(Edit2(v1), Utf8.GetString(bytes!));
    }

    [Fact]
    public void V2MakingItMixed_KeepsV2sEndings_AndV1sEdit()
    {
        var v2 = Ten.Replace("line 7\n", "line 7\r\n").Replace("line 9\n", "line 9 v2\r\n");
        var (e, bytes) = Merge(Ten, Edit1(Ten), v2);
        Assert.Equal(Merge3Outcome.Merged, e.Outcome);
        Assert.Equal(Edit1(v2), Utf8.GetString(bytes!));
    }

    [Fact]
    public void ALoneCarriageReturn_StaysInsideItsLine()
    {
        var b = Ten.Replace("line 5\n", "x\ry\n");
        var (e, bytes) = Merge(b, Edit1(b), Crlf(Edit2(b)));
        Assert.Equal(Merge3Outcome.Merged, e.Outcome);
        Assert.Equal(Crlf(Edit2(Edit1(b))), Utf8.GetString(bytes!));
    }

    [Fact]
    public void ConflictMarkers_FollowTheWinningLineEndings()
    {
        var (e, bytes) = Merge(Ten, Ten.Replace("line 5", "five v1"), Crlf(Ten.Replace("line 5", "five v2")));
        Assert.Equal("content", e.ConflictKind);
        var text = Utf8.GetString(bytes!);
        Assert.Contains("<<<<<<< v1 (f.c:5)\r\nfive v1\r\n", text);
        Assert.DoesNotMatch("[^\r]\n", text);
    }

    [Fact]
    public void AnEncodingConflict_IsNotWrittenByTheOverlay()
    {
        Merge(Ten, Edit1(Ten), Edit2(Ten), e1: Utf8Bom, e2: Utf16);
        var r = TreeMerger.Run(_b, _v1, _v2, NoCache);
        var outDir = Path.Combine(_dir, "overlay");
        var o = MergeOverlay.Write(r, _b, _v1, _v2, outDir);
        Assert.Equal(1, o.Unresolved);
        Assert.False(File.Exists(Path.Combine(outDir, "files", "f.c")));
        Assert.Contains("encoding  f.c", File.ReadAllText(Path.Combine(outDir, "conflicts.txt")));
    }
}
