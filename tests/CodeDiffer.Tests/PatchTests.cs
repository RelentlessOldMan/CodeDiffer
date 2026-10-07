using System.Diagnostics;
using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Diff;
using CodeDiffer.Core.Ledger;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// The patch output is judged by the strictest available oracle: real <c>git apply</c>. A patch generated
/// for L→R, applied to a copy of L, must reproduce R byte for byte — every edge case included (edits at
/// both ends, newline-at-EOF gained/lost, CRLF, unicode, add/remove/empty/rename). Plus rendering details.
/// </summary>
public class PatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-patch-" + Guid.NewGuid().ToString("N"));

    public PatchTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Put(string rel, string text)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(text));
    }

    private static string Lines(int from, int to, string prefix = "line") =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"{prefix} {i}\n"));

    [Fact]
    public void Patch_AppliedByGit_ReproducesRightTreeExactly()
    {
        Put("L/start.txt", Lines(1, 20));            Put("R/start.txt", "new first\n" + Lines(1, 20));
        Put("L/end.txt", Lines(1, 20));              Put("R/end.txt", Lines(1, 18));
        Put("L/middle.txt", Lines(1, 40));           Put("R/middle.txt", Lines(1, 10) + "changed A\nchanged B\n" + Lines(13, 30) + Lines(33, 40));
        Put("L/gain-eol.txt", "a\nb\nc");            Put("R/gain-eol.txt", "a\nb\nc\n");
        Put("L/lose-eol.txt", "a\nb\nc\n");          Put("R/lose-eol.txt", "a\nb\nc");
        Put("L/noeol-edit.txt", "a\nb\nc");          Put("R/noeol-edit.txt", "a\nB\nc");
        Put("L/crlf.txt", "one\r\ntwo\r\nthree\r\n"); Put("R/crlf.txt", "one\r\nTWO\r\nthree\r\n");
        Put("L/unicode.txt", "hÃ©llo\næ—¥æœ¬èªž\nend\n");  Put("R/unicode.txt", "hÃ©llo\næ—¥æœ¬èªžã§ã™\nend\n");
        Put("L/removed.txt", Lines(1, 5));
        Put("R/added.txt", Lines(1, 5, "fresh"));
        Put("R/added-noeol.txt", "x\ny");
        Put("L/sub/moved.txt", Lines(1, 30, "moving"));  Put("R/sub/renamed.txt", Lines(1, 29, "moving") + "tweaked\n");
        Put("L/same.txt", "identical\n");            Put("R/same.txt", "identical\n");

        var left = Path.Combine(_dir, "L");
        var right = Path.Combine(_dir, "R");
        var report = new DirectoryComparer(new CompareOptions { Cache = CacheMode.Off }).Compare(left, right);
        var sw = new StringWriter { NewLine = "\n" };
        var stats = PatchWriter.Write(sw, report, left, right, new PatchOptions { Literal = true });
        Assert.Equal(0, stats.BinaryFiles + stats.GiantFiles);
        var patchText = sw.ToString();
        Assert.Contains("rename from sub/moved.txt", patchText);
        Assert.Contains("\\ No newline at end of file", patchText);

        // Apply to a copy of L with real git, outside any repo.
        var work = Path.Combine(_dir, "work");
        CopyTree(left, work);
        var patchFile = Path.Combine(_dir, "p.patch");
        File.WriteAllBytes(patchFile, new UTF8Encoding(false).GetBytes(patchText));
        var (code, output) = Git(work, "-c", "core.autocrlf=false", "apply", "--whitespace=nowarn", patchFile);
        Assert.True(code == 0, $"git apply failed ({code}): {output}\n--- patch ---\n{patchText}");

        var want = Directory.GetFiles(right, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(right, f)).OrderBy(x => x).ToList();
        var got = Directory.GetFiles(work, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(work, f)).OrderBy(x => x).ToList();
        Assert.Equal(want, got);
        foreach (var rel in want)
            Assert.True(File.ReadAllBytes(Path.Combine(right, rel)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(work, rel))),
                $"{rel} differs after git apply");
    }

    private void PutBytes(string rel, byte[] bytes)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    [Fact]
    public void DefaultPatch_WithWhatItCannotCarry_StillAppliesByGit()
    {
        // Carried: a UTF-8 BOM file edited on line 1, a pure rename of a binary file, plain text.
        PutBytes("L/bom.txt", [0xEF, 0xBB, 0xBF, .. "first\nsecond\n"u8]);
        PutBytes("R/bom.txt", [0xEF, 0xBB, 0xBF, .. "FIRST\nsecond\n"u8]);
        PutBytes("L/img/a.bin", [0, 1, 2, 3, 4, 5]);
        PutBytes("R/img/b.bin", [0, 1, 2, 3, 4, 5]);
        Put("L/plain.txt", Lines(1, 5));
        Put("R/plain.txt", Lines(1, 4) + "five\n");
        // Not carried (described in '#' lines): eol-only, a changed binary, cp1252 text, UTF-16 text.
        Put("L/eol.txt", "a\nb\n");
        Put("R/eol.txt", "a\r\nb\r\n");
        PutBytes("L/blob.dat", [0, 9, 9]);
        PutBytes("R/blob.dat", [0, 8, 8, 8]);
        PutBytes("L/legacy.c", [.. "t = 25"u8, 0xB0, (byte)'\n']);
        PutBytes("R/legacy.c", [.. "t = 26"u8, 0xB0, (byte)'\n']);
        PutBytes("L/wide.txt", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("one\n")]);
        PutBytes("R/wide.txt", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("two\n")]);

        var left = Path.Combine(_dir, "L");
        var right = Path.Combine(_dir, "R");
        var report = new DirectoryComparer(new CompareOptions { Cache = CacheMode.Off }).Compare(left, right);
        var sw = new StringWriter { NewLine = "\n" };
        var stats = PatchWriter.Write(sw, report, left, right);
        var patchText = sw.ToString();
        Assert.Equal((1, 1, 2, 3), (stats.NoteFiles, stats.BinaryFiles, stats.OtherFiles, stats.NotCarried));
        Assert.Contains("# Line endings differ: a/eol.txt", patchText);
        Assert.Contains("# Binary files a/blob.dat and b/blob.dat differ", patchText);
        Assert.Contains("# legacy.c: text that is not UTF-8", patchText);
        Assert.Contains("NOT CARRIED", stats.Summary(literal: false));

        var work = Path.Combine(_dir, "work");
        CopyTree(left, work);
        var patchFile = Path.Combine(_dir, "p.patch");
        File.WriteAllBytes(patchFile, new UTF8Encoding(false).GetBytes(patchText));
        var (code, output) = Git(work, "-c", "core.autocrlf=false", "apply", "--whitespace=nowarn", patchFile);
        Assert.True(code == 0, $"git apply failed ({code}): {output}\n--- patch ---\n{patchText}");
        foreach (var rel in new[] { "bom.txt", "img/b.bin", "plain.txt" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(right, rel)), File.ReadAllBytes(Path.Combine(work, rel)));
        Assert.False(File.Exists(Path.Combine(work, "img", "a.bin")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(left, "legacy.c")), File.ReadAllBytes(Path.Combine(work, "legacy.c"))); // untouched
    }

    [Fact]
    public void Rendering_MergesNearbyEdits_AndUsesGitRangeForm()
    {
        var w = new StringWriter { NewLine = "\n" };
        UnifiedDiff.Write(w, "f", "f", Lines(1, 30), Lines(1, 9) + "X\n" + Lines(11, 14) + "Y\n" + Lines(16, 30), context: 3);
        var text = w.ToString();
        // Edits at 10 and 15 are 4 lines apart (≤ 2×context) ⇒ ONE hunk covering 7..18.
        Assert.Single(text.Split('\n').Where(l => l.StartsWith("@@")));
        Assert.Contains("@@ -7,12 +7,12 @@", text);
    }

    [Fact]
    public void Rendering_FarApartEdits_AreSeparateHunks_AndSingleLineRangeOmitsCount()
    {
        var w = new StringWriter { NewLine = "\n" };
        UnifiedDiff.Write(w, "f", "f", "only\n", "changed\n", context: 3);
        Assert.Contains("@@ -1 +1 @@", w.ToString());

        w = new StringWriter { NewLine = "\n" };
        UnifiedDiff.Write(w, "f", "f", Lines(1, 50), "X\n" + Lines(2, 49) + "Y\n", context: 3);
        Assert.Equal(2, w.ToString().Split('\n').Count(l => l.StartsWith("@@")));
    }

    [Fact]
    public void IdenticalTexts_WriteNothing()
    {
        var w = new StringWriter();
        UnifiedDiff.Write(w, "f", "f", "a\nb\n", "a\nb\n");
        Assert.Equal("", w.ToString());
    }

    [Fact]
    public void EditDistanceBudget_FallsBackToOneCorrectHunk()
    {
        var a = Enumerable.Range(0, 200).Select(i => $"a{i}").ToArray();
        var b = Enumerable.Range(0, 200).Select(i => i is < 5 or >= 195 ? $"a{i}" : $"b{i}").ToArray();
        var hunks = LineDiffer.Diff(a, b, maxEditDistance: 10, out bool coarse, maxWork: 100);
        Assert.True(coarse);
        Assert.Single(hunks);
        Assert.Equal(b, HunkApplier.Reconstruct(a, b, hunks)); // still a correct diff
        LineDiffer.Diff(a, b, maxEditDistance: 1000, out coarse);
        Assert.False(coarse);
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }

    private static (int, string) Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        // Never let git discover an enclosing repository (e.g. a home directory under version control):
        // inside one, `git apply` resolves paths from that repo's root and silently skips ours.
        psi.Environment["GIT_CEILING_DIRECTORIES"] = Path.GetDirectoryName(Path.GetFullPath(cwd));
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}
