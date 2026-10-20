using CodeDiffer.Core.Diff;
using Xunit;

namespace CodeDiffer.Tests;

/// <summary>
/// An insert or a cut beside identical lines can stand anywhere along them, and where the differ put it is its choice:
/// put hard against the other side's edit it made a conflict that git (placing it otherwise) merged clean — a function
/// deleted on one side beside the next one's signature edited on the other (git.git's setup.c), a new function added
/// above one whose body the other side edits. It slides apart; where the other side changed a line it could take, it
/// stays a conflict.
/// </summary>
public class SlideApartTests
{
    private static string[] L(string s) => s.Split('\n')[..^1].Select(x => x + "\n").ToArray();

    /// <summary>The merged text, or null on a conflict.</summary>
    private static string? Merged(string b, string v1, string v2)
    {
        string[] lb = L(b), l1 = L(v1), l2 = L(v2);
        var sb = new System.Text.StringBuilder();
        int at = 0;
        foreach (var r in ThreeWayMerger.Regions(lb, l1, l2))
        {
            if (r.Kind == RegionKind.Conflict) return null;
            for (; at < r.BaseStart0; at++) sb.Append(lb[at]);
            var (side, s0, n) = r.Kind == RegionKind.V2Only ? (l2, r.V2Start0, r.V2Lines) : (l1, r.V1Start0, r.V1Lines);
            for (int i = s0; i < s0 + n; i++) sb.Append(side[i]);
            at = r.BaseStart0 + r.BaseLines;
        }
        for (; at < lb.Length; at++) sb.Append(lb[at]);
        return sb.ToString();
    }

    private const string A = "a() {\n\tw;\n}\n\n", F = "f() {\n\tx;\n}\n\n", G = "g(int y,\n    int z)\n{\n\tq;\n}\n";

    [Fact]
    public void ACutFunction_BesideTheNextOnesEditedSignature_MergesClean()
    {
        var v2 = G.Replace("g(int y,", "g(long y,");
        Assert.Equal(A + v2, Merged(A + F + G, A + G, A + F + v2));
    }

    [Fact]
    public void ACutFunction_BesideThePreviousOnesEditedEnd_MergesClean()
    {
        var v2 = A.Replace("\tw;", "\tw2;");
        Assert.Equal(v2 + G, Merged(A + F + G, A + G, v2 + F + G));
    }

    [Fact]
    public void AnAddedFunction_BesideAnEditedSignature_MergesClean()
    {
        // v1 adds f above g; v2 edits g's signature. The added lines can as well stand before a's closing blank.
        var g2 = G.Replace("g(int y,", "g(long y,");
        Assert.Equal(A + F + g2, Merged(A + G, A + F + G, A + g2));
    }

    [Fact]
    public void AnEditToALineTheCutCouldTake_StaysAConflict()
    {
        // f's "}" is a copy of a's: v2 editing it may be editing what v1 cut. Not for the differ to guess.
        var v2 = A + F.Replace("}", "};") + G;
        Assert.Null(Merged(A + F + G, A + G, v2));
    }

    [Fact]
    public void TwoFunctionsAddedAtOnePlace_StayAConflict()
    {
        // Both sides add a near-copy of one function between a and g (git.git's t/lib-gpg.sh): either could slide past
        // the blank to stand apart, and that would keep both copies, in an order the base doesn't give.
        var f1 = "# make it\nf() {\n\tx;\n}\n\n";
        Assert.Null(Merged(A + G, A + f1 + G, A + F.Replace("f()", "f ()") + G));
    }

    [Fact]
    public void ATestRewrittenAboveATestAdded_MergesClean()
    {
        // v1 adds a prereq and rewrites the last two tests; v2 adds a test after them (git.git's t1006-cat-file.sh).
        // Matching v1's three `'`+blank tails to base's two splits the last rewrite into an insert at v2's place; it
        // goes back against v1's rewrite of the test before, one edit, as git writes it.
        const string H = "\nexpect=\"$oid blob\"\n\n", Done = "test_done\n";
        const string Added = "test_expect_success 'setup' '\n\tgit init repo\n'\n\nrun_filter \"type=tree\"\n\n";
        string Old(string o) => $"test_expect_success PERL '--{o} is unbuffered' '\n\tperl -e \"$script\" -- --{o} $oid\n'\n\n";
        string New(string o) => $"test_expect_success PERL_OPEN2 '--{o} is unbuffered' '\n\tperl -e \"$perl_script\" -- --{o} $oid\n'\n\n";
        const string Pre = "test_lazy_prereq PERL_OPEN2 '\n\tperl -MOpen2 -e \"exit 0\"\n'\n\n";
        var v1 = H + Pre + New("check") + New("command") + Done;
        var v2 = H + Old("check") + Old("command") + Added + Done;
        Assert.Equal(H + Pre + New("check") + New("command") + Added + Done,
            Merged(H + Old("check") + Old("command") + Done, v1, v2));
    }

    [Fact]
    public void AnInsertTheLinearSpaceDifferPutsHigh_GoesDownToItsOwnEdit_MergesClean()
    {
        // Both cut the first `}`; v1 adds one at the end with more lines past the exact differ's budget, v2 adds an `x;`.
        // The linear-space differ, searching from the end too, puts v1's new `}` before base's last, at v2's place; it
        // goes down against v1's lines added after, one edit (git, whose differ made it two, calls it a conflict).
        // Forward Myers never puts an insert where it could go down: it takes the match first.
        var tail = string.Concat(Enumerable.Range(0, LineDiffer.DefaultMaxEditDistance + 1).Select(i => $"more({i});\n"));
        Assert.Equal("x;\nx;\n}\n}\n" + tail, Merged("}\nx;\n}\n", "x;\n}\n}\n" + tail, "x;\nx;\n}\n"));
    }
}
