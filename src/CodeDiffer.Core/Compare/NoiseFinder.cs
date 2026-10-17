using System.Text;
using System.Text.RegularExpressions;
using CodeDiffer.Core.Model;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// Whether a content change is only noise (<see cref="ChangeNoise"/>), and which: each kind that applies to the file is
/// taken out of both texts, and the two compared spacing and blank lines aside. All of them at once first (most changes
/// are real, and one pass says so), then the smallest set that still leaves them the same, stamps before numbers before
/// comments. A change that is only blank lines or spacing once nothing is taken out isn't noise: it's content, as said.
/// </summary>
public static class NoiseFinder
{
    /// <param name="leftText">Both sides decoded and EOL-normalized, as the classifier holds them.</param>
    public static ChangeNoise Find(string leftPath, string rightPath, string leftText, string rightText, StringRules rules)
    {
        var kinds = Applicable(leftPath) & Applicable(rightPath);
        // Comments only when both names read them alike (a rename to another language: not taken out).
        var syntax = CommentSyntax.For(leftPath);
        if (syntax != CommentSyntax.For(rightPath)) kinds &= ~ChangeNoise.Comments;
        if (kinds == ChangeNoise.None || !Same(leftText, rightText, kinds, syntax, rules)) return ChangeNoise.None;
        if (Same(leftText, rightText, ChangeNoise.None, syntax, rules)) return ChangeNoise.None;
        foreach (var set in Sets)
            if ((set & kinds) == set && Same(leftText, rightText, set, syntax, rules)) return set;
        return kinds; // not reached: the whole set was the same
    }

    /// <summary>The kinds that can apply to a file: numbers only to a file that isn't code, comments where its language's
    /// are known (<see cref="CommentSyntax.For"/>).</summary>
    public static ChangeNoise Applicable(string path)
    {
        var type = FileTypes.Of(path);
        if (type == FileType.Binary) return ChangeNoise.None;
        var kinds = ChangeNoise.Stamps;
        if (type != FileType.Code) kinds |= ChangeNoise.Numbers;
        if (CommentSyntax.For(path) is not null) kinds |= ChangeNoise.Comments;
        return kinds;
    }

    /// <summary>Every non-empty set, smallest first, and in each size the earlier kinds first (the flags are in that order).</summary>
    private static readonly ChangeNoise[] Sets = Enumerable.Range(1, (1 << NoiseTokens.All.Length) - 1)
        .OrderBy(int.PopCount).ThenBy(m => m).Select(m => (ChangeNoise)m).ToArray();

    private static bool Same(string a, string b, ChangeNoise take, CommentSyntax? syntax, StringRules rules)
        => TextInspector.SameButWhitespace(Strip(a, take, syntax), Strip(b, take, syntax), rules);

    /// <summary>A text with <paramref name="take"/> taken out, and its blank lines: comments first (a stamp in one goes
    /// with it), then stamps, then numbers.</summary>
    public static string Strip(string text, ChangeNoise take, CommentSyntax? syntax = null)
    {
        if (take.HasFlag(ChangeNoise.Comments) && syntax is not null) text = syntax.Strip(text);
        if (take.HasFlag(ChangeNoise.Stamps)) text = Stamps(text);
        if (take.HasFlag(ChangeNoise.Numbers)) text = Numbers(text);
        return DropBlankLines(text);
    }

    // RCS, CVS, Subversion and Perforce keywords, expanded ("$Date: 2026/10/17 12:00:00 $") or not ("$Date$").
    private static readonly Regex Stamp = new(
        @"\$(Id|Header|Date|DateTime|Author|Revision|Rev|Source|RCSfile|Locker|Name|State|Log|Change|File|LastChangedDate|LastChangedRevision|LastChangedBy|HeadURL|URL)(?::[^$\n]*)?\$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Each keyword stamp as its bare keyword.</summary>
    public static string Stamps(string text) => text.Contains('$') ? Stamp.Replace(text, "$$$1$$") : text;

    /// <summary>Each number, date, time, version or ID (a word with a digit in it, and any more such words it is joined to
    /// by one <c>. : / -</c>) as <c>#</c>.</summary>
    public static string Numbers(string text)
    {
        var o = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (!IsWord(text[i])) { o.Append(text[i++]); continue; }
            int end = WordEnd(text, i);
            if (!HasDigit(text, i, end)) { o.Append(text, i, end - i); i = end; continue; }
            // A digit word, and more joined to it: "1.2.3", "2026-10-17", "12:30:01", "v2/7".
            while (end + 1 < text.Length && text[end] is '.' or ':' or '/' or '-' && IsWord(text[end + 1]))
            {
                int next = WordEnd(text, end + 1);
                if (!HasDigit(text, end + 1, next)) break;
                end = next;
            }
            o.Append('#');
            i = end;
        }
        return o.ToString();
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int WordEnd(string s, int i)
    {
        while (i < s.Length && IsWord(s[i])) i++;
        return i;
    }

    private static bool HasDigit(string s, int from, int to)
    {
        for (int i = from; i < to; i++)
            if (s[i] is >= '0' and <= '9') return true;
        return false;
    }

    private static string DropBlankLines(string text)
    {
        var o = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length;)
        {
            int n = text.IndexOf('\n', i);
            var line = text.AsSpan(i, (n < 0 ? text.Length : n) - i);
            if (!line.IsWhiteSpace()) o.Append(line).Append('\n');
            i = n < 0 ? text.Length : n + 1;
        }
        return o.ToString();
    }
}
