namespace CodeDiffer.Core.Model;

/// <summary>
/// What a <see cref="ChangeReason.Content"/> change turned out to be only, when it is noise: the smallest set of these
/// that, taken out of both sides, leaves them the same (spacing aside). A view, not a verdict: the reason stays
/// <c>content</c> (the delta contract's token), and nothing here is in the digest. None: a change to what the file says.
/// Found only for a pair read whole (up to the classify size); a larger one is never called noise.
/// </summary>
[Flags]
public enum ChangeNoise
{
    None = 0,
    /// <summary>Version-control keyword stamps (<c>$Id: … $</c>, <c>$Date: … $</c>, <c>$Revision: … $</c>…) expanded otherwise.</summary>
    Stamps = 1,
    /// <summary>In a file that isn't code: numbers, dates and IDs (a word with a digit in it) changed, and nothing else.</summary>
    Numbers = 2,
    /// <summary>Comments changed, added or taken out, in a language whose comments are known.</summary>
    Comments = 4,
}

/// <summary>
/// The noise a listing leaves out: content changes that are only <see cref="Kinds"/> (a file whose change is stamps and
/// comments goes only when both are left out), and modified files whose reason is in <see cref="Reasons"/> (whitespace,
/// eol, encoding: the reasons that are noise of their own).
/// </summary>
public sealed record NoiseFilter(ChangeNoise Kinds, IReadOnlySet<ChangeReason> Reasons)
{
    public static readonly ChangeReason[] NoiseReasons = [ChangeReason.Whitespace, ChangeReason.Eol, ChangeReason.Encoding];

    public static NoiseFilter AllNoise { get; } = new(ChangeNoise.Stamps | ChangeNoise.Numbers | ChangeNoise.Comments, NoiseReasons.ToHashSet());

    public bool Hides(FileChange c) => c.Status == ChangeStatus.Modified && c.Reason is { } r
        && (Reasons.Contains(r) || r == ChangeReason.Content && c.Noise != ChangeNoise.None && (c.Noise & ~Kinds) == ChangeNoise.None);

    /// <summary>Every noise token, in the order they're listed: the kinds, then the reasons.</summary>
    public static readonly string[] Tokens = [.. NoiseTokens.All.Select(NoiseTokens.Token), .. NoiseReasons.Select(CanonicalTokens.Token)];

    /// <summary>The noise a change is only, as tokens (its kinds, or its whitespace/eol/encoding reason); null when it is
    /// a real change (or not a modification).</summary>
    public static string[]? Kind(FileChange? c)
        => c is not { Status: ChangeStatus.Modified, Reason: { } r } ? null
            : NoiseReasons.Contains(r) ? [CanonicalTokens.Token(r)]
            : r == ChangeReason.Content && NoiseTokens.List(c.Noise) is { } l ? l.Split(',') : null;

    public const string Help = "all, or a comma list of stamps, numbers, comments, whitespace, eol, encoding";

    /// <summary>"all" or a comma list of noise kinds and noise reasons; null (hide nothing) when none is given.</summary>
    public static bool TryParse(string? list, out NoiseFilter? filter, out string error)
    {
        filter = null;
        error = "";
        if (string.IsNullOrWhiteSpace(list) || list.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        if (list.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) { filter = AllNoise; return true; }
        var kinds = ChangeNoise.None;
        var reasons = new HashSet<ChangeReason>();
        foreach (var part in list.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (NoiseTokens.TryParse(part, out var k)) kinds |= k;
            else if (NoiseReason(part) is { } r) reasons.Add(r);
            else { error = $"unknown noise '{part}' (use {Help})"; return false; }
        }
        filter = new NoiseFilter(kinds, reasons);
        return true;
    }

    private static ChangeReason? NoiseReason(string token)
    {
        foreach (var r in NoiseReasons)
            if (CanonicalTokens.Token(r).Equals(token, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    /// <summary>"stamps 12 · numbers 3 · comments 40": content changes that are only that kind (one with two kinds
    /// counts in both).</summary>
    public static string Line(IEnumerable<FileChange> changes)
    {
        var n = new int[NoiseTokens.All.Length];
        foreach (var c in changes)
            for (int i = 0; i < n.Length; i++)
                if (c.Noise.HasFlag(NoiseTokens.All[i])) n[i]++;
        return string.Join(" · ", NoiseTokens.All.Select((k, i) => $"{NoiseTokens.Token(k)} {n[i]:N0}"));
    }
}

public static class NoiseTokens
{
    /// <summary>The kinds, in the order they're listed and tried.</summary>
    public static readonly ChangeNoise[] All = [ChangeNoise.Stamps, ChangeNoise.Numbers, ChangeNoise.Comments];

    public static string Token(ChangeNoise n) => n switch
    {
        ChangeNoise.Stamps => "stamps",
        ChangeNoise.Numbers => "numbers",
        ChangeNoise.Comments => "comments",
        _ => throw new ArgumentOutOfRangeException(nameof(n), n, null),
    };

    /// <summary>A set as a comma list ("stamps,comments"), or null for none.</summary>
    public static string? List(ChangeNoise n) => n == ChangeNoise.None ? null : string.Join(",", All.Where(k => n.HasFlag(k)).Select(Token));

    public static bool TryParse(string token, out ChangeNoise kind)
    {
        foreach (var k in All)
            if (string.Equals(Token(k), token.Trim(), StringComparison.OrdinalIgnoreCase)) { kind = k; return true; }
        kind = ChangeNoise.None;
        return false;
    }

    /// <summary>A comma list read back; a kind not known is left out (a newer build's, read by an older one).</summary>
    public static ChangeNoise ParseList(string? list)
    {
        var n = ChangeNoise.None;
        foreach (var t in (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (TryParse(t, out var k)) n |= k;
        return n;
    }
}
