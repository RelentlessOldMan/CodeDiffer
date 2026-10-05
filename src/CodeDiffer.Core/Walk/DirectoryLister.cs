using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeDiffer.Core.Walk;

/// <summary>One directory-listing row.</summary>
internal readonly record struct DirRow(string Name, bool IsDirectory, bool IsReparsePoint, long Length,
    long LastWriteUtcTicks, long ChangeUtcTicks, long FileId);

/// <summary>
/// Lists one directory. On Windows it uses GetFileInformationByHandleEx(FileIdBothDirectoryInfo) — the
/// same single listing round-trip as a normal enumeration (batches of ~64 KB over SMB), but it also returns
/// each entry's ChangeTime and FileId, which .NET's FileSystemInfo doesn't expose. Elsewhere it falls back
/// to the managed enumeration with ChangeTime/FileId = 0 (unknown ⇒ the hash cache won't trust the file).
/// Throws IOException/UnauthorizedAccessException when the directory can't be listed.
/// </summary>
internal static class DirectoryLister
{
    public static List<DirRow> List(string dir)
        => OperatingSystem.IsWindows() ? ListWindows(dir) : ListManaged(dir);

    private const int FileIdBothDirectoryInfo = 10, FileIdBothDirectoryRestartInfo = 11;
    private const uint FileAttributeDirectory = 0x10, FileAttributeReparsePoint = 0x400;
    private const int BufferBytes = 64 * 1024;

    // FILE_ID_BOTH_DIR_INFO field offsets (x86 and x64 identical: all fields are fixed-size).
    private const int OffNext = 0, OffLastWrite = 24, OffChange = 32, OffEndOfFile = 40, OffAttributes = 56,
        OffNameLength = 60, OffFileId = 96, OffName = 104;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, IntPtr buffer, uint size);

    private static List<DirRow> ListWindows(string dir)
    {
        const uint ListDirectory = 0x0001, ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;
        const int ErrorNoMoreFiles = 18, ErrorAccessDenied = 5;

        using var h = CreateFileW(dir, ListDirectory, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw err == ErrorAccessDenied
                ? new UnauthorizedAccessException($"cannot list {dir} (access denied)")
                : new IOException($"cannot list {dir} (win32 error {err})");
        }

        var rows = new List<DirRow>();
        var buf = Marshal.AllocHGlobal(BufferBytes);
        try
        {
            int cls = FileIdBothDirectoryRestartInfo;
            while (true)
            {
                if (!GetFileInformationByHandleEx(h, cls, buf, BufferBytes))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ErrorNoMoreFiles) break;
                    throw new IOException($"listing {dir} failed (win32 error {err})");
                }
                cls = FileIdBothDirectoryInfo;
                int off = 0;
                while (true)
                {
                    var p = buf + off;
                    int next = Marshal.ReadInt32(p, OffNext);
                    int nameBytes = Marshal.ReadInt32(p, OffNameLength);
                    var name = Marshal.PtrToStringUni(p + OffName, nameBytes / 2);
                    if (name is not ("." or ".."))
                    {
                        uint attrs = (uint)Marshal.ReadInt32(p, OffAttributes);
                        rows.Add(new DirRow(
                            name,
                            (attrs & FileAttributeDirectory) != 0,
                            (attrs & FileAttributeReparsePoint) != 0,
                            Marshal.ReadInt64(p, OffEndOfFile),
                            FileTimeToUtcTicks(Marshal.ReadInt64(p, OffLastWrite)),
                            FileTimeToUtcTicks(Marshal.ReadInt64(p, OffChange)),
                            Marshal.ReadInt64(p, OffFileId)));
                    }
                    if (next == 0) break;
                    off += next;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return rows;
    }

    private static List<DirRow> ListManaged(string dir)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts)
            .Select(i => new DirRow(
                i.Name,
                i is DirectoryInfo,
                (i.Attributes & FileAttributes.ReparsePoint) != 0,
                i is FileInfo fi ? fi.Length : 0,
                i.LastWriteTimeUtc.Ticks,
                0, 0))
            .ToList();
    }

    /// <summary>FILETIME (100 ns since 1601 UTC) → DateTime UTC ticks; 0 stays 0 (unknown).</summary>
    internal static long FileTimeToUtcTicks(long fileTime)
        => fileTime <= 0 ? 0 : DateTime.FromFileTimeUtc(fileTime).Ticks;
}
