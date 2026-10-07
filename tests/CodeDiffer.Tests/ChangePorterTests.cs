using System.Text;
using CodeDiffer.Core.Compare;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Port;
using CodeDiffer.Core.Sessions;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// apply_changeset: port L→R onto a target C by diff3. Oracles: onto an exact copy of L it must reproduce R
/// byte for byte; onto R itself everything is "already"; a conflicting target file is never touched.
/// </summary>
public class ChangePorterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cd-port-" + Guid.NewGuid().ToString("N"));
    private string L => Path.Combine(_dir, "L");
    private string R => Path.Combine(_dir, "R");
    private string C => Path.Combine(_dir, "C");

    public ChangePorterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Put(string rel, string text) => PutBytes(rel, new UTF8Encoding(false).GetBytes(text));

    private void PutBytes(string rel, byte[] bytes)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_dir, rel));

    private static string Lines(int from, int to, string prefix = "line") =>
        string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"{prefix} {i}\n"));

    private PortResult Port(bool write)
    {
        var report = new DirectoryComparer(new CompareOptions { Cache = CacheMode.Off }).Compare(L, R);
        return ChangePorter.Run(report, L, R, C, write);
    }

    private PortFile F(PortResult r, string path) => r.Files.Single(f => f.Path == path);

    [Fact]
    public void ShiftedTarget_AppliesFuzzy_AndKeepsTargetsOwnEdits()
    {
        Put("L/a.c", Lines(1, 30));
        Put("R/a.c", Lines(1, 20) + "CHANGED 21\n" + Lines(22, 30));
        Put("C/a.c", "header 1\nheader 2\n" + Lines(1, 30)); // target inserted 2 lines at the top

        var dry = Port(write: false);
        var f = F(dry, "a.c");
        Assert.Equal(PortStatus.Clean, f.Status);
        Assert.Equal(HunkOutcome.Fuzzy, Assert.Single(f.Hunks).Outcome);
        Assert.Equal(2, f.Hunks[0].Offset);
        Assert.Equal("header 1\nheader 2\n" + Lines(1, 30), Read("C/a.c")); // dry run wrote nothing

        Port(write: true);
        Assert.Equal("header 1\nheader 2\n" + Lines(1, 20) + "CHANGED 21\n" + Lines(22, 30), Read("C/a.c"));
    }

    [Fact]
    public void ConflictingTarget_IsReported_AndLeftUntouched()
    {
        Put("L/a.c", Lines(1, 30));
        Put("R/a.c", Lines(1, 9) + "OURS\n" + Lines(11, 25) + "fine\n" + Lines(27, 30));
        var theirs = Lines(1, 9) + "THEIRS\n" + Lines(11, 30);
        Put("C/a.c", theirs);

        var r = Port(write: true);
        var f = F(r, "a.c");
        Assert.Equal(PortStatus.Conflict, f.Status);
        Assert.Contains(f.Hunks, h => h.Outcome == HunkOutcome.Conflict && h.BaseLine == 10);
        Assert.Contains(f.Hunks, h => h.Outcome == HunkOutcome.Applied && h.BaseLine == 26);
        Assert.Equal(theirs, Read("C/a.c")); // all-or-nothing per file
    }

    [Fact]
    public void FileLevel_AddRemoveRename_AndAlready()
    {
        Put("L/keep.c", Lines(1, 5));          Put("R/keep.c", Lines(1, 4) + "five\n");   Put("C/keep.c", Lines(1, 4) + "five\n"); // already
        Put("R/new.c", "fresh\n");                                                       // added -> create
        Put("L/old.c", "bye\n");                                                          Put("C/old.c", "bye\n");      // removed -> delete
        Put("L/edited-old.c", "x\n");                                                     Put("C/edited-old.c", "y\n"); // removed, but C changed
        Put("L/sub/m.c", Lines(1, 40, "mv"));  Put("R/sub/n.c", Lines(1, 39, "mv") + "tail\n");
        Put("C/sub/m.c", "top\n" + Lines(1, 40, "mv"));                                    // rename+edit onto shifted C

        var r = Port(write: true);
        Assert.Equal(PortStatus.Already, F(r, "keep.c").Status);
        Assert.Equal("create", F(r, "new.c").Action);
        Assert.Equal("fresh\n", Read("C/new.c"));
        Assert.Equal("delete", F(r, "old.c").Action);
        Assert.False(File.Exists(Path.Combine(C, "old.c")));
        Assert.Equal(PortStatus.Conflict, F(r, "edited-old.c").Status);
        Assert.True(File.Exists(Path.Combine(C, "edited-old.c")));
        Assert.Equal("rename", F(r, "sub/n.c").Action);
        Assert.False(File.Exists(Path.Combine(C, "sub", "m.c")));
        Assert.Equal("top\n" + Lines(1, 39, "mv") + "tail\n", Read("C/sub/n.c"));
    }

    [Fact]
    public void CrlfTarget_MergesAcrossLineEndings_AndKeepsCrlf()
    {
        Put("L/w.txt", "a\nb\nc\nd\n");
        Put("R/w.txt", "a\nB\nc\nd\n");
        Put("C/w.txt", "a\r\nb\r\nc\r\nd\r\nextra\r\n");
        var r = Port(write: true);
        Assert.Equal(PortStatus.Clean, F(r, "w.txt").Status);
        Assert.Equal("a\r\nB\r\nc\r\nd\r\nextra\r\n", Read("C/w.txt"));
    }

    [Fact]
    public void FinalNewline_AndBom_AndBinary()
    {
        Put("L/eof.txt", "a\nb");             Put("R/eof.txt", "a\nb\n");       Put("C/eof.txt", "z\na\nb");
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        PutBytes("L/bom.txt", [.. bom, .. "x\ny\n"u8]); PutBytes("R/bom.txt", [.. bom, .. "x\nY\n"u8]); PutBytes("C/bom.txt", [.. bom, .. "w\nx\ny\n"u8]);
        PutBytes("L/bin.dat", [0, 1, 2, 3]);  PutBytes("R/bin.dat", [0, 1, 9, 3]); PutBytes("C/bin.dat", [0, 1, 2, 3]);
        PutBytes("L/bin2.dat", [0, 1, 2, 3]); PutBytes("R/bin2.dat", [0, 1, 9, 3]); PutBytes("C/bin2.dat", [0, 7, 2, 3]);

        var r = Port(write: true);
        Assert.Equal("z\na\nb\n", Read("C/eof.txt"));
        Assert.Equal([.. bom, .. "w\nx\nY\n"u8], File.ReadAllBytes(Path.Combine(C, "bom.txt")));
        Assert.Equal(new byte[] { 0, 1, 9, 3 }, File.ReadAllBytes(Path.Combine(C, "bin.dat")));
        Assert.Equal(PortStatus.Conflict, F(r, "bin2.dat").Status);
        Assert.Equal(new byte[] { 0, 7, 2, 3 }, File.ReadAllBytes(Path.Combine(C, "bin2.dat")));
    }

    [Fact]
    public void OntoCopyOfLeft_ReproducesRightExactly_OntoRight_AllAlready()
    {
        var rng = new Random(99);
        for (int f = 0; f < 60; f++)
        {
            var a = Enumerable.Range(0, rng.Next(0, 60)).Select(_ => $"L{rng.Next(8)}").ToList(); // heavy duplication
            var b = a.ToList();
            for (int e = rng.Next(0, 10); e > 0; e--)
            {
                int at = rng.Next(b.Count + 1);
                switch (rng.Next(3))
                {
                    case 0: b.Insert(at, $"N{rng.Next(8)}"); break;
                    case 1: if (b.Count > 0) b.RemoveAt(Math.Min(at, b.Count - 1)); break;
                    default: if (b.Count > 0) b[Math.Min(at, b.Count - 1)] = $"R{rng.Next(8)}"; break;
                }
            }
            string ta = string.Join("\n", a) + (rng.Next(2) == 0 ? "\n" : "");
            string tb = string.Join("\n", b) + (rng.Next(2) == 0 ? "\n" : "");
            Put($"L/d{f % 4}/f{f}.txt", ta);
            Put($"R/d{f % 4}/f{f}.txt", tb);
            Put($"C/d{f % 4}/f{f}.txt", ta);
        }
        Put("R/added.txt", "new\n");

        var r = Port(write: true);
        Assert.Equal(0, r.Count(PortStatus.Conflict));
        foreach (var file in Directory.GetFiles(R, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(R, file);
            Assert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(C, rel))), rel);
        }

        var again = Port(write: false); // C now equals R: nothing left to do
        Assert.All(again.Files, f => Assert.Equal(PortStatus.Already, f.Status));
    }

    [Fact]
    public void Cancel_StopsBetweenFiles_AndRunningAgainFinishes()
    {
        for (int i = 0; i < 20; i++)
        {
            Put($"L/f{i:00}.c", Lines(1, 10));
            Put($"R/f{i:00}.c", Lines(1, 9) + $"ten {i}\n");
            Put($"C/f{i:00}.c", Lines(1, 10));
        }
        var report = new DirectoryComparer(new CompareOptions { Cache = CacheMode.Off }).Compare(L, R);
        using var stop = new CancellationTokenSource();
        var progress = new PortProgress { AfterFile = n => { if (n == 5) stop.Cancel(); } };

        var r = ChangePorter.Run(report, L, R, C, write: true, parallelism: 1, ct: stop.Token, progress: progress);
        Assert.True(r.Cancelled);
        Assert.Equal(5, r.Count(PortStatus.Clean));
        Assert.Equal(15, r.Count(PortStatus.NotReached));
        foreach (var f in r.Files) // each file is either fully written or untouched
            Assert.Equal(f.Status == PortStatus.Clean ? File.ReadAllText(Path.Combine(R, f.Path)) : Lines(1, 10), Read("C/" + f.Path));
        var text = AgentViews.PortText(r, "L -> R", null);
        Assert.Contains("CANCELLED part applied", text);
        Assert.Contains("15 not reached", text);
        Assert.Contains("run the same apply again", text);

        var again = ChangePorter.Run(report, L, R, C, write: true, parallelism: 1);
        Assert.False(again.Cancelled);
        Assert.Equal(5, again.Count(PortStatus.Already));
        Assert.Equal(15, again.Count(PortStatus.Clean));
        foreach (var f in again.Files) Assert.Equal(File.ReadAllText(Path.Combine(R, f.Path)), Read("C/" + f.Path));
    }

    [Fact]
    public void ARenameStoppedHalfway_IsFinishedByTheNextRun()
    {
        Put("L/sub/m.c", Lines(1, 40, "mv"));
        Put("R/sub/n.c", Lines(1, 39, "mv") + "tail\n");
        Put("C/sub/m.c", "top\n" + Lines(1, 40, "mv"));
        Port(write: true);
        Put("C/sub/m.c", "top\n" + Lines(1, 40, "mv")); // as if killed after writing n.c, before deleting m.c

        var dry = F(Port(write: false), "sub/n.c");
        Assert.Equal(PortStatus.Clean, dry.Status);
        Assert.Contains("finishes an interrupted rename", dry.Note);
        Assert.True(File.Exists(Path.Combine(C, "sub", "m.c")));

        Port(write: true);
        Assert.False(File.Exists(Path.Combine(C, "sub", "m.c")));
        Assert.Equal("top\n" + Lines(1, 39, "mv") + "tail\n", Read("C/sub/n.c"));
    }

    [Fact]
    public void ADifferentFileAtTheRenameTarget_IsStillAConflict()
    {
        Put("L/m.c", Lines(1, 40, "mv"));
        Put("R/n.c", Lines(1, 39, "mv") + "tail\n");
        Put("C/m.c", Lines(1, 40, "mv"));
        Put("C/n.c", "someone else's n.c\n");
        var f = F(Port(write: true), "n.c");
        Assert.Equal(PortStatus.Conflict, f.Status);
        Assert.Equal(Lines(1, 40, "mv"), Read("C/m.c"));
        Assert.Equal("someone else's n.c\n", Read("C/n.c"));
    }

    [Fact]
    public void AKilledRunsTempFile_IsReusedAndGone()
    {
        Put("L/a.c", Lines(1, 10));
        Put("R/a.c", Lines(1, 9) + "ten\n");
        Put("C/a.c", Lines(1, 10));
        Put("C/a.c" + ChangePorter.TempSuffix, "half a fi"); // what a run killed mid-write leaves
        Port(write: true);
        Assert.Equal(Lines(1, 9) + "ten\n", Read("C/a.c"));
        Assert.Equal(["a.c"], Directory.GetFiles(C).Select(Path.GetFileName));
    }

    [Fact]
    public void ACaseOnlyRename_NeverDeletesTheFile()
    {
        Put("L/Name.c", Lines(1, 20));
        Put("R/name.c", Lines(1, 20));
        Put("C/Name.c", Lines(1, 20));
        Port(write: true);
        var left = Assert.Single(Directory.GetFiles(C));
        Assert.Equal(Lines(1, 20), File.ReadAllText(left));
    }

    [Fact]
    public void AgentView_ReportsTotals_AndDryRunHint()
    {
        Put("L/a.c", Lines(1, 10));
        Put("R/a.c", Lines(1, 9) + "ten\n");
        Put("C/a.c", Lines(1, 10));
        Environment.SetEnvironmentVariable("CODEDIFFER_OUT_DIR", Path.Combine(_dir, "out"));
        try
        {
            var store = new SessionStore(save: false);
            var s = store.Start(L, R, new CompareOptions { Cache = CacheMode.Off });
            Assert.True(s.Wait(TimeSpan.FromSeconds(30)));
            var text = AgentViews.Apply(s, C, write: false);
            Assert.Contains("dry run", text);
            Assert.Contains("1 would apply", text);
            Assert.Contains("call again with write=true", text);
            Assert.Equal(Lines(1, 10), Read("C/a.c"));
            Assert.Contains("APPLIED", AgentViews.Apply(s, C, write: true));
            Assert.Equal(Lines(1, 9) + "ten\n", Read("C/a.c"));
        }
        finally { Environment.SetEnvironmentVariable("CODEDIFFER_OUT_DIR", null); }
    }
}
