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

    /// <summary>
    /// Is <paramref name="path"/> inside (or the same as) <paramref name="dir"/> by file identity: does a directory on the way up from
    /// its deepest existing part have the volume serial and file id <paramref name="dir"/> has? Catches what no name
    /// resolution does — the same directory reached through a share (\\localhost\C$\..., \\127.0.0.1\c$\..., one server by
    /// two names). False off Windows, or when <paramref name="dir"/> can't be opened.
    /// </summary>
    public static bool InsideByIdentity(string path, string dir)
    {
        if (!OperatingSystem.IsWindows() || Identity(Path.GetFullPath(dir)) is not { } want) return false;
        for (var d = Path.GetFullPath(path); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            if (Directory.Exists(d) && Identity(d) is { } id && id.Same(want)) return true;
        return false;
    }

    /// <summary>A directory's identity: the 32-bit volume serial and 64-bit file index every file system gives, and the
    /// 64-bit serial and 128-bit file id where it gives those too (ReFS ids don't fit 64 bits: two directories could
    /// share an index).</summary>
    internal readonly record struct DirIdentity(uint Volume, ulong File, ulong? Volume64 = null, UInt128? File128 = null)
    {
        /// <summary>One directory: by the 128-bit ids where both have them, else by the 64-bit index.</summary>
        public bool Same(DirIdentity o) => File128 is { } f && o.File128 is { } g
            ? Volume64 == o.Volume64 && f == g
            : Volume == o.Volume && File == o.File;
    }

    /// <summary>A directory's identity, or null when it can't be opened or has none.</summary>
    internal static DirIdentity? Identity(string dir)
    {
        using var h = CreateFileW(Long(dir), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var info)) return null;
        ulong id = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        if (id == 0) return null;
        return GetFileInformationByHandleEx(h, 18 /* FileIdInfo */, out var ex, (uint)Marshal.SizeOf<FileIdInfo>())
               && (ex.IdLow | ex.IdHigh) != 0
            ? new DirIdentity(info.VolumeSerialNumber, id, ex.VolumeSerialNumber, new UInt128(ex.IdHigh, ex.IdLow))
            : new DirIdentity(info.VolumeSerialNumber, id);
    }

    /// <summary>A path CreateFileW opens past MAX_PATH: \\?\ in front of a long one (\\?\UNC\ for a share).</summary>
    internal static string Long(string path)
    {
        if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    private static string? Final(string dir)
    {
        using var h = CreateFileW(Long(dir), 0, 7 /* share read|write|delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
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

    // Native BY_HANDLE_FILE_INFORMATION is 52 bytes: its FILETIMEs are pairs of DWORDs, 4-aligned. Declared as long they
    // would be 8-aligned, shifting every later field by 4 (the volume serial read as the size, the file id cut to 32 bits).
    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, LastAccessLow, LastAccessHigh, LastWriteLow, LastWriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);

    /// <summary>Native FILE_ID_INFO: a 64-bit volume serial and a 128-bit file id (24 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong IdLow, IdHigh;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FileIdInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, [Out] char[] buf, uint size, uint flags);
}
