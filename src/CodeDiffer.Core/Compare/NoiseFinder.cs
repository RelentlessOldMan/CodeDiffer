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
        if (kinds == ChangeNoise.None) return ChangeNoise.None;
        var (left, right) = (new Stripper(leftText, syntax), new Stripper(rightText, syntax));
        var compared = new List<(string A, string B, bool Same)>();
        bool Same(ChangeNoise take)
        {
            var (a, b) = (left.Strip(take), right.Strip(take));
            foreach (var c in compared)
                if (ReferenceEquals(c.A, a) && ReferenceEquals(c.B, b)) return c.Same;
            bool same = TextInspector.SameButWhitespace(a, b, rules);
            compared.Add((a, b, same));
            return same;
        }
        if (!Same(kinds) || Same(ChangeNoise.None)) return ChangeNoise.None;
        foreach (var set in Sets)
            if ((set & kinds) == set && Same(set)) return set;
        return kinds; // not reached: the whole set was the same
    }

    /// <summary>
    /// <see cref="Strip"/> for one text, each step done once per input: a set whose kinds take nothing out of it (no stamp
    /// in it, say) comes to a text already stripped, and that text and its comparison are reused, not built again (a
    /// 7 MB pair cost some 325 MB, each set's texts built anew).
    /// </summary>
    private sealed class Stripper(string text, CommentSyntax? syntax)
    {
        private string? _comments;
        private readonly Dictionary<string, string> _stamps = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, string> _numbers = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, string> _dropped = new(ReferenceEqualityComparer.Instance);

        public string Strip(ChangeNoise take)
        {
            var t = text;
            if (take.HasFlag(ChangeNoise.Comments) && syntax is not null) t = _comments ??= syntax.Strip(text);
            if (take.HasFlag(ChangeNoise.Stamps)) t = Once(_stamps, t, Stamps);
            if (take.HasFlag(ChangeNoise.Numbers)) t = Once(_numbers, t, Numbers);
            return Once(_dropped, t, DropBlankLines);
        }

        private static string Once(Dictionary<string, string> done, string input, Func<string, string> step)
        {
            if (!done.TryGetValue(input, out var output)) done[input] = output = step(input);
            return output;
        }
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

    /// <summary>
    /// <paramref name="edited"/>, a side's edit of <paramref name="baseLine"/>, with each keyword stamp the other side
    /// changed there (<paramref name="other"/>, which differs from the base only in stamps) carried into it: the other
    /// side's where the edit kept the base's stamp, or where <paramref name="otherWins"/>. Null when the stamps don't
    /// pair up (a stamp the edit added, dropped or swapped for another keyword).
    /// </summary>
    internal static string? CarryStamps(string edited, string baseLine, string other, bool otherWins)
    {
        var mb = Stamp.Matches(baseLine);
        var mo = Stamp.Matches(other);
        var me = Stamp.Matches(edited);
        if (mo.Count != mb.Count || me.Count != mb.Count) return null;
        var s = new System.Text.StringBuilder(edited.Length + 32);
        int at = 0;
        for (int i = 0; i < mb.Count; i++)
        {
            string key = mb[i].Groups[1].Value;
            if (mo[i].Groups[1].Value != key || me[i].Groups[1].Value != key) return null;
            bool theirs = mo[i].Value != mb[i].Value && (otherWins || me[i].Value == mb[i].Value);
            s.Append(edited, at, me[i].Index - at).Append(theirs ? mo[i].Value : me[i].Value);
            at = me[i].Index + me[i].Length;
        }
        return s.Append(edited, at, edited.Length - at).ToString();
    }

    /// <summary>Each number, date, time, version or ID (a word with a digit in it, and any more such words it is joined to
    /// by one <c>. : / -</c>) as <c>#</c>, followed by the letters of its words that aren't hex: <c>v1.2.3</c> is <c>#v</c>,
    /// an ID <c>7f3a9c2</c> is <c>#</c>, and <c>agent07</c> is <c>#agent</c> — so a word swapped for another
    /// (<c>agent07</c> → <c>robot07</c>) is a change, not a number.</summary>
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
            for (int s = i; s < end; s++)
            {
                int e = WordEnd(text, s);
                if (!AllHex(text, s, e))
                    for (int k = s; k < e; k++)
                        if (text[k] is not (>= '0' and <= '9')) o.Append(text[k]);
                s = e; // then past its one separator
            }
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

    private static bool AllHex(string s, int from, int to)
    {
        for (int i = from; i < to; i++)
            if (!char.IsAsciiHexDigit(s[i])) return false;
        return true;
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
