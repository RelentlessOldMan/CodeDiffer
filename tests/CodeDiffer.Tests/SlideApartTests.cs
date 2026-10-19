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
}
