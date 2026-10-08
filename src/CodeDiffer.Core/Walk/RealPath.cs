using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeDiffer.Core.Walk;

/// <summary>
/// A path as the file system finally resolves it: junctions, symlinks, subst drives and mapped drives followed, so two
/// names of one directory compare equal. Only the deepest part that exists is resolved; the rest is appended as given.
/// </summary>
internal static class RealPath
{
    /// <summary>The resolved full path, or the plain full path when it can't be resolved (off Windows, no access).</summary>
    public static string Of(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows()) return full;
        var tail = new Stack<string>();
        for (var dir = full; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir) && Final(dir) is { } real)
            {
                foreach (var part in tail) real = Path.Combine(real, part);
                return Path.TrimEndingDirectorySeparator(real);
            }
            var name = Path.GetFileName(dir);
            if (name.Length == 0) break; // a root that doesn't resolve
            tail.Push(name);
        }
        return full;
    }

    private static string? Final(string dir)
    {
        using var h = CreateFileW(dir, 0, 7 /* share read|write|delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS: open a directory */, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var buf = new char[1024];
        for (int pass = 0; pass < 2; pass++)
        {
            uint n = GetFinalPathNameByHandleW(h, buf, (uint)buf.Length, 0 /* VOLUME_NAME_DOS */);
            if (n == 0) return null;
            if (n < buf.Length)
            {
                var s = new string(buf, 0, (int)n);
                if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s[8..];
                return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
            }
            buf = new char[n + 1];
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, [Out] char[] buf, uint size, uint flags);
}
