using System.Security.Cryptography;
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
/// Review round 10, tier 1: prune never deletes through a link; the results, overlay and report gates see a tree by
/// any of its names; a saved path that could reach outside its tree is a corrupt result; verify checks shas and sizes
/// against the trees, each 3-way region against the trees, and that compare3's merged files are in the manifest; a
/// UTF-8 BOM excuses no NUL; a path UTF-8 can't spell is left out of a patch.
/// </summary>
public sealed class Round10Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-r10-" + Guid.NewGuid().ToString("N"));
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };
    private static readonly string Ten = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));

    public Round10Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string P(string rel) => Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string rel, string text) => PutBytes(rel, new UTF8Encoding(false).GetBytes(text));

    private void PutBytes(string rel, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllBytes(P(rel), bytes);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>A junction (no admin rights needed); false off Windows.</summary>
    private bool Junction(string rel, string targetRel)
    {
        if (!OperatingSystem.IsWindows()) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        using var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{P(rel)}\" \"{P(targetRel)}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        mk.WaitForExit();
        Assert.True(Directory.Exists(P(rel)), "mklink /J failed");
        return true;
    }

    // ---- results / links ----

    [Fact]
    public void Prune_NeverDeletesThroughALinkInTheResultsDirectory()
    {
        var started = DateTime.UtcNow - TimeSpan.FromDays(30);
        Directory.CreateDirectory(P("archive/kept"));
        File.WriteAllText(P("archive/kept/" + ResultStore.MetaName), $$"""
            { "format": "{{ResultStore.Format}}", "version": {{ResultStore.FormatVersion}}, "kind": "compare", "id": "beef",
              "state": "done", "started": "{{started:O}}", "roots": { "left": "L", "right": "R" } }
            """);
        File.WriteAllText(P("archive/kept/changes.jsonl"), "keep me");
        Directory.CreateDirectory(P("results"));
        if (!Junction("results/20200101-000000-beef", "archive/kept")) return;

        Assert.Empty(ResultStore.PruneCandidates(P("results"), 0, null, DateTime.UtcNow));
        Assert.DoesNotContain(ResultStore.List(P("results")), c => c.Id == "beef");
        var linked = new SavedCompare(P("results/20200101-000000-beef"), "beef", "compare", "done", started, TimeSpan.Zero, [], [], null);
        Assert.Throws<ArgumentException>(() => ResultStore.Delete(P("results"), linked));
        Assert.Equal("keep me", File.ReadAllText(P("archive/kept/changes.jsonl")));
    }

    [Fact]
    public void AResultsDirectoryInsideATreeNamedByAJunction_IsRefused()
    {
        Put("realL/a.c", Ten);
        if (!Junction("jl", "realL")) return;
        var ex = Assert.Throws<ArgumentException>(() => ResultStore.CreateRunDir(P("realL/res"), "abcd", "compare", [P("jl"), P("R")]));
        Assert.Contains("inside the compared tree", ex.Message);
        Assert.False(Directory.Exists(P("realL/res")));
        Assert.Throws<ArgumentException>(() => MergeOverlay.CheckDir(P("realL/ov"), P("jl"), P("V1"), P("V2")));
    }

    [Theory]
    [InlineData("../outside.c")]
    [InlineData("sub/../../outside.c")]
    [InlineData("C:/outside.c")]
    [InlineData("/outside.c")]
    public void ASavedPathThatCouldReachOutsideItsTree_IsACorruptResult(string bad)
    {
        Put("L/a.c", Ten);
        Put("R/a.c", Ten.Replace("line 3", "line three"));
        var s = new SessionStore(resultsRoot: P("results")).Start(P("L"), P("R"), NoCache);
        Assert.True(s.Wait(TimeSpan.FromMinutes(1)));
        var changes = Path.Combine(s.ResultDir!, "changes.jsonl");
        var text = File.ReadAllText(changes);
        Assert.Contains("\"a.c\"", text);
        File.WriteAllText(changes, text.Replace("\"a.c\"", System.Text.Json.JsonSerializer.Serialize(bad)));
        Assert.Throws<InvalidDataException>(() => ResultStore.Load(s.ResultDir!));
    }

    // ---- verify ----

    [Fact]
    public void Verify_ShasAndSizesAreCheckedAgainstTheTrees()
    {
        Put("B/a.c", Ten);
        Put("V/a.c", Ten.Replace("line 3", "line three"));
        var hunks = new[] { new Hunk(HunkOp.Replace, 3, 1, 3, 1) };
        FileDelta Rec(string o, string n, long os, long ns) => new("a.c", ChangeReason.Content, o, n, os, ns, hunks, []);
        DeltaManifest M(FileDelta f) => new(1, [], [], [], [f], null);
        long bs = new FileInfo(P("B/a.c")).Length, vs = new FileInfo(P("V/a.c")).Length;

        Assert.True(DeltaTreeCrossCheck.Run(P("B"), P("V"), M(Rec(Sha(P("B/a.c")), Sha(P("V/a.c")), bs, vs))).Ok);
        var wrongSha = DeltaTreeCrossCheck.Run(P("B"), P("V"), M(Rec(new string('0', 64), Sha(P("V/a.c")), bs, vs)));
        Assert.False(wrongSha.Ok);
        Assert.Contains("base", Assert.Single(wrongSha.ContentMismatches));
        var wrongSize = DeltaTreeCrossCheck.Run(P("B"), P("V"), M(Rec(Sha(P("B/a.c")), Sha(P("V/a.c")), bs, vs + 1)));
        Assert.Contains("variant", Assert.Single(wrongSize.ContentMismatches));
    }

    [Fact]
    public void Verify3_ACleanEditPassedOffAsAConflict_Fails()
    {
        Put("B/x.c", "a\nb\nc\nd\ne\n");
        Put("V1/x.c", "a\nB1\nc\nD1\ne\n");
        Put("V2/x.c", "a\nB2\nc\nd\ne\n");
        var m = ThreeWayMerger.Merge("x.c", File.ReadAllText(P("B/x.c")), File.ReadAllText(P("V1/x.c")), File.ReadAllText(P("V2/x.c")));
        Assert.Single(m.Conflicts);
        Assert.Single(m.CleanMerges);

        // No digest, so the fallback decides: the true decomposition passes it...
        var truth = new ConflictManifest(1, "base_v1", "base_v2", m.Conflicts, m.CleanMerges, ConflictTruthSha: null);
        var ok = ConflictTreeCrossCheck.Run(P("B"), P("V1"), P("V2"), truth);
        Assert.True(ok.Ok, string.Join("; ", ok.UnsoundRegions));

        // ...v1's clean edit of d listed as a conflict (v2 "replacing" d with its unchanged d) does not.
        var wrong = truth with { Conflicts = [.. m.Conflicts, new Conflict("x.c", 4, 1, HunkOp.Replace, 4, 1, HunkOp.Replace, 4, 1)], CleanMerges = [] };
        var cc = ConflictTreeCrossCheck.Run(P("B"), P("V1"), P("V2"), wrong);
        Assert.True(cc.Reconstructs);
        Assert.True(cc.ConflictPathsMatch);
        Assert.False(cc.Ok);
        Assert.Contains("v2 left the base as it was", Assert.Single(cc.UnsoundRegions));
    }

    [Fact]
    public void Verify3_AManifestLeavingOutACleanlyMergedFile_Fails()
    {
        Put("B/x.c", Ten);
        Put("V1/x.c", Ten.Replace("line 5", "v1 five"));
        Put("V2/x.c", Ten.Replace("line 5", "v2 five"));
        Put("B/y.c", Ten);
        Put("V1/y.c", Ten.Replace("line 2", "v1 two"));
        Put("V2/y.c", Ten.Replace("line 8", "v2 eight"));
        string T(string p) => File.ReadAllText(P(p));
        var x = ThreeWayMerger.Merge("x.c", T("B/x.c"), T("V1/x.c"), T("V2/x.c"));
        var y = ThreeWayMerger.Merge("y.c", T("B/y.c"), T("V1/y.c"), T("V2/y.c"));
        var r = TreeMerger.Run(P("B"), P("V1"), P("V2"), NoCache);

        var full = new ConflictManifest(1, "base_v1", "base_v2", x.Conflicts, [.. x.CleanMerges, .. y.CleanMerges], null);
        Assert.True(ConflictTreeCrossCheck.Compare3(r, full).Ok);
        var c3 = ConflictTreeCrossCheck.Compare3(r, full with { CleanMerges = x.CleanMerges });
        Assert.False(c3.Ok);
        Assert.Equal(["y.c"], c3.MergedNotInManifest);
    }

    // ---- patches ----

    [Fact]
    public void AUtf8BomExcusesNoNul_ButAUtf16BomDoes()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        Assert.True(TextInspector.LooksBinary([.. bom, (byte)'a', 0, 1, (byte)'\n']));
        Assert.False(TextInspector.LooksBinary(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("a\r\n")).ToArray()));

        PutBytes("L/f.txt", [.. bom, .. "a\nb\n"u8]);
        PutBytes("R/f.txt", [.. bom, .. "a\n\0\0\u0001\u0002b\n"u8]);
        var r = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        Assert.Equal(ChangeReason.Binary, Assert.Single(r.Changes, c => c.Status == ChangeStatus.Modified).Reason);
        var w = new StringWriter { NewLine = "\n" };
        PatchWriter.Write(w, r, P("L"), P("R"));
        Assert.DoesNotContain('\0', w.ToString());
    }

    [Fact]
    public void APathUtf8CantSpell_IsLeftOutOfAPatch_AndTheRestStillApplies()
    {
        if (!OperatingSystem.IsWindows()) return;
        Put("L/ok.txt", Ten);
        Put("R/ok.txt", Ten.Replace("line 4", "line four"));
        var odd = "x\uD800.txt"; // an unpaired high surrogate: a legal NTFS name
        Put("R/" + odd, "new\n");
        var r = new DirectoryComparer(NoCache).Compare(P("L"), P("R"));
        Assert.Contains(r.Changes, c => c.RelativePath == odd);

        var w = new StringWriter { NewLine = "\n" };
        var stats = PatchWriter.Write(w, r, P("L"), P("R"));
        var patch = w.ToString();
        Assert.Equal(1, stats.NameFiles);
        Assert.Equal(1, stats.TextFiles);
        Assert.Contains("UTF-8 can't spell", patch);
        Assert.Contains("diff --git a/ok.txt b/ok.txt", patch);
        Assert.DoesNotContain("diff --git a/x", patch);
        Assert.Contains("path(s) UTF-8 can't spell", stats.Summary(false));
    }
}
