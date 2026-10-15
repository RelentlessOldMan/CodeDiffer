using System.Security.Cryptography;
using System.Text;

namespace CodeDiffer.Core.Walk;

/// <summary>
/// The names a file is written under before it is moved into place, never past what a file name may hold: a tree's own
/// name can already be 250 characters, and a suffix on it was a name Windows refuses ("the filename, directory name, or
/// volume label syntax is incorrect"), or a Samba share on ext4 or XFS ("file name too long").
/// </summary>
public static class TempNames
{
    /// <summary>The longest file name: NTFS and ReFS take 255 UTF-16 code units, a Samba share on ext4 or XFS 255 bytes
    /// of UTF-8 (80 CJK characters are 240). A name is held to both (<see cref="Fits"/>).</summary>
    public const int MaxName = 255;

    /// <summary>Whether <paramref name="name"/> is a name every file system the trees are on takes: at most
    /// <see cref="MaxName"/> UTF-16 code units and as many bytes of UTF-8.</summary>
    public static bool Fits(string name) => name.Length <= MaxName && Encoding.UTF8.GetByteCount(name) <= MaxName;

    /// <summary><paramref name="path"/> + <paramref name="suffix"/>, the same name every time (a killed run's leftover is
    /// found and reused), or, where that is too long a name, one made from the name's hash beside it.</summary>
    public static string Beside(string path, string suffix)
    {
        var name = Path.GetFileName(path);
        if (Fits(name + suffix)) return path + suffix;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16].ToLowerInvariant();
        return Path.Combine(Path.GetDirectoryName(path) ?? "", "." + hash + suffix);
    }

    /// <summary>A name beside <paramref name="path"/> no other writer uses: short whatever the name.</summary>
    public static string Unique(string path) => Path.Combine(Path.GetDirectoryName(path) ?? "", $".{Guid.NewGuid():N}.tmp");

    /// <summary><paramref name="name"/> cut so that with <paramref name="reserve"/> more ASCII characters it still
    /// <see cref="Fits"/>.</summary>
    public static string Fit(string name, int reserve)
    {
        int room = Math.Max(0, MaxName - reserve), keep = Math.Min(name.Length, room);
        while (keep > 0)
        {
            if (char.IsHighSurrogate(name[keep - 1])) keep--; // never half a character
            else if (Encoding.UTF8.GetByteCount(name.AsSpan(0, keep)) > room) keep--;
            else break;
        }
        return name[..keep];
    }
}
