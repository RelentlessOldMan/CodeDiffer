using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.ThreeWay;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// Conflicts settled in bulk before an overlay is applied: <c>resolve</c> takes v1's or v2's file (or keeps v1's to
/// regenerate) for every conflict a rule picks, by editing the overlay, so apply-overlay gives the chosen tree; undo
/// takes a choice back to the overlay as written.
/// </summary>
public sealed class ConflictResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-resolve-" + Guid.NewGuid().ToString("N"));
    private readonly string _b, _v1, _v2;
    private static readonly CompareOptions NoCache = new() { Cache = CacheMode.Off };
    private static readonly string Ten = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));

    public ConflictResolverTests()
    {
        _b = Path.Combine(_dir, "B");
        _v1 = Path.Combine(_dir, "V1");
        _v2 = Path.Combine(_dir, "V2");
        foreach (var d in new[] { "B", "V1", "V2" })
        {
            Put($"{d}/same.c", "untouched\n");
            Put($"{d}/src/clash.c", Ten);
            Put($"{d}/gen/regs.h", Ten);
            Put($"{d}/blob.bin", "\0\u0001base");
            Put($"{d}/model.c", "x\n");
            Put($"{d}/kept.c", "y\n");
            Put($"{d}/old/r.c", Ten);
            Put($"{d}/v2only.c", "b\n");
        }
        Put("V1/src/clash.c", Ten.Replace("line 5\n", "five v1\n"));      // content: markers
        Put("V2/src/clash.c", Ten.Replace("line 5\n", "five v2\n"));
        Put("V1/gen/regs.h", Ten.Replace("line 3\n", "three v1\n"));      // content: markers
        Put("V2/gen/regs.h", Ten.Replace("line 3\n", "three v2\n"));
        Put("V1/blob.bin", "\0\u0001v1!");                                // binary: not written
        Put("V2/blob.bin", "\0\u0001v2!!");
        File.Delete(Path.Combine(_v1, "model.c"));                        // delete/modify: v1 deleted
        Put("V2/model.c", "x changed\n");
        Put("V1/kept.c", "y changed\n");                                  // modify/delete: v2 deleted
        File.Delete(Path.Combine(_v2, "kept.c"));
        Put("V1/old/r.c", Ten.Replace("line 5\n", "r five v1\n"));        // v2 renames and edits the same line
        Directory.CreateDirectory(Path.Combine(_v2, "new"));
        File.Delete(Path.Combine(_v2, "old", "r.c"));
        Put("V2/new/r.c", Ten.Replace("line 5\n", "r five v2\n"));
        Put("V2/v2only.c", "B from v2\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Put(string rel, string text)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(text));
    }

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel));

    private string Overlay()
    {
        var r = TreeMerger.Run(_b, _v1, _v2, NoCache);
        var dir = Path.Combine(_dir, "overlay-" + Guid.NewGuid().ToString("N")[..6]);
        MergeOverlay.Write(r, _b, _v1, _v2, dir);
        return dir;
    }

    /// <summary>apply-overlay onto a fresh copy of v1.</summary>
    private string Apply(string overlay)
    {
        var target = Path.Combine(_dir, "M-" + Guid.NewGuid().ToString("N")[..6]);
        foreach (var f in Directory.EnumerateFiles(_v1, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(_v1, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
        var a = OverlayApplier.Run(overlay, target, write: true);
        Assert.Empty(a.Failed);
        return target;
    }

    private static ResolveResult Resolve(string overlay, string choice, string[] patterns, bool write = true, string[]? kinds = null)
        => ConflictResolver.Run(overlay, choice, patterns, kinds ?? [], write);

    [Fact]
    public void TheOverlayRecordsItsConflicts()
    {
        var s = OverlayConflicts.Load(Overlay());
        Assert.Equal(["blob.bin", "gen/regs.h", "kept.c", "model.c", "old/r.c", "src/clash.c"], s.All.Select(c => c.Path));
        var clash = s.All.Single(c => c.Path == "src/clash.c");
        Assert.Equal(("content", "src/clash.c", "src/clash.c", "src/clash.c"), (clash.Kind, clash.Markers, clash.V1At, clash.V2At));
        var r = s.All.Single(c => c.Path == "old/r.c");
        Assert.Equal(("new/r.c", "old/r.c", "old/r.c", "new/r.c"), (r.Markers, r.AlsoDelete, r.V1At, r.V2At));
        var model = s.All.Single(c => c.Path == "model.c");
        Assert.Equal(("delete/modify", null, null, "model.c"), (model.Kind, model.Markers, model.V1At, model.V2At));
        var kept = s.All.Single(c => c.Path == "kept.c");
        Assert.Equal(("modify/delete", "kept.c", null), (kept.Kind, kept.V1At, kept.V2At));
        Assert.Empty(s.Decisions);

        var text = s.Text([], []);
        Assert.Contains("6 conflict(s) in", text);
        Assert.Contains("6 undecided · 0 decided", text);
        Assert.Matches(@"content \(markers in files\\\)\s+3", text);
        Assert.Matches(@"gen/\s+1", text);
        Assert.Matches(@"\(top\)\s+3", text);
    }

    [Fact]
    public void TakeV2_ForAll_GivesV2sFiles()
    {
        var o = Overlay();
        var dry = Resolve(o, "v2", [], write: false);
        Assert.Equal(6, dry.Done.Count);
        Assert.False(File.Exists(Path.Combine(o, OverlayConflicts.DecisionsName)));
        Assert.Contains("would be decided", ConflictResolver.Text(dry));

        var r = Resolve(o, "v2", []);
        Assert.Empty(r.Refused);
        Assert.Equal(0, r.Undecided);
        var m = Apply(o);
        Assert.Equal(Read(_v2, "src/clash.c"), Read(m, "src/clash.c"));
        Assert.Equal(Read(_v2, "gen/regs.h"), Read(m, "gen/regs.h"));
        Assert.Equal("\0\u0001v2!!", Read(m, "blob.bin"));
        Assert.Equal("x changed\n", Read(m, "model.c"));
        Assert.False(File.Exists(Path.Combine(m, "kept.c")));           // v2 deleted it
        Assert.Equal(Read(_v2, "new/r.c"), Read(m, "new/r.c"));         // v2's rename and edit
        Assert.False(File.Exists(Path.Combine(m, "old", "r.c")));
        Assert.Equal("B from v2\n", Read(m, "v2only.c"));               // the clean merge is untouched by it
        Assert.Equal("untouched\n", Read(m, "same.c"));
    }

    [Fact]
    public void TakeV1_KeepsV1sFiles_AndARenamesDeleteIsDropped()
    {
        var o = Overlay();
        Assert.Contains("old/r.c", File.ReadAllLines(Path.Combine(o, "deletes.txt")));
        var r = Resolve(o, "v1", ["*.c"]);
        Assert.Equal(["kept.c", "model.c", "old/r.c", "src/clash.c"], r.Done.Select(d => d.Path));
        Assert.DoesNotContain("old/r.c", File.ReadAllLines(Path.Combine(o, "deletes.txt")));
        Assert.True(File.Exists(Path.Combine(o, OverlayConflicts.SetAsideDir, "src", "clash.c")));

        var m = Apply(o);
        Assert.Equal(Read(_v1, "src/clash.c"), Read(m, "src/clash.c"));
        Assert.Equal(Read(_v1, "old/r.c"), Read(m, "old/r.c"));
        Assert.False(File.Exists(Path.Combine(m, "new", "r.c")));
        Assert.False(File.Exists(Path.Combine(m, "model.c")));
        Assert.Equal("y changed\n", Read(m, "kept.c"));
        Assert.Contains("<<<<<<< v1", Read(m, "gen/regs.h"));          // not picked: keeps its markers
        Assert.Equal("\0\u0001v1!", Read(m, "blob.bin"));

        var s = OverlayConflicts.Load(o);
        Assert.Equal(["blob.bin", "gen/regs.h"], s.Undecided.Select(c => c.Path));
        Assert.Contains("4 v1", s.Text([], []));
    }

    [Fact]
    public void Regenerate_KeepsV1_AndIsListedAsSuch()
    {
        var o = Overlay();
        var r = Resolve(o, "regenerate", ["gen"]);
        Assert.Equal(["gen/regs.h"], r.Done.Select(d => d.Path));
        var m = Apply(o);
        Assert.Equal(Read(_v1, "gen/regs.h"), Read(m, "gen/regs.h"));
        var listed = OverlayConflicts.Load(o).Text(["gen"], [], list: true);
        Assert.Contains("regenerate gen/regs.h  (rule: gen)", listed);
    }

    [Fact]
    public void Undo_PutsTheOverlayBackAsWritten()
    {
        var o = Overlay();
        var before = Snapshot(o);
        Resolve(o, "v2", ["src/**", "old"]);
        Resolve(o, "v1", [], kinds: ["delete/modify", "modify/delete"]);
        Assert.NotEqual(before, Snapshot(o));
        var u = Resolve(o, "undo", []);
        Assert.Equal(4, u.Done.Count);
        Assert.Equal(before, Snapshot(o));
        Assert.Empty(OverlayConflicts.Load(o).Decisions);
        Assert.Contains("<<<<<<< v1", Read(Apply(o), "src/clash.c"));
    }

    /// <summary>files\ and deletes.txt, as path → bytes.</summary>
    private static string Snapshot(string o)
    {
        var files = Path.Combine(o, "files");
        return string.Join("\n", Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                   .Select(f => Path.GetRelativePath(files, f) + "=" + Convert.ToHexString(File.ReadAllBytes(f))))
               + "\n--\n" + File.ReadAllText(Path.Combine(o, "deletes.txt"));
    }

    [Fact]
    public void AChoiceIsKept_UntilUndone()
    {
        var o = Overlay();
        Resolve(o, "v1", ["src/clash.c"]);
        var again = Resolve(o, "v2", ["src/clash.c"]);
        Assert.Empty(again.Done);
        Assert.Equal(1, again.AlreadyDecided);
        Assert.Equal("v1", OverlayConflicts.Load(o).Decisions["src/clash.c"].Choice);
    }

    [Fact]
    public void TakeV2_RefusesAV2FileChangedSinceTheOverlay()
    {
        var o = Overlay();
        Put("V2/blob.bin", "\0\u0001changed after the compare");
        var r = Resolve(o, "v2", ["blob.bin"]);
        Assert.Empty(r.Done);
        Assert.Contains("changed since the overlay was written", Assert.Single(r.Refused).Why);
        Assert.Equal("\0\u0001v1!", Read(Apply(o), "blob.bin"));
    }

    [Fact]
    public void AStoppedResolve_BlocksApply_AndTheNextResolveFinishesIt()
    {
        var o = Overlay();
        // A resolve that stopped after its journal: nothing moved yet.
        var plan = Resolve(o, "v2", ["src"], write: false);
        File.WriteAllText(Path.Combine(o, OverlayConflicts.ResolvingName), Journal(plan.Done));
        var target = Path.Combine(_dir, "T");
        Directory.CreateDirectory(target);
        Assert.Contains("stopped part way", Assert.Throws<ArgumentException>(() => OverlayApplier.Run(o, target, write: false)).Message);
        Assert.Throws<ArgumentException>(() => Resolve(o, "v1", ["gen"], write: false));

        var next = Resolve(o, "v1", ["gen"]);
        Assert.True(next.FinishedStopped);
        var s = OverlayConflicts.Load(o);
        Assert.Equal("v2", s.Decisions["src/clash.c"].Choice);
        Assert.Equal("v1", s.Decisions["gen/regs.h"].Choice);
        Assert.Equal(Read(_v2, "src/clash.c"), Read(Apply(o), "src/clash.c"));
    }

    private static string Journal(IReadOnlyList<ConflictDecision> ds)
    {
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteBoolean("undo", false);
            w.WriteStartArray("decisions");
            foreach (var d in ds) OverlayConflicts.WriteDecision(w, d);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public void AnAppliedOverlay_IsRefused()
    {
        var o = Overlay();
        Apply(o);
        Assert.Contains("has been applied", Assert.Throws<ArgumentException>(() => Resolve(o, "v1", ["src"])).Message);
    }

    [Fact]
    public void AnOverlayWithoutConflictsJson_IsRefused()
    {
        var o = Overlay();
        File.Delete(Path.Combine(o, OverlayConflicts.FileName));
        Assert.Contains("older CodeDiffer", Assert.Throws<ArgumentException>(() => OverlayConflicts.Load(o)).Message);
    }

    [Theory]
    [InlineData("*.h", "gen/regs.h", true)]
    [InlineData("*.h", "gen/regs.c", false)]
    [InlineData("gen", "a/gen/regs.h", true)]
    [InlineData("gen", "a/generated/regs.h", false)]
    [InlineData("gen/", "gen/x/regs.h", true)]
    [InlineData("src/gen", "src/gen/x/regs.h", true)]
    [InlineData("src/gen", "a/src/gen/regs.h", false)]
    [InlineData("src/**/regs_*.h", "src/regs_a.h", true)]
    [InlineData("src/**/regs_*.h", "src/a/b/regs_a.h", true)]
    [InlineData("src/**/regs_*.h", "src/a/b/regs_a.c", false)]
    [InlineData("src/*.c", "src/a/x.c", false)]
    [InlineData("src\\gen", "src/gen/x.h", true)]
    [InlineData("r?.c", "a/r1.c", true)]
    public void Patterns(string pattern, string path, bool matches)
    {
        var c = new OverlayConflict(path, "content", null, null, null, path, path, 1, 1, false);
        Assert.Equal(matches, new ConflictMatcher([pattern], []).Matches(c));
    }

    [Fact]
    public void Kinds_FilterAndCombineWithPatterns()
    {
        var bin = new OverlayConflict("a/x.bin", "binary", null, null, null, "a/x.bin", "a/x.bin", 1, 1, false);
        Assert.True(new ConflictMatcher([], ["Binary"]).Matches(bin));
        Assert.False(new ConflictMatcher([], ["content"]).Matches(bin));
        Assert.False(new ConflictMatcher(["b"], ["binary"]).Matches(bin));
        Assert.True(new ConflictMatcher(["a", "b"], ["binary", "content"]).Matches(bin));
    }
}
